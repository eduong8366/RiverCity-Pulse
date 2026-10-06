using System.Globalization;
using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker reclean [--restart] [--max-pages N]</c>: fetch the whole feed again and apply the current cleaners and
/// seeds to every request still in the source (<see cref="RecleanJob"/>). Exit codes as in <see cref="RunExitCode"/>.
/// </summary>
internal sealed partial class RecleanVerb(RecleanJob job, ILogger<RecleanVerb> logger) : IVerb
{
    public string Name => "reclean";

    public string Usage => "reclean [--restart] [--max-pages N]   re-fetch the feed and apply the current cleaners and seeds to loaded rows";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = new RecleanRequest();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--max-pages" when i + 1 < args.Length && int.TryParse(args[++i], CultureInfo.InvariantCulture, out var max) && max > 0:
                    request = request with { MaxPages = max };
                    break;
                case "--restart":
                    request = request with { Restart = true };
                    break;
                default:
                    LogBadArgument(logger, args[i], Usage);
                    return 2;
            }
        }

        var run = await job.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return RunExitCode.For(run);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Bad argument near '{Argument}'. Usage: {Usage}")]
    private static partial void LogBadArgument(ILogger logger, string argument, string usage);
}
