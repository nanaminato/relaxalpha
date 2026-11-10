using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;

namespace RelaxAlpha.Infrastructure;

public sealed class CzceDailyIngestion(
    MarketDatabase db, IMarketStore store, CzceDailyReports reports, CzceDailyOptions settings,
    ExchangeTradingCalendar calendar, TimeProvider clock, DailyImportLedger ledger) : IExchangeDailyIngestion
{
    private const string Feed = "CZCE";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(6);

    public async Task<BatchSummary?> CollectAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!settings.Enabled || calendar.PreviousTradingDay(now) is not { } day ||
            !await ledger.ClaimAsync(Feed, day, now, RetryDelay, ct))
            return null;

        var cached = await ledger.Day(Feed, day).SingleAsync(ct);
        try
        {
            // 期货文件提供标的结算价，期权文件提供合约结算价与成交持仓，两者齐备才能建批次。
            if (cached.Futures is null)
            {
                cached.Futures = await reports.ReadAsync(day, futures: true, ct);
                await db.SaveChangesAsync(ct);
            }
            if (cached.Options is null)
            {
                cached.Options = await reports.ReadAsync(day, futures: false, ct);
                await db.SaveChangesAsync(ct);
            }

            var products = CzceMarket.Products(Overrides());
            var batch = await store.SaveAsync(CzceDailyReports.BuildBatch(
                cached.Futures!, cached.Options!, day, clock.GetUtcNow().UtcDateTime, products, calendar), ct);
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
        ledger.StatusAsync(Feed, CzceDailyReports.Name, settings.Enabled,
            !string.IsNullOrWhiteSpace(settings.OfflineDirectory),
            calendar.PreviousTradingDay(clock.GetUtcNow().UtcDateTime), ct);

    private Dictionary<string, CzceProduct> Overrides() =>
        settings.Products.ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => new CzceProduct(
            pair.Value.Multiplier, pair.Value.OpenFee, pair.Value.CloseFee, pair.Value.ExerciseFee), StringComparer.Ordinal);
}
