using System.Globalization;
using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker backfill [--since 2026-09-02 | --since 30d] [--until 2026-10-02] [--restart] [--max-pages N]</c>.
/// Dates are UTC days. Exit codes as in <see cref="RunExitCode"/>.
/// </summary>
internal sealed partial class BackfillVerb(BackfillJob job, TimeProvider time, ILogger<BackfillVerb> logger) : IVerb
{
    public string Name => "backfill";

    public string Usage => "backfill [--since <date|Nd>] [--until <date>] [--restart] [--max-pages N]   load the feed (or a DateUpdated slice)";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = new BackfillRequest();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--since" when i + 1 < args.Length && ParseDay(args[++i]) is { } since:
                    request = request with { SinceUtc = since };
                    break;
                case "--until" when i + 1 < args.Length && ParseDay(args[++i]) is { } until:
                    request = request with { UntilUtc = until };
                    break;
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

    // "2026-09-02" is that UTC midnight; "30d" is 30 days before today's UTC midnight.
    private DateTime? ParseDay(string value)
    {
        if (value.EndsWith('d') && int.TryParse(value.AsSpan(0, value.Length - 1), CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return time.GetUtcNow().UtcDateTime.Date.AddDays(-days);
        }

        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Bad argument near '{Argument}'. Usage: {Usage}")]
    private static partial void LogBadArgument(ILogger logger, string argument, string usage);
}
