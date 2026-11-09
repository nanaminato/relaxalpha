namespace RelaxAlpha.Application;

public sealed class ExchangeTradingCalendar(DateOnly coveredThrough, IEnumerable<DateOnly> additionalClosures)
{
    public static readonly DateOnly PublishedThrough = new(2026, 12, 31);
    private static readonly DateOnly FirstCoveredDate = new(2025, 12, 31);
    private readonly HashSet<DateOnly> closures = additionalClosures.Concat(PublishedClosures()).ToHashSet();

    public DateOnly? PreviousTradingDay(DateTime utcNow)
    {
        var date = DateOnly.FromDateTime(utcNow.AddHours(8)).AddDays(-1);
        if (date > coveredThrough) return null;

        while (date >= FirstCoveredDate)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closures.Contains(date))
                return date;
            date = date.AddDays(-1);
        }

        return null;
    }

    private static IEnumerable<DateOnly> PublishedClosures()
    {
        // https://www.shfe.com.cn/services/calenderandholidays/holiday/
        (int Month, int Start, int End)[] holidays =
            [(1, 1, 3), (2, 15, 23), (4, 4, 6), (5, 1, 5), (6, 19, 21), (9, 25, 27), (10, 1, 7)];
        foreach (var (month, start, end) in holidays)
            for (var day = start; day <= end; day++)
                yield return new DateOnly(2026, month, day);
    }
}

public sealed record ExchangeDailyStatus(
    bool Enabled, bool Offline, DateOnly? TradingDate, Guid? BatchId, DateTime? LastAttemptUtc,
    DateTime? RetryAfterUtc, string? Failure);

public interface IExchangeDailyIngestion
{
    Task<BatchSummary?> CollectAsync(CancellationToken cancellationToken);
    Task<ExchangeDailyStatus> StatusAsync(CancellationToken cancellationToken);
}

public interface IMarketUpdates
{
    Task PublishAsync(BatchSummary summary, CancellationToken cancellationToken);
}
