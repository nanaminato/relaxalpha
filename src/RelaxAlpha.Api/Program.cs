using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Quartz;
using RelaxAlpha.Api;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;
using RelaxAlpha.Worker;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(
    new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(
    new JsonStringEnumConverter(allowIntegerValues: false)));

var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connection = builder.Configuration.GetConnectionString("Market");
if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<MarketDatabase, SqliteMarketDatabase>(o =>
        o.UseSqlite(connection ?? "Data Source=relaxalpha.db"));
}
else if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<MarketDatabase, PostgresMarketDatabase>(o =>
        o.UseNpgsql(connection ?? throw new InvalidOperationException("Postgres 需要 ConnectionStrings:Market。")));
}
else
{
    throw new InvalidOperationException("Database:Provider 必须为 Sqlite 或 Postgres。");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IMarketStore, EfMarketStore>();
builder.Services.AddScoped<IMarketDataSource, CsvMarketDataSource>();
builder.Services.AddScoped<MarketResearch>();
var dailyOptions = builder.Configuration.GetSection("ExchangeDaily").Get<ExchangeDailyOptions>() ?? new();
builder.Services.AddSingleton(dailyOptions);
builder.Services.AddSingleton(new ExchangeTradingCalendar(
    dailyOptions.CalendarThrough ?? ExchangeTradingCalendar.PublishedThrough, dailyOptions.AdditionalClosures));
builder.Services.AddHttpClient<ShfeDailyReports>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
    http.MaxResponseContentBufferSize = ShfeDailyReports.MaxFileBytes;
    http.DefaultRequestHeaders.UserAgent.ParseAdd("RelaxAlpha/0.1");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IExchangeDailyIngestion, ShfeDailyIngestion>();
var liveUrl = builder.Configuration["LiveQuotes:Url"];
builder.Services.AddHttpClient<TqQuoteFeed>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(3);
    if (!string.IsNullOrWhiteSpace(liveUrl)) http.BaseAddress = new Uri(liveUrl.TrimEnd('/') + "/");
});
builder.Services.AddSingleton<IMarketUpdates, SignalRMarketUpdates>();
builder.Services.AddSingleton<ScheduledScan>();
builder.Services.AddQuartz(q =>
{
    var key = new JobKey("market-scan");
    q.AddJob<MarketScanJob>(o => o.WithIdentity(key));
    q.AddTrigger(o => o.ForJob(key).WithIdentity("market-scan-5min").StartNow()
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

    if (dailyOptions.Enabled)
    {
        var dailyKey = new JobKey("exchange-daily");
        q.AddJob<ExchangeDailyJob>(o => o.WithIdentity(dailyKey));
        q.AddTrigger(o => o.ForJob(dailyKey).WithIdentity("exchange-daily-hourly").StartNow()
            .WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()));
    }
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<MarketDatabase>().Database.MigrateAsync();
}

var api = app.MapGroup("/api");
api.MapLiveQuotes();
api.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }));
api.MapGet("/market", async (MarketUniverse? universe, bool? simulated, MarketResearch research, CancellationToken ct) =>
{
    var selection = new ScanSettings { Universe = universe, Simulated = simulated }.SelectedUniverse;
    if (!Enum.IsDefined(selection)) return Results.Problem("无效的行情范围。", statusCode: 400);
    var quotes = await research.LatestAsync(selection, ct);
    return Results.Ok(quotes.Select(MarketQuote.From).ToArray());
});
api.MapGet("/sources", async (IMarketStore store, CancellationToken ct) =>
    TypedResults.Ok(await store.SourcesAsync(ct)));
api.MapGet("/sources/daily", async (IExchangeDailyIngestion daily, CancellationToken ct) =>
    TypedResults.Ok(await daily.StatusAsync(ct)));
api.MapGet("/scans/scheduled", (ScheduledScan scan) => TypedResults.Ok(scan.Latest));
api.MapPost("/scans", async (ScanSettings settings, MarketResearch research, CancellationToken ct) =>
{
    try { return Results.Ok(await research.ScanAsync(settings, ct)); }
    catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: 400); }
});
api.MapPost("/simulate", async (SimulationInput input, MarketResearch research, CancellationToken ct) =>
{
    try
    {
        var result = await research.SimulateAsync(input, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: 400); }
});
api.MapPost("/sources/import", async (string source, string kind, string observedAt, HttpRequest request,
    IMarketDataSource parser, IMarketStore store, IMarketUpdates updates, CancellationToken ct) =>
{
    if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !type.MediaType.Equals("text/csv"))
        return Results.Problem("请使用 text/csv 上传 UTF-8 文件。", statusCode: 415);
    if (request.ContentLength > CsvMarketDataSource.MaxBytes)
        return Results.Problem("CSV 超过 4 MiB。", statusCode: 413);
    if (!Enum.GetNames<SourceKind>().Contains(kind, StringComparer.OrdinalIgnoreCase) ||
        !CsvMarketDataSource.TryTimestamp(observedAt, out var observed))
        return Results.Problem("请填写有效的来源类型和带时区的行情时间。", statusCode: 400);

    return await Import(request.Body, new(source, Enum.Parse<SourceKind>(kind, true), observed), parser, store, updates, ct);
});
api.MapPost("/sources/demo", async (HttpRequest request, TimeProvider clock, IMarketDataSource parser, IMarketStore store,
    IMarketUpdates updates, CancellationToken ct) =>
{
    if (!request.HasJsonContentType()) return Results.StatusCode(415);
    var observed = clock.GetUtcNow().UtcDateTime.Date;
    var expiry = observed.AddDays(52).AddHours(7).ToString("O");
    var csv = $"""
        {CsvMarketDataSource.Header}
        DCE,DEMO-M-C-3250,DEMO-M,{expiry},C,3250,3100,10,2,2,2,42,48,80,400
        DCE,DEMO-M-C-3400,DEMO-M,{expiry},C,3400,3100,10,2,2,2,17,20,130,710
        DCE,DEMO-M-P-2900,DEMO-M,{expiry},P,2900,3100,10,2,2,2,34,39,145,900
        SHFE,DEMO-RB-C-3650,DEMO-RB,{expiry},C,3650,3500,10,3,3,3,58,64,97,520
        SHFE,DEMO-RB-P-3200,DEMO-RB,{expiry},P,3200,3500,10,3,3,3,0,24,6,22
        """;
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));
    return await Import(input, new("RelaxAlpha 演示", SourceKind.Simulated, observed), parser, store, updates, ct);
});

app.MapHub<MarketHub>("/hubs/market");
app.MapFallbackToFile("index.html");
app.Run();

static async Task<IResult> Import(Stream input, SourceInfo source, IMarketDataSource parser, IMarketStore store,
    IMarketUpdates updates, CancellationToken ct)
{
    try
    {
        var batch = await parser.ReadAsync(input, source, ct);
        var summary = await store.SaveAsync(batch, ct);
        await updates.PublishAsync(summary, ct);
        return Results.Ok(summary);
    }
    catch (InvalidDataException error) { return Results.Problem(error.Message, statusCode: 400); }
}

public sealed class MarketHub : Hub;

public sealed class SignalRMarketUpdates(IHubContext<MarketHub> hub) : IMarketUpdates
{
    public Task PublishAsync(BatchSummary summary, CancellationToken ct) =>
        hub.Clients.All.SendAsync("MarketUpdated", summary, ct);
}
