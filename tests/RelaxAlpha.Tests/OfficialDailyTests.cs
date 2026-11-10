using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;
using Xunit;

namespace RelaxAlpha.Tests;

public sealed class OfficialDailyTests
{
    private static readonly DateOnly TradeDate = new(2026, 11, 6);
    private static readonly DateTime Monday = new(2026, 11, 9, 1, 0, 0, DateTimeKind.Utc);
    private static readonly ExchangeTradingCalendar Calendar = new(ExchangeTradingCalendar.PublishedThrough, []);

    [Theory]
    [InlineData("2026-11-08T16:30:00Z", "2026-11-06")]
    [InlineData("2026-11-09T16:01:00Z", "2026-11-09")]
    [InlineData("2026-10-07T18:00:00Z", "2026-09-30")]
    [InlineData("2026-02-24T01:00:00Z", "2026-02-13")]
    [InlineData("2027-01-02T01:00:00Z", null)]
    public void PriorTradingDayUsesBeijingDateAndOfficialClosures(string now, string? previous) =>
        Assert.Equal(previous, Calendar.PreviousTradingDay(DateTimeOffset.Parse(now).UtcDateTime)?.ToString("yyyy-MM-dd"));

    [Fact]
    public async Task FailedReportIsRetriedWithoutDownloadingSuccessfulFilesAgain()
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync();

        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var clock = new MutableClock();
        var enabled = new ExchangeDailyOptions { Enabled = true };
        ShfeDailyIngestion Service(MarketDatabase db, ExchangeDailyOptions settings) =>
            new(db, new EfMarketStore(db), new ShfeDailyReports(http, settings), settings, Calendar, clock,
                new DailyImportLedger(db));

        await using (var db = database.Create())
            Assert.Null(await Service(db, new()).CollectAsync(ct));
        Assert.Empty(handler.Requests);

