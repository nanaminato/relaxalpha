using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;
using Xunit;

namespace RelaxAlpha.Tests;

public sealed class TqQuoteFeedTests
{
    [Theory]
    [InlineData("SHFE", "RB2701C3000", "RB2701", "SHFE.rb2701C3000", "SHFE.rb2701")]
    [InlineData("INE", "SC2701C600", "SC2701", "INE.sc2701C600", "INE.sc2701")]
    [InlineData("DCE", "M2701-C-3000", "M2701", "DCE.m2701-C-3000", "DCE.m2701")]
    [InlineData("GFEX", "SI2701C12000", "SI2701", "GFEX.si2701-C-12000", "GFEX.si2701")]
    [InlineData("CZCE", "SR2701C5800", "SR701", "CZCE.SR701C5800", "CZCE.SR701")]
    public void ConvertsStoredExchangeSymbols(string exchange, string option, string future, string tqOption, string tqFuture)
    {
        Assert.Equal((tqOption, tqFuture), TqQuoteFeed.Symbols(Quote(exchange, option, future)));
    }

    [Theory]
    [InlineData("SHFE", "DEMO-RB-C-3650", "DEMO-RB")]
    [InlineData("SHFE", "RB2701P3000", "RB2701")]
    [InlineData("SHFE", "RB2701C3000", "RB701")]
    public void RejectsUnsupportedOrInconsistentSymbols(string exchange, string option, string future) =>
        Assert.Null(TqQuoteFeed.Symbols(Quote(exchange, option, future)));

    private static OptionQuote Quote(string exchange, string option, string future) => new()
    {
        Exchange = exchange, Symbol = option, Underlying = future, ExpiryUtc = DateTime.UnixEpoch,
        Side = OptionSide.Call, Strike = 3000, FuturesPrice = 3100, Multiplier = 10,
        OpenFee = 1, CloseFee = 1, ExerciseFee = 1, Volume = 1, OpenInterest = 1, Quality = QuoteQuality.DailySettlement
    };
}
