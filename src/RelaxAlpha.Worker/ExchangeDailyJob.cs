using Quartz;
using RelaxAlpha.Application;

namespace RelaxAlpha.Worker;

[DisallowConcurrentExecution]
public sealed class ExchangeDailyJob(IExchangeDailyIngestion ingestion, IMarketUpdates updates) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        if (await ingestion.CollectAsync(ct) is { } batch)
            await updates.PublishAsync(batch, ct);
    }
}
