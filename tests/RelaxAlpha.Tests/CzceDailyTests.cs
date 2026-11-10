using System.Net;
using System.Text;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;
using Xunit;

namespace RelaxAlpha.Tests;

public sealed class CzceDailyTests
{
    private static readonly DateOnly TradeDate = new(2026, 11, 6);
    private static readonly DateTime Monday = new(2026, 11, 9, 1, 0, 0, DateTimeKind.Utc);
    private static readonly ExchangeTradingCalendar Calendar = new(ExchangeTradingCalendar.PublishedThrough, []);

    // 到期日对照 2612、2701 合约的实际到期安排，覆盖常规、系列与月末口径三类品种。
    [Theory]
    [InlineData("SR612C5000", "2026-11-11")]
    [InlineData("TA612P6000", "2026-11-11")]
    [InlineData("SR701MSC5600", "2026-11-11")]
    [InlineData("AP612C4800", "2026-10-28")]
    [InlineData("CJ701P10000", "2026-11-26")]
    [InlineData("PX701C8000", "2026-11-26")]
    [InlineData("CF702C15000", null)]
    public void OptionExpiryFollowsExchangeRules(string code, string? expected)
    {
        Assert.True(CzceMarket.TryParseOption(code, out var option));
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), CzceMarket.Expiry(option, Calendar));
    }

    [Fact]
    public void OptionCodeParsesProductMonthSeriesAndStrike()
    {
        Assert.True(CzceMarket.TryParseOption("CF701MSC13600", out var series));
        Assert.Equal("CF", series.Product);
        Assert.Equal("CF701", series.Underlying);
        Assert.True(series.Series);
        Assert.Equal(OptionSide.Call, series.Side);
        Assert.Equal(13600m, series.Strike);

        Assert.False(CzceMarket.TryParseOption("小计", out _));
        Assert.False(CzceMarket.TryParseOption("AP合计", out _));
        Assert.Null(CzceMarket.Expiry(series with { Month = "999" }, Calendar));
    }

    [Fact]
    public async Task ImportsOptionChainUsingConfiguredTerms()
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync();
        using var http = new HttpClient(new CzceHandler());
        var clock = new MutableClock();
        var settings = new CzceDailyOptions { Enabled = true };
        await using var db = database.Create();

        var service = new CzceDailyIngestion(db, new EfMarketStore(db), new CzceDailyReports(http, settings),
            settings, Calendar, clock, new DailyImportLedger(db));

        var batch = await service.CollectAsync(ct);
        Assert.NotNull(batch);
        Assert.Equal(TradeDate, batch.TradingDate);
        Assert.Equal(1, batch.Count);

        var status = await service.StatusAsync(ct);
        Assert.Equal("CZCE", status.Source);
        Assert.Equal(batch.Id, status.BatchId);
        Assert.Null(status.Failure);

        var quotes = await new EfMarketStore(db).LatestAsync(MarketUniverse.OfficialDaily, Monday, TradeDate, ct);
        var quote = Assert.Single(quotes);
        Assert.Equal("CZCE", quote.Exchange);
        Assert.Equal("SR612C5000", quote.Symbol);
        Assert.Equal("SR612", quote.Underlying);
        Assert.Equal(OptionSide.Call, quote.Side);
        Assert.Equal(5000m, quote.Strike);
        Assert.Equal(5915m, quote.FuturesPrice);
        Assert.Equal(10m, quote.Multiplier);
        Assert.Equal(1.5m, quote.OpenFee);
        Assert.Equal(1.5m, quote.ExerciseFee);
        Assert.Equal(128m, quote.SettlementPrice);
        Assert.Equal(20, quote.OpenInterest);
        Assert.Equal(QuoteQuality.DailySettlement, quote.Quality);
        Assert.Equal(new DateOnly(2026, 11, 11), DateOnly.FromDateTime(quote.ExpiryUtc));

        // 已入库的交易日不再重复采集。
        Assert.Null(await service.CollectAsync(ct));
    }

    private sealed class MutableClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Monday);
    }

    private sealed class CzceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var data = path.Contains("/Future/", StringComparison.Ordinal) ? Futures : Options;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(data, Encoding.UTF8, "text/plain")
            });
        }

        private const string Futures = """
            					郑州商品交易所期货每日行情表(2026-11-06)
            合约代码|昨结算    |今开盘    |最高价    |最低价    |今收盘    |今结算    |涨跌1    |涨跌2    |成交量(手)|持仓量    |增减量    |成交额(万元)|交割结算价
            SR612 |5,900.00  |5,900.00  |5,900.00  |5,900.00  |5,910.00  |5,915.00  |0.00     |0.00     |1,000     |2,000     |0         |1.00        |
            小计    |          |          |          |          |          |          |         |         |0         |0         |0         |0.00        |
            """;

        private const string Options = """
            					郑州商品交易所期权每日行情表(2026-11-06)
            合约代码   |昨结算    |今开盘    |最高价    |最低价    |今收盘    |今结算    |涨跌1     |涨跌2     |成交量(手)|持仓量    |增减量    |成交额(万元)|DELTA     |隐含波动率|行权量
            SR612C5000 |100.00    |120.00    |130.00    |110.00    |125.00    |128.00    |1.00      |1.00      |9         |20        |2         |10.51       |0.5000    |12.34     |0
            SR612MSC5100|80.00    |90.00     |95.00     |85.00     |88.00     |90.00     |1.00      |1.00      |1         |2         |0         |1.00        |0.4000    |11.00     |0
            AP701C4800 |903.00    |1,357.50  |1,357.50  |1,090.00  |1,200.00  |1,177.00  |297.00    |274.00    |9         |2         |2         |10.51       |0.9698    |51.54     |0
            ZZ612C1000 |1.00      |1.00      |1.00      |1.00      |1.00      |1.00      |0.00      |0.00      |1         |1         |0         |0.00        |0.1000    |10.00     |0
            小计       |          |          |          |          |          |          |          |          |0         |0         |0         |0.00        |          |          |0
            """;
    }
}
