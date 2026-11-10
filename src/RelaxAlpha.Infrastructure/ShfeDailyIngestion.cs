using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;

namespace RelaxAlpha.Infrastructure;

public sealed class ShfeDailyIngestion(
    MarketDatabase db, IMarketStore store, ShfeDailyReports reports, ExchangeDailyOptions settings,
    ExchangeTradingCalendar calendar, TimeProvider clock) : IExchangeDailyIngestion
{
    private const string Feed = "SHFE";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(6);

    // 每份原文件独立下载并立即持久化，重试只补缺失的文件。
    private static readonly (ShfeDailyReports.Report Report, Func<OfficialDailyImport, byte[]?> Get,
        Action<OfficialDailyImport, byte[]> Set)[] SourceFiles =
    [
        (ShfeDailyReports.Futures, x => x.Futures, (x, value) => x.Futures = value),
        (ShfeDailyReports.Options, x => x.Options, (x, value) => x.Options = value),
        (ShfeDailyReports.Contracts, x => x.Contracts, (x, value) => x.Contracts = value),
        (ShfeDailyReports.Fees, x => x.Fees, (x, value) => x.Fees = value)
    ];

    public async Task<BatchSummary?> CollectAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!settings.Enabled || calendar.PreviousTradingDay(now) is not { } day ||
            !await ClaimAsync(day, now, ct))
            return null;

        var cached = await Day(day).SingleAsync(ct);
        try
        {
            foreach (var (report, get, set) in SourceFiles)
            {
                if (get(cached) is not null) continue;
                set(cached, await reports.ReadAsync(report, day, ct));
                await db.SaveChangesAsync(ct);
            }

            var batch = await store.SaveAsync(ShfeDailyReports.BuildBatch(cached, clock.GetUtcNow().UtcDateTime), ct);
            // A restart between the two saves can rebuild the batch from cache; SaveAsync deduplicates it.
            await Day(day).ExecuteUpdateAsync(u => u.SetProperty(x => x.BatchId, batch.Id).SetProperty(x => x.Failure, (string?)null), ct);
            return batch;
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
            e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or ArgumentException)
        {
            var detail = e.Message.Length <= 300 ? e.Message : e.Message[..300];
            await Day(day).ExecuteUpdateAsync(u => u.SetProperty(x => x.Failure, detail), ct);
            throw;
        }
    }

    public async Task<ExchangeDailyStatus> StatusAsync(CancellationToken ct)
    {
        var day = calendar.PreviousTradingDay(clock.GetUtcNow().UtcDateTime);
        var cached = day is { } date ? await Day(date).Select(x => new { x.BatchId, x.LastAttemptUtc, x.RetryAfterUtc, x.Failure })
            .SingleOrDefaultAsync(ct) : null;
        return new(settings.Enabled, !string.IsNullOrWhiteSpace(settings.OfflineDirectory), day, cached?.BatchId,
            cached?.LastAttemptUtc, cached?.BatchId is null ? cached?.RetryAfterUtc : null, cached?.Failure);
    }

    private IQueryable<OfficialDailyImport> Day(DateOnly day) =>
        db.DailyImports.Where(x => x.Source == Feed && x.TradingDate == day);

    private async Task<bool> ClaimAsync(DateOnly day, DateTime now, CancellationToken ct)
    {
        var retryAfter = now.Add(RetryDelay);
        if (await Day(day).Where(x => x.BatchId == null && x.RetryAfterUtc <= now).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.LastAttemptUtc, now).SetProperty(x => x.RetryAfterUtc, retryAfter)
            .SetProperty(x => x.Failure, (string?)null), ct) == 1)
            return true;

        if (await Day(day).AnyAsync(ct)) return false;
        var added = new OfficialDailyImport { Source = Feed, TradingDate = day, LastAttemptUtc = now, RetryAfterUtc = retryAfter };
        db.DailyImports.Add(added);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(added).State = EntityState.Detached;
            if (await Day(day).AnyAsync(ct)) return false;
            throw;
        }
    }
}
