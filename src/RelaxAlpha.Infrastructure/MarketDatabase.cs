using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Infrastructure;

public abstract class MarketDatabase(DbContextOptions options) : DbContext(options)
{
    public DbSet<ImportBatch> Batches => Set<ImportBatch>();
    public DbSet<OptionQuote> Quotes => Set<OptionQuote>();
    public DbSet<OfficialDailyImport> DailyImports => Set<OfficialDailyImport>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OfficialDailyImport>(daily =>
        {
            daily.HasKey(x => new { x.Source, x.TradingDate });
            daily.Property(x => x.Source).HasMaxLength(16);
            daily.Property(x => x.Failure).HasMaxLength(300);
        });
        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.HasIndex(x => new { x.Kind, x.Source, x.ObservedAtUtc, x.ContentHash }).IsUnique();
            batch.Property(x => x.Source).HasMaxLength(160);
            batch.Property(x => x.ContentHash).HasMaxLength(64);
            batch.HasIndex(x => x.TradingDate);
        });
        modelBuilder.Entity<OptionQuote>(quote =>
        {
            quote.HasIndex(x => new { x.Exchange, x.Symbol, x.BatchId }).IsUnique();
            quote.Property(x => x.Exchange).HasMaxLength(8);
            quote.Property(x => x.Symbol).HasMaxLength(48);
            quote.Property(x => x.Underlying).HasMaxLength(48);
            quote.HasOne(x => x.Batch).WithMany(x => x.Quotes).HasForeignKey(x => x.BatchId);
        });
    }
}

// SQLite materializes timestamps without a kind. All market timestamps are stored in UTC.
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

public sealed class SqliteMarketDatabase(DbContextOptions<SqliteMarketDatabase> options) : MarketDatabase(options);
public sealed class PostgresMarketDatabase(DbContextOptions<PostgresMarketDatabase> options) : MarketDatabase(options);

public sealed class EfMarketStore(MarketDatabase db) : IMarketStore
{
    public async Task<BatchSummary> SaveAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        var matching = db.Batches.AsNoTracking().Where(x => x.Kind == batch.Kind && x.Source == batch.Source &&
            x.ObservedAtUtc == batch.ObservedAtUtc && x.ContentHash == batch.ContentHash);
        var existing = await matching.Select(Summary).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        db.Batches.Add(batch);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent upload of the exact same snapshot may have won the unique index.
            existing = await matching.Select(Summary).SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            db.ChangeTracker.Clear();
            return existing;
        }

        return new(batch.Id, batch.Source, batch.Kind, batch.ObservedAtUtc, batch.ImportedAtUtc, batch.Quotes.Count, batch.TradingDate);
    }

    public async Task<IReadOnlyList<BatchSummary>> SourcesAsync(CancellationToken cancellationToken) =>
        await db.Batches.AsNoTracking().OrderByDescending(x => x.ImportedAtUtc).ThenBy(x => x.Id).Take(100)
            .Select(Summary).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OptionQuote>> LatestAsync(
        MarketUniverse universe, DateTime asOf, DateOnly? tradingDate, CancellationToken cancellationToken)
    {
        var quotes = db.Quotes.AsNoTracking();
        if (universe == MarketUniverse.OfficialDaily)
        {
            if (tradingDate is null) return [];
            return await quotes.Where(q => q.Batch.TradingDate == tradingDate && q.ExpiryUtc > asOf)
                .Include(q => q.Batch).OrderBy(q => q.Exchange).ThenBy(q => q.Symbol).ToListAsync(cancellationToken);
        }

        var simulated = universe == MarketUniverse.Simulated;
        quotes = quotes.Where(q => q.Batch.TradingDate == null && (q.Batch.Kind == SourceKind.Simulated) == simulated);
        return await quotes.Where(q => q.ExpiryUtc > asOf && !quotes.Any(other =>
                other.Exchange == q.Exchange && other.Symbol == q.Symbol &&
                (other.Batch.ObservedAtUtc > q.Batch.ObservedAtUtc ||
                 (other.Batch.ObservedAtUtc == q.Batch.ObservedAtUtc && other.Id > q.Id))))
            .Include(q => q.Batch).OrderBy(q => q.Exchange).ThenBy(q => q.Symbol).ToListAsync(cancellationToken);
    }

    public Task<OptionQuote?> FindAsync(long id, CancellationToken cancellationToken) =>
        db.Quotes.AsNoTracking().Include(q => q.Batch).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);

    private static readonly Expression<Func<ImportBatch, BatchSummary>> Summary =
        b => new(b.Id, b.Source, b.Kind, b.ObservedAtUtc, b.ImportedAtUtc, b.Quotes.Count, b.TradingDate);
}
