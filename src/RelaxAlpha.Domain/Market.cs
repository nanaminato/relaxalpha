namespace RelaxAlpha.Domain;

public enum OptionSide { Call, Put }
public enum SourceKind { UserProvided, ExchangePublic, Licensed, Simulated }
public enum QuoteQuality { TwoSided, Indicative, Crossed, DailySettlement }
public enum MarketUniverse { OfficialDaily, Imported, Simulated }

// 价格、预算等金额的统一上限，供 CSV、交易所文件和 API 入参校验共用。
public static class MarketLimits
{
    public const decimal MaxAmount = 1_000_000_000m;
}

public sealed class ImportBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Source { get; init; }
    public required SourceKind Kind { get; init; }
    public required string ContentHash { get; init; }
    public DateOnly? TradingDate { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
    public required DateTime ImportedAtUtc { get; init; }
    public List<OptionQuote> Quotes { get; init; } = [];
}

public sealed class OptionQuote
{
    public long Id { get; init; }
    public Guid BatchId { get; init; }
    public ImportBatch Batch { get; init; } = null!;
    public required string Exchange { get; init; }
    public required string Symbol { get; init; }
    public required string Underlying { get; init; }
    public required DateTime ExpiryUtc { get; init; }
    public required OptionSide Side { get; init; }
    public required decimal Strike { get; init; }
    public required decimal FuturesPrice { get; init; }
    public required decimal Multiplier { get; init; }
    public required decimal OpenFee { get; init; }
    public required decimal CloseFee { get; init; }
    public required decimal ExerciseFee { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? SettlementPrice { get; init; }
    public required int Volume { get; init; }
    public required int OpenInterest { get; init; }
    public required QuoteQuality Quality { get; init; }
    public decimal? EntryPrice => Quality == QuoteQuality.DailySettlement ? SettlementPrice : Ask;
    public decimal? Premium => EntryPrice > 0 ? EntryPrice * Multiplier : null;
    public decimal? EntryCost => Premium + OpenFee;
}
