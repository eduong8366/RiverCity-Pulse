using Microsoft.Extensions.Logging;
using Sac311.Data.Ingest;
using Sac311.Data.Quality;
using Sac311.Domain.Cleaners;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <summary>
/// Data-quality checks, written to <c>ops.dq_result</c> after every successful ingestion run (see <see cref="DqRules"/>
/// for the thresholds):
/// <list type="bullet">
/// <item><c>source_count</c>: the source's row count against the highest count of the past 7 days; a drop of more than 2% fails.</item>
/// <item><c>null_rate.*</c>: null rates of requests created in the last 7 days against the 90 days before; more than 5 points higher warns.</item>
/// <item><c>repeat_address</c>: one address with 100+ requests created in the last 7 days warns, naming it (a form default, most likely).</item>
/// <item><c>flag.*</c>: how many requests carry each DQ flag (sentinel and future dates, bad close dates, unmapped values, bulk closures, ...); a jump warns.</item>
/// <item><c>rejects</c>: rows the run rejected; any warns.</item>
/// </list>
/// </summary>
public sealed partial class DqRunner(ArcGisClient client, DqStore store, TimeProvider time, ILogger<DqRunner> logger)
{
    public const int RecentDays = 7;
    public const int BaselineDays = 90;
    public static readonly TimeSpan SourceCountWindow = TimeSpan.FromDays(7);

    /// <summary>Runs the checks after <paramref name="run"/> if it succeeded. A failing check run is logged, never thrown: DQ watches ingestion, it doesn't fail it.</summary>
    public async Task RunAfterAsync(IngestRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status != RunStatus.Succeeded)
        {
            return;
        }

        try
        {
            await RunAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(logger, ex);
        }
    }

    /// <summary>Runs every check and records the results under <paramref name="runId"/> (null for an ad hoc check).</summary>
    public async Task<IReadOnlyList<DqResult>> RunAsync(long? runId, CancellationToken cancellationToken)
    {
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var results = new List<DqResult>();

        var sourceCount = await client.CountAsync("1=1", cancellationToken).ConfigureAwait(false);
        var high = await store.MaxObservedAsync("source_count", nowUtc - SourceCountWindow, cancellationToken).ConfigureAwait(false);
        results.Add(DqRules.SourceCount(sourceCount, high));

        var (recent, baseline) = await store.CountNullsAsync(Pacific.ToLocalDate(nowUtc), RecentDays, BaselineDays, cancellationToken).ConfigureAwait(false);
        results.Add(DqRules.NullRate("address", recent.AddressNull, recent.Rows, baseline.AddressNull, baseline.Rows));
        results.Add(DqRules.NullRate("zip", recent.ZipNull, recent.Rows, baseline.ZipNull, baseline.Rows));
        results.Add(DqRules.NullRate("geo", recent.GeoNull, recent.Rows, baseline.GeoNull, baseline.Rows));
        results.Add(DqRules.NullRate("category", recent.CategoryNull, recent.Rows, baseline.CategoryNull, baseline.Rows));
        results.Add(DqRules.NullRate("source", recent.SourceNull, recent.Rows, baseline.SourceNull, baseline.Rows));
        results.Add(DqRules.NullRate("neighborhood", recent.NeighborhoodNull, recent.CityRows, baseline.NeighborhoodNull, baseline.CityRows));

        results.Add(DqRules.RepeatAddress(await store.BusiestAddressesAsync(Pacific.ToLocalDate(nowUtc), RecentDays, 5, cancellationToken).ConfigureAwait(false)));

        var flags = await store.CountFlagsAsync(cancellationToken).ConfigureAwait(false);
        var previous = await store.LatestObservedAsync("flag.", cancellationToken).ConfigureAwait(false);
        foreach (var (flag, count) in flags)
        {
            results.Add(DqRules.FlagCount(flag.ToString(), count, previous.TryGetValue("flag." + flag, out var p) ? p : null));
        }

        if (runId is { } id)
        {
            results.Add(DqRules.Rejects(await store.CountRejectsAsync(id, cancellationToken).ConfigureAwait(false)));
        }

        await store.SaveAsync(runId, results, cancellationToken).ConfigureAwait(false);

        int fails = 0, warns = 0;
        foreach (var r in results)
        {
            if (r.Status == DqStatus.Fail)
            {
                fails++;
                LogFail(logger, r.CheckName, r.Message);
            }
            else if (r.Status == DqStatus.Warn)
            {
                warns++;
                LogWarn(logger, r.CheckName, r.Message);
            }
        }

        LogSummary(logger, results.Count, fails, warns);
        return results;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "DQ check {Check} failed: {Message}")]
    private static partial void LogFail(ILogger logger, string check, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DQ check {Check} warns: {Message}")]
    private static partial void LogWarn(ILogger logger, string check, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "DQ: {Checks} checks recorded, {Fails} failed, {Warns} warned")]
    private static partial void LogSummary(ILogger logger, int checks, int fails, int warns);

    [LoggerMessage(Level = LogLevel.Error, Message = "DQ checks could not run; the ingestion run itself is unaffected")]
    private static partial void LogError(ILogger logger, Exception ex);
}
