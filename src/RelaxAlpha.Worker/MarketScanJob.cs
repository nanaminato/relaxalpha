using Quartz;
using RelaxAlpha.Application;

namespace RelaxAlpha.Worker;

public sealed class ScheduledScan
{
    private ScanResult? latest;
    public ScanResult? Latest => Volatile.Read(ref latest);
    internal void Publish(ScanResult result) => Volatile.Write(ref latest, result);
}

[DisallowConcurrentExecution]
public sealed class MarketScanJob(MarketResearch research, ScheduledScan scheduledScan) : IJob
{
    public async Task Execute(IJobExecutionContext context) =>
        scheduledScan.Publish(await research.ScanAsync(new ScanSettings(), context.CancellationToken));
}
