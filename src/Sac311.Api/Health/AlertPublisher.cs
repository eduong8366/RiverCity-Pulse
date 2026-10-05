using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Sac311.Api.Health;

internal sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Optional. When set, every change in readiness is POSTed here as JSON with a <c>text</c> field (Slack-style incoming webhooks).</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>How often the readiness checks run in the background.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>One alert: readiness moved from <see cref="Previous"/> (null at startup) to <see cref="Status"/>.</summary>
internal sealed record Alert(string Text, string Status, string? Previous, DateTime AtUtc, IReadOnlyList<AlertCheck> Checks);

internal sealed record AlertCheck(string Name, string Status, string? Description);

/// <summary>
/// The freshness monitor: runs the readiness checks every <see cref="AlertOptions.Period"/> and alerts when the overall
/// status changes (Healthy → Degraded, Degraded → Healthy, anything → Unhealthy). A first report that isn't Healthy
/// alerts too, so restarting the API during an incident doesn't hide it. Each alert is logged (Warning, or Information
/// for a recovery) and, if <see cref="AlertOptions.WebhookUrl"/> is set, POSTed there; a failed POST is logged, never thrown.
/// It lives in the API rather than the worker so it still fires when the worker has stopped.
/// </summary>
internal sealed partial class AlertPublisher(
    IHttpClientFactory httpClients, IOptions<AlertOptions> options, TimeProvider time, ILogger<AlertPublisher> logger) : IHealthCheckPublisher
{
    public const string HttpClientName = "alerts";

    private HealthStatus? _last;

    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        var previous = _last;
        _last = report.Status;
        if (previous == report.Status || (previous is null && report.Status == HealthStatus.Healthy))
        {
            return;
        }

        var alert = Build(report, previous, time.GetUtcNow().UtcDateTime);
        if (report.Status == HealthStatus.Healthy)
        {
            LogRecovered(logger, alert.Text);
        }
        else
        {
            LogAlert(logger, alert.Text);
        }

        if (options.Value.WebhookUrl is not { } url)
        {
            return;
        }

        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(url, alert, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogWebhookFailed(logger, ex, url);
        }
    }

    internal static Alert Build(HealthReport report, HealthStatus? previous, DateTime nowUtc)
    {
        var checks = report.Entries.Select(e => new AlertCheck(e.Key, e.Value.Status.ToString(), e.Value.Description)).ToList();
        var headline = report.Status == HealthStatus.Healthy
            ? "RiverCity Pulse recovered: readiness is Healthy again."
            : $"RiverCity Pulse readiness is {report.Status}"
              + (previous is { } p ? $" (was {p})." : " at startup.");
        var problems = report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => $"{e.Key}: {e.Value.Description}");
        var text = string.Join('\n', problems.Prepend(headline));
        return new Alert(text, report.Status.ToString(), previous?.ToString(), nowUtc, checks);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ALERT {Text}")]
    private static partial void LogAlert(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "ALERT {Text}")]
    private static partial void LogRecovered(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Error, Message = "The alert webhook {Url} failed")]
    private static partial void LogWebhookFailed(ILogger logger, Exception ex, Uri url);
}
