using Sac311.Data.Ingest;
using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker aggregates</c>: recomputes the <c>agg</c> tables the API serves now (ingestion runs do it after any run that
/// changed rows). Holds the ingest lock like a job: exits 4 if another job has it, otherwise 0.
/// </summary>
internal sealed partial class AggregatesVerb(IngestLock ingestLock, AggregateRefresher refresher, ILogger<AggregatesVerb> logger) : IVerb
{
    public string Name => "aggregates";

    public string Usage => "aggregates              recompute the API's aggregate tables (agg.*) now";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            LogBadArgument(logger, args[0], Usage);
            return 2;
        }

        var held = await ingestLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            LogSkipped(logger);
            return 4;
        }

        await using (held.ConfigureAwait(false))
        {
            var refresh = await refresher.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Refresh {refresh.RefreshId}: as of {refresh.AsOfDate:yyyy-MM-dd}, {refresh.RequestCount:N0} requests, built in {refresh.BuildMs:N0} ms");
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected argument '{Argument}'. Usage: {Usage}")]
    private static partial void LogBadArgument(ILogger logger, string argument, string usage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aggregates not refreshed: another ingestion job holds the lock")]
    private static partial void LogSkipped(ILogger logger);
}
