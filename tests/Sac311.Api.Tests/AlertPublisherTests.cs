using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sac311.Api.Health;

namespace Sac311.Api.Tests;

/// <summary>The freshness monitor: which readiness changes alert, and what reaches the webhook.</summary>
public sealed class AlertPublisherTests : IDisposable
{
    private static readonly DateTime NowUtc = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);
    private static readonly Uri Webhook = new("http://alerts.test/hook");

    private readonly RecordingHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private AlertPublisher Publisher(Uri? webhook = null) => new(
        new SingleClientFactory(_handler),
        Options.Create(new AlertOptions { WebhookUrl = webhook ?? Webhook }),
        new FixedTime(NowUtc),
        NullLogger<AlertPublisher>.Instance);

    private static HealthReport Report(HealthStatus freshness, HealthStatus ingestion = HealthStatus.Healthy) => new(
        new Dictionary<string, HealthReportEntry>
        {
            ["database"] = new(HealthStatus.Healthy, "The database answers.", TimeSpan.Zero, null, null),
            ["freshness"] = new(freshness, freshness == HealthStatus.Healthy ? "Last successful run 3 min ago." : "Data is stale.", TimeSpan.Zero, null, null),
            ["ingestion"] = new(ingestion, ingestion == HealthStatus.Healthy ? "Last run 9: Succeeded." : "Last run 9 stopped on schema drift.", TimeSpan.Zero, null, null),
        },
        TimeSpan.Zero);

    [Fact]
    public async Task A_healthy_start_and_unchanged_status_send_nothing()
    {
        var publisher = Publisher();

        await publisher.PublishAsync(Report(HealthStatus.Healthy), CancellationToken.None);
        await publisher.PublishAsync(Report(HealthStatus.Healthy), CancellationToken.None);

        Assert.Empty(_handler.Bodies);
    }

    [Fact]
    public async Task Degrading_then_recovering_sends_one_alert_each()
    {
        var publisher = Publisher();

        await publisher.PublishAsync(Report(HealthStatus.Healthy), CancellationToken.None);
        await publisher.PublishAsync(Report(HealthStatus.Healthy, HealthStatus.Degraded), CancellationToken.None);
        await publisher.PublishAsync(Report(HealthStatus.Degraded, HealthStatus.Degraded), CancellationToken.None);
        await publisher.PublishAsync(Report(HealthStatus.Healthy), CancellationToken.None);

        Assert.Equal(2, _handler.Bodies.Count);
        var degraded = _handler.Bodies[0].RootElement;
        Assert.Equal("Degraded", degraded.GetProperty("status").GetString());
        Assert.Equal("Healthy", degraded.GetProperty("previous").GetString());
        Assert.Equal(
            "RiverCity Pulse readiness is Degraded (was Healthy)." + Environment.NewLine + "ingestion: Last run 9 stopped on schema drift.",
            degraded.GetProperty("text").GetString());
        Assert.Equal(3, degraded.GetProperty("checks").GetArrayLength());

        var recovered = _handler.Bodies[1].RootElement;
        Assert.Equal("Healthy", recovered.GetProperty("status").GetString());
        Assert.Equal("Degraded", recovered.GetProperty("previous").GetString());
        Assert.StartsWith("RiverCity Pulse recovered", recovered.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_that_isnt_healthy_alerts()
    {
        await Publisher().PublishAsync(Report(HealthStatus.Degraded), CancellationToken.None);

        var alert = Assert.Single(_handler.Bodies).RootElement;
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("previous").ValueKind);
        Assert.StartsWith("RiverCity Pulse readiness is Degraded at startup.", alert.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_webhook_is_logged_not_thrown_and_the_next_change_still_alerts()
    {
        var publisher = Publisher();
        _handler.Status = HttpStatusCode.InternalServerError;

        await publisher.PublishAsync(Report(HealthStatus.Degraded), CancellationToken.None);
        _handler.Status = HttpStatusCode.OK;
        await publisher.PublishAsync(Report(HealthStatus.Healthy), CancellationToken.None);

        Assert.Equal(2, _handler.Bodies.Count);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public List<JsonDocument> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Webhook, request.RequestUri);
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}
