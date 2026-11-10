using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;

namespace RelaxAlpha.Infrastructure;

public sealed class ShfeDailyIngestion(
    MarketDatabase db, IMarketStore store, ShfeDailyReports reports, ExchangeDailyOptions settings,
    ExchangeTradingCalendar calendar, TimeProvider clock, DailyImportLedger ledger) : IExchangeDailyIngestion
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
            !await ledger.ClaimAsync(Feed, day, now, RetryDelay, ct))
            return null;

        var cached = await ledger.Day(Feed, day).SingleAsync(ct);
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
            await ledger.CompleteAsync(Feed, day, batch.Id, ct);
            return batch;
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
            e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or ArgumentException)
        {
            await ledger.FailAsync(Feed, day, e.Message, ct);
            throw;
        }
    }

    public Task<ExchangeDailyStatus> StatusAsync(CancellationToken ct) =>
        ledger.StatusAsync(Feed, ShfeDailyReports.Name, settings.Enabled,
            !string.IsNullOrWhiteSpace(settings.OfflineDirectory),
            calendar.PreviousTradingDay(clock.GetUtcNow().UtcDateTime), ct);
}
