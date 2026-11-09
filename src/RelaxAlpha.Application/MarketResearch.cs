using RelaxAlpha.Domain;
using RelaxAlpha.Quant;

namespace RelaxAlpha.Application;

public interface IMarketDataSource
{
    Task<ImportBatch> ReadAsync(Stream input, SourceInfo source, CancellationToken cancellationToken);
}

public interface IMarketStore
{
    Task<BatchSummary> SaveAsync(ImportBatch batch, CancellationToken cancellationToken);
    Task<IReadOnlyList<BatchSummary>> SourcesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OptionQuote>> LatestAsync(MarketUniverse universe, DateTime asOf, DateOnly? tradingDate, CancellationToken cancellationToken);
    Task<OptionQuote?> FindAsync(long id, CancellationToken cancellationToken);
}

public sealed record SourceInfo(string Source, SourceKind Kind, DateTime ObservedAtUtc);
public sealed record BatchSummary(Guid Id, string Source, SourceKind Kind, DateTime ObservedAtUtc, DateTime ImportedAtUtc, int Count, DateOnly? TradingDate);
public sealed record MarketQuote(
    long Id, string Exchange, string Symbol, string Underlying, DateTime ExpiryUtc, OptionSide Side,
    decimal Strike, decimal FuturesPrice, decimal Multiplier, decimal OpenFee, decimal CloseFee, decimal ExerciseFee,
    decimal? Bid, decimal? Ask, decimal? Premium, decimal? EntryCost, int Volume, int OpenInterest,
    QuoteQuality Quality, string Source, SourceKind Kind, DateTime ObservedAtUtc, double? Iv,
    decimal? SettlementPrice, DateOnly? TradingDate)
{
    public static MarketQuote From(OptionQuote q) => new(q.Id, q.Exchange, q.Symbol, q.Underlying, q.ExpiryUtc,
        q.Side, q.Strike, q.FuturesPrice, q.Multiplier, q.OpenFee, q.CloseFee, q.ExerciseFee, q.Bid, q.Ask,
        q.Premium, q.EntryCost, q.Volume, q.OpenInterest, q.Quality, q.Batch.Source, q.Batch.Kind,
        q.Batch.ObservedAtUtc, OptionResearch.ImpliedVolatility(q, q.Batch.ObservedAtUtc), q.SettlementPrice, q.Batch.TradingDate);
}

public sealed record ScanSettings
{
    public decimal Budget { get; init; } = 1000;
    public double TargetMultiple { get; init; } = 5;
    public int MinDays { get; init; } = 30;
    public int MaxDays { get; init; } = 90;
    public OptionSide? Side { get; init; }
    public double MinIv { get; init; }
    public double MaxIv { get; init; } = 5;
    public int MinVolume { get; init; } = 10;
    public int MinOpenInterest { get; init; } = 50;
    public decimal MaxSpreadRatio { get; init; } = 0.3m;
    public int MaxAgeDays { get; init; } = 3;
    public double Shock { get; init; } = 0.1;
    public bool? Simulated { get; init; }
    public MarketUniverse? Universe { get; init; }
    public MarketUniverse SelectedUniverse => Universe ?? (Simulated switch
    {
        true => MarketUniverse.Simulated,
        false => MarketUniverse.Imported,
        null => MarketUniverse.OfficialDaily
    });

    public void Validate()
    {
        if (!Enum.IsDefined(SelectedUniverse) || Budget is <= 0 or > 1_000_000_000 || !(TargetMultiple is >= 1 and <= 1000) ||
            MinDays < 0 || MaxDays < MinDays || MaxDays > 730 ||
            !(MinIv >= 0 && MaxIv >= MinIv && MaxIv <= Black76.MaxVolatility) ||
            MinVolume < 0 || MinOpenInterest < 0 || MaxSpreadRatio is < 0 or > 1 ||
            MaxAgeDays is < 1 or > 365 || !(Shock is >= 0 and <= 1))
            throw new ArgumentException("策略参数超出范围。");
    }
}

public sealed record Opportunity(MarketQuote Quote, decimal? SpreadRatio, Target Target, Scenario ShockScenario, bool ReachesTarget);
public sealed record ScanResult(DateTime AsOfUtc, ScanSettings Settings, int Evaluated, int Matched, IReadOnlyList<Opportunity> Opportunities);
public sealed record SimulationInput(long QuoteId, double FuturesPrice, double RemainingDays, double Volatility, double[]? Targets);
public sealed record TargetResult(double Multiple, Target? Condition);
public sealed record SimulationResult(Scenario Scenario, IReadOnlyList<TargetResult> Targets);

