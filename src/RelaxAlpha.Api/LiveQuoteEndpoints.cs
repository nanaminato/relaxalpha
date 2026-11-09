using System.Net;
using System.Text.Json;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;
using RelaxAlpha.Infrastructure;

namespace RelaxAlpha.Api;

public static class LiveQuoteEndpoints
{
    public static void MapLiveQuotes(this RouteGroupBuilder api)
    {
        var live = api.MapGroup("/live");
        live.MapGet("/status", (TqQuoteFeed feed, CancellationToken ct) => Call(feed, feed.StatusAsync, ct));
        live.MapPost("/quotes/{id:long}", async (long id, HttpRequest request, TqQuoteFeed feed,
            IMarketStore store, TimeProvider time, CancellationToken ct) =>
        {
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (!feed.Enabled) return Disabled();
            var quote = await store.FindAsync(id, ct);
            if (quote is null) return Results.NotFound();
            if (quote.Batch.Kind == SourceKind.Simulated || quote.ExpiryUtc <= time.GetUtcNow().UtcDateTime ||
                TqQuoteFeed.Symbols(quote) is not { } symbols)
                return Results.Problem("当前合约无法订阅天勤盘口。", statusCode: 400);
            return await Call(feed, token => feed.QuotesAsync(symbols.Option, symbols.Underlying, token), ct);
        });
    }

    private static async Task<IResult> Call<T>(TqQuoteFeed feed, Func<CancellationToken, Task<T?>> fetch,
        CancellationToken ct) where T : class
    {
        if (!feed.Enabled) return Disabled();
        try
        {
            var result = await fetch(ct);
            return result is null ? Results.Problem("行情服务返回空响应。", statusCode: 502) : Results.Ok(result);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Results.Problem("天勤订阅已达当前会话容量。", statusCode: 429);
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
            e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Results.Problem("天勤行情服务暂不可用。", statusCode: 503);
        }
    }

    private static IResult Disabled() => Results.Problem("天勤行情服务尚未启用。", statusCode: 503);
}
