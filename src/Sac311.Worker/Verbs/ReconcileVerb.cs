using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker reconcile</c>: one reconcile run (what the scheduler does daily at 03:30 Sacramento time). Exit codes as in
/// <see cref="RunExitCode"/>; a run stopped by the 95% guard is a failure (1).
/// </summary>
internal sealed partial class ReconcileVerb(ReconcileJob job, ILogger<ReconcileVerb> logger) : IVerb
{
    public string Name => "reconcile";

    public string Usage => "reconcile               mark requests gone from the source, fetch missing or stale ones again";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            LogBadArgument(logger, args[0], Usage);
            return 2;
        }

        var run = await job.RunAsync(cancellationToken).ConfigureAwait(false);
        return RunExitCode.For(run);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected argument '{Argument}'. Usage: {Usage}")]
    private static partial void LogBadArgument(ILogger logger, string argument, string usage);
}
