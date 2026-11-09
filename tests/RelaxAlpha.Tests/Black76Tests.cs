using RelaxAlpha.Domain;
using RelaxAlpha.Quant;
using Xunit;

namespace RelaxAlpha.Tests;

public sealed class Black76Tests
{
    [Fact]
    public void AtTheMoneyBenchmarkAndPutCallParity()
    {
        var call = Black76.Evaluate(OptionSide.Call, 100, 100, 1, 0.05, 0.2);
        var put = Black76.Evaluate(OptionSide.Put, 100, 100, 1, 0.05, 0.2);
        Assert.Equal(7.577082, call.Price, 6);
        Assert.Equal(call.Price, put.Price, 10);

        call = Black76.Evaluate(OptionSide.Call, 114, 100, 0.5, 0.03, 0.3);
        put = Black76.Evaluate(OptionSide.Put, 114, 100, 0.5, 0.03, 0.3);
        Assert.Equal(Math.Exp(-0.03 * 0.5) * 14, call.Price - put.Price, 10);
    }

    [Theory]
    [InlineData(OptionSide.Call)]
    [InlineData(OptionSide.Put)]
    public void GreeksMatchFiniteDifferencesAndIvRecoversInput(OptionSide side)
    {
        const double futures = 102;
        const double time = 0.45;
        const double vol = 0.28;
        double Price(double f, double t, double v) => Black76.Evaluate(side, f, 105, t, 0.025, v).Price;
        var model = Black76.Evaluate(side, futures, 105, time, 0.025, vol);
        const double priceStep = 0.01;
        const double step = 0.00001;
        var up = Price(futures + priceStep, time, vol);
        var down = Price(futures - priceStep, time, vol);

        Assert.Equal((up - down) / (2 * priceStep), model.Delta!.Value, 6);
        Assert.Equal((up - 2 * model.Price + down) / (priceStep * priceStep), model.Gamma!.Value, 6);
        Assert.Equal((Price(futures, time - step, vol) - Price(futures, time + step, vol)) / (2 * step * 365),
            model.ThetaPerDay!.Value, 7);
        Assert.Equal((Price(futures, time, vol + step) - Price(futures, time, vol - step)) / (2 * step * 100),
            model.VegaPerPoint!.Value, 7);
        Assert.Equal(vol, Black76.ImpliedVolatility(side, futures, 105, time, 0.025, model.Price)!.Value, 8);
    }

    [Fact]
    public void DegenerateModelsAndUnavailableVolatilityAreExplicit()
    {
        var expiry = Black76.Evaluate(OptionSide.Put, 95, 100, 0, 0.05, 0.2);
        Assert.Equal(5, expiry.Price);
        Assert.Null(expiry.Delta);
        Assert.Equal(10 * Math.Exp(-0.02), Black76.Evaluate(OptionSide.Call, 110, 100, 1, 0.02, 0).Price, 10);
        Assert.Equal(100 * Math.Exp(-0.02), Black76.Evaluate(OptionSide.Put, 0, 100, 1, 0.02, 0.4).Price, 10);
        Assert.Null(Black76.ImpliedVolatility(OptionSide.Call, 110, 100, 1, 0, 5));
        Assert.Null(Black76.ImpliedVolatility(OptionSide.Call, 110, 100, 1, 0, 110));
        Assert.Null(Black76.ImpliedVolatility(OptionSide.Call, 110, 100, 0, 0, 11));
        Assert.Throws<ArgumentException>(() => Black76.Evaluate(OptionSide.Call, 110, 100, -1, 0.05, 0.2));
    }
}

public sealed class OptionResearchTests
{
    [Fact]
    public void ExpiryIncludesPerLotMultiplierAndFees()
    {
        var quote = Quote(OptionSide.Call);
        Assert.Equal(40, quote.Premium);
        Assert.Equal(42, quote.EntryCost);

        var scenario = OptionResearch.Simulate(quote, 115, 0, 0.3);
        Assert.Equal(15, scenario.Valuation.Price);
        Assert.Equal(5, scenario.ExitFee);
        Assert.Equal(145, scenario.ExitValue);
        Assert.Equal(103, scenario.Profit);
        Assert.Equal(145d / 42, scenario.Multiple, 10);
        Assert.Equal(-42, OptionResearch.Simulate(quote, 90, 0, 0.3).Profit);
        Assert.Equal(0, OptionResearch.Simulate(quote, 100.2, 0, 0.3).ExitValue);
    }

    [Theory]
    [InlineData(OptionSide.Call, 0, 2)]
    [InlineData(OptionSide.Call, 20, 5)]
    [InlineData(OptionSide.Put, 0, 10)]
    [InlineData(OptionSide.Put, 20, 10)]
    public void TargetPriceRecoversRequestedMultiple(OptionSide side, double days, double multiple)
    {
        var quote = Quote(side);
        var target = Assert.IsType<Target>(OptionResearch.RequiredMove(quote, multiple, days, 0.3));
        Assert.Equal(multiple, OptionResearch.Simulate(quote, target.FuturesPrice, days, 0.3).Multiple, 7);
        Assert.Equal((target.FuturesPrice - 100) / 100, target.Change, 10);
        if (days > 0) Assert.Equal(3, OptionResearch.Simulate(quote, target.FuturesPrice, days, 0.3).ExitFee);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void UnreachablePutTargetIsNull(double days) =>
        Assert.Null(OptionResearch.RequiredMove(Quote(OptionSide.Put), 100, days, 0.3));

    private static OptionQuote Quote(OptionSide side) => new()
    {
        Exchange = "DCE", Symbol = "M", Underlying = "M", ExpiryUtc = DateTime.UnixEpoch.AddDays(60),
        Side = side, Strike = 100, FuturesPrice = 100, Multiplier = 10, Bid = 3.5m, Ask = 4,
        OpenFee = 2, CloseFee = 3, ExerciseFee = 5, Volume = 200, OpenInterest = 800, Quality = QuoteQuality.TwoSided
    };
}
