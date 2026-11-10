using System.Globalization;
using System.Text.RegularExpressions;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Application;

/// <summary>郑商所品种的合约乘数，以及交易所收取的每手交易与行权费用（不含期货公司加收）。</summary>
public sealed record CzceProduct(decimal Multiplier, decimal OpenFee, decimal CloseFee, decimal ExerciseFee);

/// <summary>郑商所期权合约代码，例如 SR601C5600、CF701MSC13600。</summary>
public sealed record CzceOptionCode(string Product, string Month, bool Series, OptionSide Side, decimal Strike)
{
    public string Underlying => Product + Month;
}

public static class CzceMarket
{
    private const string SeriesMarker = "MS";

    private static readonly Regex OptionPattern = new(
        @"\A(?<product>[A-Z]{1,2})(?<month>[0-9]{3})(?<series>MS)?(?<side>[CP])(?<strike>[0-9]+(?:\.[0-9]+)?)\z",
        RegexOptions.CultureInvariant);

    // 期权到期日按「交割月前两个月最后一个日历日」计算的品种。
    private static readonly HashSet<string> EndOfMonthExpiry = new(StringComparer.Ordinal) { "AP", "CJ", "PX" };

    /// <summary>
    /// 默认参数取自交易所收费一览表与品种合约细则，可在 CzceDaily:Products 中按品种覆盖。
    /// 乘数单位：吨/手；费用单位：元/手。
    /// </summary>
    public static IReadOnlyDictionary<string, CzceProduct> DefaultProducts { get; } =
        new Dictionary<string, CzceProduct>(StringComparer.Ordinal)
        {
            ["AP"] = new(10, 1, 1, 1),
            ["CF"] = new(5, 1.5m, 1.5m, 1.5m),
            ["CJ"] = new(5, 1, 1, 1),
            ["FG"] = new(20, 0.5m, 0.5m, 0.5m),
            ["MA"] = new(10, 0.5m, 0.5m, 0.5m),
            ["OI"] = new(10, 1.5m, 1.5m, 1.5m),
            ["PF"] = new(5, 0.5m, 0.5m, 0.5m),
            ["PK"] = new(5, 0.8m, 0.8m, 0.8m),
            ["PL"] = new(20, 1, 1, 1),
            ["PR"] = new(15, 1, 1, 1),
            ["PX"] = new(5, 1, 1, 1),
            ["RM"] = new(10, 0.8m, 0.8m, 0.8m),
            ["SA"] = new(20, 0.5m, 0.5m, 0.5m),
            ["SF"] = new(5, 0.5m, 0.5m, 0.5m),
            ["SH"] = new(30, 2, 2, 2),
            ["SM"] = new(5, 0.5m, 0.5m, 0.5m),
            ["SR"] = new(10, 1.5m, 1.5m, 1.5m),
            ["TA"] = new(5, 0.5m, 0.5m, 0.5m),
            ["UR"] = new(20, 1, 1, 1),
            ["ZC"] = new(100, 150, 150, 150)
        };

    /// <summary>合并默认参数与配置覆盖。</summary>
    public static IReadOnlyDictionary<string, CzceProduct> Products(IReadOnlyDictionary<string, CzceProduct>? overrides)
    {
        if (overrides is not { Count: > 0 }) return DefaultProducts;
        var merged = new Dictionary<string, CzceProduct>(DefaultProducts, StringComparer.Ordinal);
        foreach (var (code, product) in overrides) merged[code] = product;
        return merged;
    }

    public static bool TryParseOption(string code, out CzceOptionCode option)
    {
        option = default!;
        var match = OptionPattern.Match(code);
        if (!match.Success) return false;
        var product = match.Groups["product"].Value;
        if (!decimal.TryParse(match.Groups["strike"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var strike) || strike <= 0)
            return false;

        option = new(product, match.Groups["month"].Value, match.Groups["series"].Value == SeriesMarker,
            match.Groups["side"].Value == "C" ? OptionSide.Call : OptionSide.Put, strike);
        return true;
    }

    /// <summary>三位合约月份（如 701）解析为交割月第一天。郑商所自 2020 年起使用三位月份，首位为年份个位数。</summary>
    public static DateOnly? DeliveryMonth(string month)
    {
        if (month.Length != 3 || !month.All(char.IsAsciiDigit)) return null;
        var monthOfYear = int.Parse(month.AsSpan(1), CultureInfo.InvariantCulture);
        return monthOfYear is >= 1 and <= 12 ? new DateOnly(2020 + (month[0] - '0'), monthOfYear, 1) : null;
    }

    /// <summary>
    /// 期权到期日（同最后交易日），按交易所期权合约细则：
    /// 常规期权为交割月前一个月第 15 个日历日（含）往前数第 3 个交易日；
    /// 苹果、红枣、对二甲苯为交割月前两个月最后一个日历日（含）往前数第 3 个交易日；
    /// 系列期权为交割月前两个月第 15 个日历日（含）往前数第 3 个交易日。
    /// 交易日历未覆盖该日期时返回 null。
    /// </summary>
    public static DateOnly? Expiry(CzceOptionCode option, ExchangeTradingCalendar calendar)
    {
        if (DeliveryMonth(option.Month) is not { } delivery) return null;
        var endOfMonth = !option.Series && EndOfMonthExpiry.Contains(option.Product);
        var month = delivery.AddMonths(option.Series || endOfMonth ? -2 : -1);
        var anchor = endOfMonth
            ? new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month))
            : new DateOnly(month.Year, month.Month, 15);
        if (calendar.TradingDayOnOrBefore(anchor) is not { } start) return null;

        var expiry = calendar.TradingDayBackFrom(start, 3);
        return expiry is { } date && calendar.Covers(date) ? date : null;
    }
}
