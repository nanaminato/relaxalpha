namespace RelaxAlpha.Infrastructure;

public sealed class OfficialDailyImport
{
    public required string Source { get; init; }
    public required DateOnly TradingDate { get; init; }
    public required DateTime LastAttemptUtc { get; set; }
    public required DateTime RetryAfterUtc { get; set; }
    public Guid? BatchId { get; set; }
    public string? Failure { get; set; }
    public byte[]? Futures { get; set; }
    public byte[]? Options { get; set; }
    public byte[]? Contracts { get; set; }
    public byte[]? Fees { get; set; }
}