        await using (var db = database.Create())
        {
            var service = Service(db, enabled);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.CollectAsync(ct));
            var status = await service.StatusAsync(ct);
            Assert.Equal(TradeDate, status.TradingDate);
            Assert.Null(status.BatchId);
            Assert.NotNull(status.Failure);
            Assert.Equal(Monday.AddHours(6), status.RetryAfterUtc);
        }

        await using (var db = database.Create())
            Assert.Null(await Service(db, enabled).CollectAsync(ct));
        Assert.Equal(2, handler.Requests.Count);

        clock.Now = Monday.AddHours(6);
        await using (var first = database.Create())
        await using (var second = database.Create())
        {
            var results = await Task.WhenAll(Service(first, enabled).CollectAsync(ct), Service(second, enabled).CollectAsync(ct));
            Assert.Single(results.OfType<BatchSummary>());
        }
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(path => path.Contains("/future/", StringComparison.Ordinal)));

        // 清空入库结果但保留原文件缓存：再次采集只重建批次，不重新下载。
        await using (var db = database.Create())
            await db.DailyImports.ExecuteUpdateAsync(u => u.SetProperty(x => x.BatchId, (Guid?)null)
                .SetProperty(x => x.RetryAfterUtc, Monday));
        await using (var db = database.Create())
            Assert.NotNull(await Service(db, enabled).CollectAsync(ct));
        Assert.Equal(5, handler.Requests.Count);

        await using (var db = database.Create())
        {
            var service = Service(db, enabled);
            Assert.Null(await service.CollectAsync(ct));
            var status = await service.StatusAsync(ct);
            Assert.NotNull(status.BatchId);
            Assert.Null(status.Failure);
            Assert.Null(status.RetryAfterUtc);

            var research = new MarketResearch(new EfMarketStore(db), clock, Calendar);
            var quote = Assert.Single(await research.LatestAsync(MarketUniverse.OfficialDaily, ct));
            Assert.Equal("RB2701C3400", quote.Symbol);
            Assert.Equal(201.5m, quote.EntryCost);
            Assert.Equal(3100m, quote.FuturesPrice);
            Assert.Equal(TradeDate, quote.Batch.TradingDate);
            Assert.Null(quote.Ask);
            Assert.Null(quote.Bid);
            Assert.Empty(await research.LatestAsync(MarketUniverse.Imported, ct));

            var scan = await research.ScanAsync(new() { MaxAgeDays = 1, MaxSpreadRatio = 0 }, ct);
            var opportunity = Assert.Single(scan.Opportunities);
            Assert.Null(opportunity.SpreadRatio);
            Assert.Equal(QuoteQuality.DailySettlement, opportunity.Quote.Quality);
            Assert.NotNull(opportunity.Quote.Iv);
            var simulation = await research.SimulateAsync(new(quote.Id, 3600, 0, 0.25, null), ct);
            Assert.NotNull(simulation);
            Assert.Equal(1998.5 / 201.5, simulation.Scenario.Multiple, 10);

            clock.Now = Monday.AddDays(1);
            Assert.Empty(await research.LatestAsync(MarketUniverse.OfficialDaily, ct));
        }

        Assert.Equal(5, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{"report_date":20261105,"o_curinstrument":[{"PRODUCTID":"rb_f"}]}""")]
    [InlineData("""{"report_date":"20261106","o_curinstrument":[]}""")]
    [InlineData(@"{""report_date"":""20261106"",""o_curinstrument"":[{""PRODUCTID"":")]
    [InlineData("""<html>系统维护中</html>""")]
    public async Task InvalidOfficialFilesAreRejectedWithoutTouchingTheCache(string payload)
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync();
        using var http = new HttpClient(new StaticHandler(payload));
        var clock = new MutableClock();
        var settings = new ExchangeDailyOptions { Enabled = true };
        await using var db = database.Create();

        var service = new ShfeDailyIngestion(db, new EfMarketStore(db), new ShfeDailyReports(http, settings),
            settings, Calendar, clock, new DailyImportLedger(db));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CollectAsync(ct));

        var status = await service.StatusAsync(ct);
        Assert.Null(status.BatchId);
        Assert.NotNull(status.Failure);
        Assert.Null((await db.DailyImports.SingleAsync(ct)).Futures);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Monday;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        private int optionRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("www.shfe.com.cn", uri.Host);
            Assert.Empty(uri.Query);
            var path = uri.AbsolutePath;
            Requests.Enqueue(path);
            if (path.Contains("/option/dailydata/", StringComparison.Ordinal) && Interlocked.Increment(ref optionRequests) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var data = path switch
            {
                "/data/tradedata/future/dailydata/kx20261106.dat" => """
                    {"report_date":20261106,"o_curinstrument":[
                        {"PRODUCTID":"rb_f","DELIVERYMONTH":"2701","SETTLEMENTPRICE":3100},
                        {"PRODUCTID":"rb_f","DELIVERYMONTH":"小计","SETTLEMENTPRICE":""}
                    ]}
                    """,
                "/data/tradedata/option/dailydata/kx20261106.dat" => """
                    {"report_date":"20261106","o_curinstrument":[
                        {"INSTRUMENTID":"rb2701C3400","UNDERLYINGINSTRID":"rb2701","PRODUCTGROUPID":"rb",
                         "OPTIONSTYPE":"1","STRIKEPRICE":3400,"SETTLEMENTPRICE":20,"VOLUME":150,"OPENINTEREST":600},
                        {"INSTRUMENTID":"小计","OPTIONSTYPE":""}
                    ]}
                    """,
                "/data/busiparamdata/option/ContractBaseInfo20261106.dat" => """
                    {"report_date":"20261106","OptionContractBaseInfo":[
                        {"INSTRUMENTID":"RB2701C3400","COMMODITYID":"rb","TRADINGDAY":"20261106","TRADEUNIT":"10","EXPIREDATE":"20261225"}
                    ]}
                    """,
                "/data/busiparamdata/option/Settlement20261106.dat" => """
                    {"report_date":"20261106","OptionSettlement":[
                        {"INSTRUMENTID":"rb2701C3400","TRADINGDAY":"20261106","TRADEFEERATIO":"0","TRADEFEEUNIT":"1.5",
                         "STRIKEFEERATIO":"0","STRIKEFEEUNIT":"1.5"}
                    ]}
                    """,
                _ => throw new InvalidOperationException(path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(data, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }
}
