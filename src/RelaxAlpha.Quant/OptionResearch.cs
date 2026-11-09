using MathNet.Numerics.RootFinding;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Quant;

public sealed record Scenario(Valuation Valuation, double ExitFee, double ExitValue, double Profit, double Multiple);
public sealed record Target(double FuturesPrice, double Change);

public static class OptionResearch
{
    public const double Rate = 0.02;

    public static double? ImpliedVolatility(OptionQuote quote, DateTime asOf)
    {
        decimal? price = quote.Quality switch
        {
            QuoteQuality.TwoSided => (quote.Ask + quote.Bid) / 2,
            QuoteQuality.DailySettlement => quote.SettlementPrice,
            _ => null
        };
        return price is > 0 ? Black76.ImpliedVolatility(quote.Side, (double)quote.FuturesPrice, (double)quote.Strike,
            Math.Max(0, (quote.ExpiryUtc - asOf).TotalDays) / Black76.DaysPerYear, Rate, (double)price.Value) : null;
    }

    public static Scenario Simulate(OptionQuote quote, double futures, double days, double volatility)
    {
        var entry = (double)(quote.EntryCost ?? throw new ArgumentException("模拟需要有效研究价格。"));
        var value = Black76.Evaluate(quote.Side, futures, (double)quote.Strike, days / Black76.DaysPerYear, Rate, volatility);
        var gross = value.Price * (double)quote.Multiplier;
        var fee = Math.Min(gross, (double)(days == 0 ? quote.ExerciseFee : quote.CloseFee));
        var exit = gross - fee;
        return new(value, fee, exit, exit - entry, exit / entry);
    }

    public static Target? RequiredMove(OptionQuote quote, double multiple, double days, double volatility)
    {
        var entry = (double)(quote.EntryCost ?? throw new ArgumentException("目标分析需要有效研究价格。"));
        var strike = (double)quote.Strike;
        var reference = (double)quote.FuturesPrice;
        var requiredPremium = (entry * multiple + (double)(days == 0 ? quote.ExerciseFee : quote.CloseFee)) /
            (double)quote.Multiplier;
        var call = quote.Side == OptionSide.Call;
        double price;
        if (days == 0)
        {
            price = call ? strike + requiredPremium : strike - requiredPremium;
            if (price < 0) return null;
        }
        else
        {
            double Error(double future) =>
                Black76.Evaluate(quote.Side, future, strike, days / Black76.DaysPerYear, Rate, volatility).Price - requiredPremium;
            if (!call && Error(0) < 0) return null;
            if (!call && Error(0) == 0) return new(0, -1);

            // A call's price is at least its discounted intrinsic value.
            var upper = call ? strike + requiredPremium * Math.Exp(Rate * days / Black76.DaysPerYear) * 2 : strike * 2;
            while (!call && Error(upper) > 0) upper *= 2;
            price = Brent.FindRoot(Error, 0, upper, 1e-8);
        }

        return new(price, (price - reference) / reference);
    }
}
