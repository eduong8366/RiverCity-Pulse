using Microsoft.Extensions.Options;
using Sac311.Ingestion;

namespace Sac311.Worker;

/// <summary>
/// <c>worker run</c>: an incremental run at startup and then every <see cref="IngestOptions.IncrementalInterval"/>.
/// A <see cref="PeriodicTimer"/> doesn't queue missed ticks, so a run longer than the interval delays the next one
/// instead of stacking them; the ingest lock covers a second worker process or a manual verb running alongside.
/// </summary>
internal sealed partial class IngestScheduler(
    IServiceScopeFactory scopes, IOptions<IngestOptions> options, TimeProvider time, ILogger<IngestScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.IncrementalInterval;
        LogStarted(logger, interval);
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                await RunIncrementalAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task RunIncrementalAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A fresh scope per run, so the typed ArcGisClient gets a current handler from the HttpClient factory.
            using var scope = scopes.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<IncrementalJob>();
            await job.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job records its own failures; this is for the database being unreachable before a run row exists.
            LogRunError(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduler started: incremental every {Interval}")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Incremental run could not start; retrying at the next tick")]
    private static partial void LogRunError(ILogger logger, Exception ex);
}
