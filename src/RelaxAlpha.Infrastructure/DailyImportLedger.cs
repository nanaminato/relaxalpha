using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;

namespace RelaxAlpha.Infrastructure;

/// <summary>按「数据源 + 交易日」声明采集尝试、记录失败与完成状态，供各交易所日行情任务共用。</summary>
public sealed class DailyImportLedger(MarketDatabase db)
{
    public IQueryable<OfficialDailyImport> Day(string source, DateOnly day) =>
        db.DailyImports.Where(x => x.Source == source && x.TradingDate == day);

    /// <summary>声明本次尝试；已有入库结果或未到重试时间时返回 false。</summary>
    public async Task<bool> ClaimAsync(string source, DateOnly day, DateTime now, TimeSpan retryDelay, CancellationToken ct)
    {
        var retryAfter = now.Add(retryDelay);
        if (await Day(source, day).Where(x => x.BatchId == null && x.RetryAfterUtc <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastAttemptUtc, now)
                .SetProperty(x => x.RetryAfterUtc, retryAfter)
                .SetProperty(x => x.Failure, (string?)null), ct) == 1)
            return true;

        if (await Day(source, day).AnyAsync(ct)) return false;
        var added = new OfficialDailyImport
        {
            Source = source, TradingDate = day, LastAttemptUtc = now, RetryAfterUtc = retryAfter
        };
        db.DailyImports.Add(added);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(added).State = EntityState.Detached;
            if (await Day(source, day).AnyAsync(ct)) return false;
            throw;
        }
    }

    public Task FailAsync(string source, DateOnly day, string failure, CancellationToken ct)
    {
        var detail = failure.Length <= 300 ? failure : failure[..300];
        return Day(source, day).ExecuteUpdateAsync(update => update.SetProperty(x => x.Failure, detail), ct);
    }

    public Task CompleteAsync(string source, DateOnly day, Guid batchId, CancellationToken ct) =>
        Day(source, day).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.BatchId, batchId)
            .SetProperty(x => x.Failure, (string?)null), ct);

    public async Task<ExchangeDailyStatus> StatusAsync(string source, string name, bool enabled, bool offline,
        DateOnly? day, CancellationToken ct)
    {
        var record = day is { } date
            ? await Day(source, date).Select(x => new { x.BatchId, x.LastAttemptUtc, x.RetryAfterUtc, x.Failure })
                .SingleOrDefaultAsync(ct)
            : null;
        return new(source, name, enabled, offline, day, record?.BatchId, record?.LastAttemptUtc,
            record?.BatchId is null ? record?.RetryAfterUtc : null, record?.Failure);
    }
}
