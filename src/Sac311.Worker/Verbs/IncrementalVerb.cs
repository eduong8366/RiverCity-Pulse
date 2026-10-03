using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker incremental</c>: one incremental run (what the scheduler does every 15 minutes). Exit codes as in
/// <see cref="RunExitCode"/>.
/// </summary>
internal sealed partial class IncrementalVerb(IncrementalJob job, ILogger<IncrementalVerb> logger) : IVerb
{
    public string Name => "incremental";

    public string Usage => "incremental             load rows edited since the watermark (needs a finished backfill)";

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
