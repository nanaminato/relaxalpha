using Microsoft.Extensions.Logging;
using Quartz;
using RelaxAlpha.Application;

namespace RelaxAlpha.Worker;

/// <summary>按注册顺序逐个交易所采集日行情；单个交易所失败不影响其他交易所。</summary>
[DisallowConcurrentExecution]
public sealed class ExchangeDailyJob(
    IEnumerable<IExchangeDailyIngestion> ingestions, IMarketUpdates updates, ILogger<ExchangeDailyJob> log) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        foreach (var ingestion in ingestions)
        {
            try
            {
                if (await ingestion.CollectAsync(ct) is { } batch)
                    await updates.PublishAsync(batch, ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(error, "日行情采集失败：{Feed}", ingestion.GetType().Name);
            }
        }
    }
}
