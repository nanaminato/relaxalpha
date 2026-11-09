using MathNet.Numerics.Distributions;
using MathNet.Numerics.RootFinding;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Quant;

public sealed record Valuation(double Price, double? Delta, double? Gamma, double? ThetaPerDay, double? VegaPerPoint);

public static class Black76
{
    public const double DaysPerYear = 365;
    public const double MaxVolatility = 5;

    public static Valuation Evaluate(OptionSide side, double futures, double strike, double years, double rate, double volatility)
    {
        if (!Enum.IsDefined(side) || !double.IsFinite(futures) || futures < 0 ||
            !double.IsFinite(strike) || strike <= 0 || !double.IsFinite(years) || years < 0 ||
            !double.IsFinite(rate) || !double.IsFinite(volatility) || volatility < 0 || volatility > MaxVolatility)
            throw new ArgumentException("无效的 Black-76 定价参数。");

        var sign = side == OptionSide.Call ? 1 : -1;
        var discount = Math.Exp(-rate * years);
        if (years == 0 || volatility == 0 || futures == 0)
            return new(discount * Math.Max(sign * (futures - strike), 0), null, null, null, null);

        var rootTime = Math.Sqrt(years);
        var deviation = volatility * rootTime;
        var d1 = (Math.Log(futures) - Math.Log(strike)) / deviation + deviation / 2;
        var d2 = d1 - deviation;
        var density = Normal.PDF(0, 1, d1);
        var cdf1 = Normal.CDF(0, 1, sign * d1);
        var delta = discount * sign * cdf1;
        var price = Math.Max(0, discount * sign * (futures * cdf1 - strike * Normal.CDF(0, 1, sign * d2)));
        return new(price, delta, discount * density / (futures * deviation),
            (rate * price - discount * futures * density * volatility / (2 * rootTime)) / DaysPerYear,
            discount * futures * density * rootTime / 100);
    }

    public static double? ImpliedVolatility(OptionSide side, double futures, double strike, double years, double rate, double marketPrice)
    {
        var lower = Evaluate(side, futures, strike, years, rate, 0).Price;
        if (years <= 0 || futures <= 0 || !double.IsFinite(marketPrice) || marketPrice <= lower ||
            marketPrice >= Math.Exp(-rate * years) * (side == OptionSide.Call ? futures : strike))
            return null;

        double PriceError(double vol) => Evaluate(side, futures, strike, years, rate, vol).Price - marketPrice;
        var upperError = PriceError(MaxVolatility);
        if (upperError < 0) return null;
        if (upperError == 0) return MaxVolatility;

        return Brent.FindRoot(PriceError, 0, MaxVolatility, 1e-10);
    }
}
