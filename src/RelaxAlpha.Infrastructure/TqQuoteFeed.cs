using System.Net.Http.Json;
using System.Text.RegularExpressions;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Infrastructure;

public sealed record LiveQuote(
    string Symbol, string State, DateTimeOffset? QuoteTime, bool? Stale,
    double? LastPrice, double? Bid, double? Ask, long? Volume, long? OpenInterest);

public sealed record LiveQuoteBatch(string Source, string State, IReadOnlyList<LiveQuote> Quotes);
public sealed record LiveFeedStatus(string Source, string State, int Subscriptions, int Capacity, int SessionGeneration);

public sealed class TqQuoteFeed(HttpClient http)
{
    private static readonly Regex Contract = new(
        @"\A([A-Z]{1,3})([0-9]{3,4})(?:-?([CP])-?([0-9]+(?:\.[0-9]+)?))?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool Enabled => http.BaseAddress is not null;

    public Task<LiveFeedStatus?> StatusAsync(CancellationToken ct) =>
        http.GetFromJsonAsync<LiveFeedStatus>("status", ct);

    public Task<LiveQuoteBatch?> QuotesAsync(string option, string underlying, CancellationToken ct) =>
        http.GetFromJsonAsync<LiveQuoteBatch>($"quotes?symbols={Uri.EscapeDataString($"{option},{underlying}")}", ct);

    public static (string Option, string Underlying)? Symbols(OptionQuote quote)
    {
        var option = Format(quote.Exchange, quote.Symbol, quote.Side);
        var underlying = Format(quote.Exchange, quote.Underlying, null);
        return option is null || underlying is null ? null : (option, underlying);
    }

    private static string? Format(string exchange, string code, OptionSide? side)
    {
        if (exchange is not ("SHFE" or "INE" or "DCE" or "CZCE" or "GFEX")) return null;
        var parts = Contract.Match(code);
        if (!parts.Success || parts.Groups[3].Success != side.HasValue) return null;
        var month = parts.Groups[2].Value;
        if (exchange != "CZCE" && month.Length != 4) return null;
        var prefix = exchange == "CZCE"
            ? parts.Groups[1].Value.ToUpperInvariant() + month[^3..]
            : parts.Groups[1].Value.ToLowerInvariant() + month;
        if (side is null) return $"{exchange}.{prefix}";

        var direction = side == OptionSide.Call ? "C" : "P";
        if (!parts.Groups[3].Value.Equals(direction, StringComparison.OrdinalIgnoreCase)) return null;
        var strike = parts.Groups[4].Value;
        return exchange is "DCE" or "GFEX" ? $"{exchange}.{prefix}-{direction}-{strike}"
            : $"{exchange}.{prefix}{direction}{strike}";
    }
}
