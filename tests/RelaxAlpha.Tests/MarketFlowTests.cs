using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;
using Xunit;

namespace RelaxAlpha.Tests;

public sealed class MarketFlowTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private readonly TimeProvider clock = new FixedClock();

    [Fact]
    public async Task MigratesImportsDeduplicatesAndFiltersSeparateUniverses()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SqliteMarketDatabase(new DbContextOptionsBuilder<SqliteMarketDatabase>()
            .UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var store = new EfMarketStore(db);
        var research = new MarketResearch(store, clock, new(ExchangeTradingCalendar.PublishedThrough, []));
        var parser = new CsvMarketDataSource(clock);
        var good = Row("M-C-3400", "C", 3400, "17", "20");
        var data = string.Join('\n', good, Row("M-P-2900", "P", 2900, "34", "39"),
            Row("M-C-CROSSED", "C", 3250, "49", "48"), Row("M-P-NOBID", "P", 2900, "", "39"),
            Row("M-C-WIDE", "C", 3400, "10", "20"), Row("M-C-EXPENSIVE", "C", 3150, "104", "115"),
            Row("M-C-THIN", "C", 3400, "17", "20", volume: 5), Row("M-C-FAR", "C", 3400, "17", "20", days: 120));
        var real = new SourceInfo("交易所测试样本", SourceKind.ExchangePublic, Now);
        var old = new SourceInfo("过时样本", SourceKind.UserProvided, Now.AddDays(-4));
        var demo = new SourceInfo("模拟", SourceKind.Simulated, Now);
        var first = await store.SaveAsync(await Read(parser, real, data), default);
        var duplicate = await store.SaveAsync(await Read(parser, real, data), default);
        Assert.Equal(first.Id, duplicate.Id);
        await store.SaveAsync(await Read(parser, old, Row("M-C-STALE", "C", 3400, "17", "20")), default);
        await store.SaveAsync(await Read(parser, demo, good), default);

        var sources = await store.SourcesAsync(default);
        Assert.Equal(3, sources.Count);
        Assert.Equal(8, sources.Single(b => b.Id == first.Id).Count);
        var result = await research.ScanAsync(new() { Universe = MarketUniverse.Imported }, default);
        Assert.Equal(9, result.Evaluated);
        Assert.Equal(2, result.Matched);
        Assert.All(result.Opportunities, o => Assert.Equal(SourceKind.ExchangePublic, o.Quote.Kind));
        Assert.Single((await research.ScanAsync(new() { Simulated = true }, default)).Opportunities);
        Assert.Single((await research.ScanAsync(new() { Universe = MarketUniverse.Imported, Side = OptionSide.Put }, default)).Opportunities);
        Assert.Empty((await research.ScanAsync(new() { Universe = MarketUniverse.Imported, Budget = 201 }, default)).Opportunities);
        Assert.Empty((await research.ScanAsync(new() { Universe = MarketUniverse.Imported, MinIv = 0.9 }, default)).Opportunities);

        var latest = await research.LatestAsync(MarketUniverse.Imported, default);
        Assert.Equal(QuoteQuality.Crossed, latest.Single(q => q.Symbol == "M-C-CROSSED").Quality);
        Assert.Equal(QuoteQuality.Indicative, latest.Single(q => q.Symbol == "M-P-NOBID").Quality);
        var selected = latest.Single(q => q.Symbol == "M-C-3400");
        var simulated = await research.SimulateAsync(new(selected.Id, 3600, 0, 0.2, null), default);
        Assert.NotNull(simulated);
        Assert.Equal(3, simulated.Targets.Count);
        Assert.Equal(1998d / 202, simulated.Scenario.Multiple, 10);
        await Assert.ThrowsAsync<ArgumentException>(() => research.SimulateAsync(new(selected.Id, 3600, 60, 0.2, null), default));
    }

    [Fact]
    public async Task LatestSelectionUsesObservationTimeRatherThanImportOrder()
    {
        await using var db = new SqliteMarketDatabase(new DbContextOptionsBuilder<SqliteMarketDatabase>()
            .UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        var store = new EfMarketStore(db);
        var parser = new CsvMarketDataSource(clock);
        var recent = new SourceInfo("来源 A", SourceKind.Licensed, Now);
        var older = new SourceInfo("来源 B", SourceKind.UserProvided, Now.AddMinutes(-5));
        await store.SaveAsync(await Read(parser, recent, Row("M-C-3400", "C", 3400, "18", "22")), default);
        await store.SaveAsync(await Read(parser, older, Row("M-C-3400", "C", 3400, "17", "20")), default);
        Assert.Equal(22, Assert.Single(await store.LatestAsync(MarketUniverse.Imported, Now, null, default)).Ask);
    }

    [Fact]
    public async Task CsvRejectsDuplicateContractsInvalidDecimalsAndFutureObservations()
    {
        var parser = new CsvMarketDataSource(clock);
        var source = new SourceInfo("样本", SourceKind.UserProvided, Now);
        var row = Row("M-C-3400", "C", 3400, "17", "20");
        var duplicate = await Assert.ThrowsAsync<InvalidDataException>(() => Read(parser, source, row + "\n" + row));
        Assert.Contains("第 3 行", duplicate.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(parser, source, Row("M-C-3400", "C", 3400, "17", "-2")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(parser, source with { ObservedAtUtc = Now.AddMinutes(1) }, row));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(parser, source, Row("DEMO-M-C-3400", "C", 3400, "17", "20")));
        Assert.True(CsvMarketDataSource.TryTimestamp("2026-11-09T16:00:00+08:00", out var converted));
        Assert.Equal(Now, converted);
        Assert.False(CsvMarketDataSource.TryTimestamp("2026-11-09T16:00:00", out _));
    }

    private static string Row(string symbol, string side, int strike, string bid, string ask, int volume = 150, int days = 52) =>
        $"DCE,{symbol},M,{Now.AddDays(days):O},{side},{strike},3100,10,2,2,2,{bid},{ask},{volume},600";

    private static async Task<ImportBatch> Read(CsvMarketDataSource parser, SourceInfo source, string rows)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(CsvMarketDataSource.Header + "\n" + rows));
        return await parser.ReadAsync(input, source, default);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
