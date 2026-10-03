using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Sac311.Data;
using Sac311.Data.Ingest;
using Sac311.Data.Quality;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ArcGisClient"/> bound to the <c>ArcGis</c> configuration section, behind the standard
    /// resilience handler: per-attempt timeout, retries with exponential backoff and jitter, a circuit breaker and an
    /// overall timeout. <see cref="ArcGisErrorHandler"/> sits inside it so transient 200-with-error bodies are retried too.
    /// </summary>
    public static IServiceCollection AddArcGisClient(this IServiceCollection services)
    {
        services.AddOptions<ArcGisOptions>().BindConfiguration(ArcGisOptions.SectionName);
        var client = services.AddHttpClient<ArcGisClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<ArcGisOptions>>().Value;
                // Trailing slash so "query" resolves to .../FeatureServer/0/query.
                var url = options.BaseUrl.ToString();
                http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
                // The resilience pipeline owns timeouts.
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("rivercity-pulse/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

        client.AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var options = sp.GetRequiredService<IOptions<ArcGisOptions>>().Value;
                resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;
                resilience.Retry.MaxRetryAttempts = options.MaxRetries;
                resilience.Retry.Delay = options.RetryDelay;
                resilience.Retry.BackoffType = DelayBackoffType.Exponential;
                resilience.Retry.UseJitter = true;
                // The breaker's window must be at least twice the attempt timeout.
                resilience.CircuitBreaker.SamplingDuration = options.AttemptTimeout * 2 > TimeSpan.FromSeconds(30)
                    ? options.AttemptTimeout * 2
                    : TimeSpan.FromSeconds(30);
            });

        // Added after the resilience handler, so it runs inside each attempt.
        client.AddHttpMessageHandler(() => new ArcGisErrorHandler());
        return services;
    }

    /// <summary>Registers the ingestion jobs and the stores they write to, on the <c>Sac311</c> connection string.</summary>
    public static IServiceCollection AddIngestion(this IServiceCollection services)
    {
        services.AddArcGisClient();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new Sac311Db(sp.GetRequiredService<IConfiguration>().GetConnectionString("Sac311")));
        services.AddOptions<IngestOptions>().BindConfiguration(IngestOptions.SectionName);
        services.AddSingleton<IngestLock>();
        services.AddSingleton<RunLog>();
        services.AddSingleton<CheckpointStore>();
        services.AddSingleton<PageWriter>();
        services.AddSingleton<PageProcessor>();
        services.AddSingleton<DqStore>();
        services.AddTransient<DqRunner>();
        services.AddTransient<BackfillJob>();
        services.AddTransient<IncrementalJob>();
        return services;
    }
}
