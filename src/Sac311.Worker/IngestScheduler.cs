using Microsoft.Extensions.Options;
using Sac311.Data.Ingest;
using Sac311.Ingestion;

namespace Sac311.Worker;

/// <summary>
/// <c>worker run</c>: an incremental run at startup and then every <see cref="IngestOptions.IncrementalInterval"/>, and
/// the daily reconcile on the first tick after <see cref="IngestOptions.ReconcileTimeLocal"/> (Sacramento time). Both
/// run on the same loop, one after the other, so they never compete for the ingest lock with each other.
/// A <see cref="PeriodicTimer"/> doesn't queue missed ticks, so a run longer than the interval delays the next one
/// instead of stacking them; the ingest lock covers a second worker process or a manual verb running alongside.
/// </summary>
internal sealed partial class IngestScheduler(
    IServiceScopeFactory scopes, IOptions<IngestOptions> options, TimeProvider time, ILogger<IngestScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.IncrementalInterval;
        var reconcileAt = options.Value.ReconcileTimeLocal;
        var nextReconcileUtc = ReconcileJob.NextRunUtc(time.GetUtcNow().UtcDateTime, reconcileAt);
        LogStarted(logger, interval, nextReconcileUtc);
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                await RunJobAsync<IncrementalJob>((job, ct) => job.RunAsync(ct), stoppingToken).ConfigureAwait(false);

                if (time.GetUtcNow().UtcDateTime >= nextReconcileUtc)
                {
                    var run = await RunJobAsync<ReconcileJob>((job, ct) => job.RunAsync(ct), stoppingToken).ConfigureAwait(false);
                    // A reconcile that was skipped (another process held the lock) or couldn't start tries again on the next tick.
                    if (run is not null && run.Status != RunStatus.Skipped)
                    {
                        nextReconcileUtc = ReconcileJob.NextRunUtc(time.GetUtcNow().UtcDateTime, reconcileAt);
                        LogNextReconcile(logger, nextReconcileUtc);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task<IngestRun?> RunJobAsync<TJob>(Func<TJob, CancellationToken, Task<IngestRun>> run, CancellationToken stoppingToken)
        where TJob : notnull
    {
        try
        {
            // A fresh scope per run, so the typed ArcGisClient gets a current handler from the HttpClient factory.
            using var scope = scopes.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<TJob>();
            return await run(job, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job records its own failures; this is for the database being unreachable before a run row exists.
            LogRunError(logger, ex, typeof(TJob).Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduler started: incremental every {Interval}, next reconcile {NextReconcileUtc:u}")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval, DateTime nextReconcileUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Next reconcile {NextReconcileUtc:u}")]
    private static partial void LogNextReconcile(ILogger logger, DateTime nextReconcileUtc);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Job} could not start; retrying at the next tick")]
    private static partial void LogRunError(ILogger logger, Exception ex, string job);
}
