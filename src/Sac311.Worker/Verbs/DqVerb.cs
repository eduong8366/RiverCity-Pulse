using Sac311.Data.Quality;
using Sac311.Ingestion;

namespace Sac311.Worker.Verbs;

/// <summary>
/// <c>worker dq</c>: runs the data-quality checks now (they also run after every successful ingestion run) and prints
/// them. Exits 1 if a check failed, otherwise 0.
/// </summary>
internal sealed partial class DqVerb(DqRunner runner, ILogger<DqVerb> logger) : IVerb
{
    public string Name => "dq";

    public string Usage => "dq                      run the data-quality checks now (results in ops.dq_result)";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            LogBadArgument(logger, args[0], Usage);
            return 2;
        }

        var results = await runner.RunAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Status,-4}  {r.CheckName,-28}  {r.Message}");
        }

        return results.Any(r => r.Status == DqStatus.Fail) ? 1 : 0;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected argument '{Argument}'. Usage: {Usage}")]
    private static partial void LogBadArgument(ILogger logger, string argument, string usage);
}
