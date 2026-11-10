namespace RelaxAlpha.Application;

public sealed class ExchangeTradingCalendar(DateOnly coveredThrough, IEnumerable<DateOnly> additionalClosures)
{
    public static readonly DateOnly PublishedThrough = new(2026, 12, 31);
    private static readonly DateOnly FirstCoveredDate = new(2025, 12, 31);
    private readonly HashSet<DateOnly> closures = additionalClosures.Concat(PublishedClosures()).ToHashSet();

    public DateOnly? PreviousTradingDay(DateTime utcNow)
    {
        var date = DateOnly.FromDateTime(utcNow.AddHours(8)).AddDays(-1);
        if (date > coveredThrough || date < FirstCoveredDate) return null;

        return TradingDayOnOrBefore(date);
    }

    /// <summary>是否在已发布日历范围内。超出范围的日期无法判断休市，一律视为不可用。</summary>
    public bool Covers(DateOnly date) => date >= FirstCoveredDate && date <= coveredThrough;

    public bool IsTradingDay(DateOnly date) =>
        Covers(date) && date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closures.Contains(date);

    /// <summary>取该日期当天或之前最近的交易日；超出日历范围返回 null。</summary>
    public DateOnly? TradingDayOnOrBefore(DateOnly date)
    {
        if (date > coveredThrough) return null;
        for (var day = date; day >= FirstCoveredDate; day = day.AddDays(-1))
            if (IsTradingDay(day)) return day;
        return null;
    }

    /// <summary>从给定交易日往前数第 count 个交易日（含当日，count 从 1 起）。</summary>
    public DateOnly? TradingDayBackFrom(DateOnly tradingDay, int count)
    {
        var day = tradingDay;
        for (var index = 1; index < count; index++)
        {
            day = day.AddDays(-1);
            while (day >= FirstCoveredDate && !IsTradingDay(day)) day = day.AddDays(-1);
            if (day < FirstCoveredDate) return null;
        }
        return day;
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
    string Source, string Name, bool Enabled, bool Offline, DateOnly? TradingDate, Guid? BatchId,
    DateTime? LastAttemptUtc, DateTime? RetryAfterUtc, string? Failure);

public interface IExchangeDailyIngestion
{
    Task<BatchSummary?> CollectAsync(CancellationToken cancellationToken);
    Task<ExchangeDailyStatus> StatusAsync(CancellationToken cancellationToken);
}

public interface IMarketUpdates
{
    Task PublishAsync(BatchSummary summary, CancellationToken cancellationToken);
}
