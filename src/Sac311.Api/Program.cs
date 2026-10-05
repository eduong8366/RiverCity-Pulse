using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Sac311.Api.Data;
using Sac311.Api.Endpoints;
using Sac311.Api.Health;
using Sac311.Data;
using Sac311.Data.Aggregates;

const string CachePolicy = "api";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new Sac311Db(sp.GetRequiredService<IConfiguration>().GetConnectionString("Sac311")));
builder.Services.AddSingleton<AggregateStore>();
builder.Services.AddSingleton<StatsReader>();
builder.Services.AddSingleton<FreshnessReader>();
builder.Services.AddSingleton<ExclusionReader>();
builder.Services.AddSingleton<ClearOutReader>();
builder.Services.AddOptions<FreshnessOptions>().BindConfiguration(FreshnessOptions.SectionName);
builder.Services.AddOptions<AlertOptions>().BindConfiguration(AlertOptions.SectionName);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "RiverCity Pulse API",
        Version = "v1",
        Description = "How long Sacramento 311 requests take to close, by neighborhood, district and category. "
            + "Derived from the City of Sacramento's open 311 data; not official city figures.",
    };
    return Task.CompletedTask;
}));

// The aggregates change at most once per worker run (every 15 minutes), so responses are cached for 5.
builder.Services.AddOutputCache(options => options.AddPolicy(CachePolicy, policy => policy.Expire(TimeSpan.FromMinutes(5)).SetVaryByQuery("*")));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready])
    .AddCheck<FreshnessHealthCheck>("freshness", tags: [HealthTags.Ready])
    .AddCheck<IngestionHealthCheck>("ingestion", tags: [HealthTags.Ready]);

// The freshness monitor: the readiness checks also run in the background, and a change in status is an alert.
builder.Services.AddHttpClient(AlertPublisher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IHealthCheckPublisher, AlertPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.FromSeconds(10);
    options.Period = builder.Configuration.GetSection(AlertOptions.SectionName).Get<AlertOptions>()?.Period ?? new AlertOptions().Period;
    options.Predicate = check => check.Tags.Contains(HealthTags.Ready);
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseOutputCache();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "RiverCity Pulse API v1");
    options.DocumentTitle = "RiverCity Pulse API";
});
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Liveness runs no checks; readiness checks the database, data freshness and the last run (Degraded when stale or
// after schema drift, still 200).
app.MapHealthChecks("/api/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponse.WriteAsync });
app.MapHealthChecks("/api/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthTags.Ready),
    ResponseWriter = HealthResponse.WriteAsync,
});

app.MapGroup("/api")
    .CacheOutput(CachePolicy)
    .MapMetaEndpoints()
    .MapStatsEndpoints()
    .MapGeoEndpoints();

await app.RunAsync();