public sealed class MarketResearch(IMarketStore store, TimeProvider time, ExchangeTradingCalendar calendar)
{
    public Task<IReadOnlyList<OptionQuote>> LatestAsync(MarketUniverse universe, CancellationToken ct) =>
        LatestAtAsync(universe, time.GetUtcNow().UtcDateTime, ct);

    private Task<IReadOnlyList<OptionQuote>> LatestAtAsync(MarketUniverse universe, DateTime now, CancellationToken ct)
    {
        var tradingDate = universe == MarketUniverse.OfficialDaily ? calendar.PreviousTradingDay(now) : null;
        return store.LatestAsync(universe, now, tradingDate, ct);
    }

    public async Task<ScanResult> ScanAsync(ScanSettings settings, CancellationToken ct)
    {
        settings.Validate();
        var now = time.GetUtcNow().UtcDateTime;
        var universe = settings.SelectedUniverse;
        var daily = universe == MarketUniverse.OfficialDaily;
        var quotes = await LatestAtAsync(universe, now, ct);
        var opportunities = new List<Opportunity>();
        foreach (var quote in quotes)
        {
            var days = (quote.ExpiryUtc - now).TotalDays;
            if (days < settings.MinDays || days > settings.MaxDays ||
                (settings.Side is { } side && side != quote.Side) ||
                (!daily && quote.Batch.ObservedAtUtc < now.AddDays(-settings.MaxAgeDays)) ||
                quote.Quality != (daily ? QuoteQuality.DailySettlement : QuoteQuality.TwoSided) ||
                quote.EntryCost is not { } entryCost || entryCost > settings.Budget ||
                quote.Volume < settings.MinVolume || quote.OpenInterest < settings.MinOpenInterest)
                continue;

            decimal? spread = daily ? null : (quote.Ask!.Value - quote.Bid!.Value) / quote.Ask.Value;
            if (spread > settings.MaxSpreadRatio) continue;

            var data = MarketQuote.From(quote);
            if (data.Iv is not { } iv || iv < settings.MinIv || iv > settings.MaxIv) continue;
            var target = OptionResearch.RequiredMove(quote, settings.TargetMultiple, 0, iv);
            if (target is null) continue;

            var direction = quote.Side == OptionSide.Call ? 1 : -1;
            var shockedPrice = (double)quote.FuturesPrice * (1 + direction * settings.Shock);
            var shock = OptionResearch.Simulate(quote, shockedPrice, 0, iv);
            opportunities.Add(new(data, spread, target, shock, shock.Multiple >= settings.TargetMultiple));
        }

        return new(now, settings, quotes.Count, opportunities.Count, opportunities
            .OrderBy(o => Math.Max(0, o.Quote.Side == OptionSide.Call ? o.Target.Change : -o.Target.Change))
            .ThenBy(o => o.Quote.EntryCost).ThenBy(o => o.Quote.Id).Take(200).ToArray());
    }

    public async Task<SimulationResult?> SimulateAsync(SimulationInput input, CancellationToken ct)
    {
        var quote = await store.FindAsync(input.QuoteId, ct);
        if (quote is null) return null;

        var remaining = (quote.ExpiryUtc - time.GetUtcNow().UtcDateTime).TotalDays;
        var targets = input.Targets ?? [2, 5, 10];
        if (quote.EntryPrice is not > 0 || quote.Quality == QuoteQuality.Crossed ||
            !(input.FuturesPrice is >= 0 and <= 1_000_000_000) ||
            !(input.RemainingDays >= 0 && input.RemainingDays <= Math.Min(730, remaining)) ||
            !(input.Volatility is >= 0 and <= Black76.MaxVolatility) ||
            targets.Length > 20 || targets.Any(x => !(x is >= 1 and <= 1000)))
            throw new ArgumentException("模拟需要未到期合约、有效研究价格及有效情景参数。");

        var scenario = OptionResearch.Simulate(quote, input.FuturesPrice, input.RemainingDays, input.Volatility);
        return new(scenario, targets.Select(m =>
            new TargetResult(m, OptionResearch.RequiredMove(quote, m, input.RemainingDays, input.Volatility))).ToArray());
    }
}
