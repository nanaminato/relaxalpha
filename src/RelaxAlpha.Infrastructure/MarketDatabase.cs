using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Infrastructure;

public abstract class MarketDatabase(DbContextOptions options) : DbContext(options)
{
    public DbSet<ImportBatch> Batches => Set<ImportBatch>();
    public DbSet<OptionQuote> Quotes => Set<OptionQuote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.HasIndex(x => new { x.Kind, x.Source, x.ObservedAtUtc, x.ContentHash }).IsUnique();
            batch.Property(x => x.Source).HasMaxLength(160);
            batch.Property(x => x.ContentHash).HasMaxLength(64);
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

        return new(batch.Id, batch.Source, batch.Kind, batch.ObservedAtUtc, batch.ImportedAtUtc, batch.Quotes.Count);
    }

    public async Task<IReadOnlyList<BatchSummary>> SourcesAsync(CancellationToken cancellationToken) =>
        await db.Batches.AsNoTracking().OrderByDescending(x => x.ImportedAtUtc).ThenBy(x => x.Id).Take(100)
            .Select(Summary).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OptionQuote>> LatestAsync(bool simulated, DateTime asOf, CancellationToken cancellationToken)
    {
        var universe = db.Quotes.AsNoTracking().Where(q => (q.Batch.Kind == SourceKind.Simulated) == simulated);
        return await universe.Where(q => q.ExpiryUtc > asOf && !universe.Any(other =>
                other.Exchange == q.Exchange && other.Symbol == q.Symbol &&
                (other.Batch.ObservedAtUtc > q.Batch.ObservedAtUtc ||
                 (other.Batch.ObservedAtUtc == q.Batch.ObservedAtUtc && other.Id > q.Id))))
            .Include(q => q.Batch).OrderBy(q => q.Exchange).ThenBy(q => q.Symbol).ToListAsync(cancellationToken);
    }

    public Task<OptionQuote?> FindAsync(long id, CancellationToken cancellationToken) =>
        db.Quotes.AsNoTracking().Include(q => q.Batch).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);

    private static readonly Expression<Func<ImportBatch, BatchSummary>> Summary =
        b => new(b.Id, b.Source, b.Kind, b.ObservedAtUtc, b.ImportedAtUtc, b.Quotes.Count);
}
