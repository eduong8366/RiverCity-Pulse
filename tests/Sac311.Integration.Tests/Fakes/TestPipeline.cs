using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sac311.Ingestion;

namespace Sac311.Integration.Tests.Fakes;

/// <summary>
/// The worker's ingestion services (<c>AddIngestion</c>, exactly as the worker registers them) wired to a test
/// database and a <see cref="FakeArcGis"/>, with small pages and no delays.
/// </summary>
internal sealed class TestPipeline : IDisposable
{
    public const int PageSize = 5;

    private readonly ServiceProvider _services;

    public TestPipeline(SqlServerFixture db)
    {
        Source = new FakeArcGis();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sac311"] = db.ConnectionString,
            ["ArcGis:BaseUrl"] = Source.BaseUrl.ToString(),
            ["ArcGis:PageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["ArcGis:PageDelay"] = "00:00:00",
            ["ArcGis:RetryDelay"] = "00:00:00.001",
            ["ArcGis:MaxRetries"] = "2",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddIngestion();
        _services = services.BuildServiceProvider();
    }

    public FakeArcGis Source { get; }

    /// <summary>When the test started; fake rows are dated relative to it.</summary>
    public DateTime NowUtc { get; } = DateTime.UtcNow;

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public Task<Sac311.Data.Ingest.IngestRun> BackfillAsync(BackfillRequest? request = null) =>
        Get<BackfillJob>().RunAsync(request ?? new BackfillRequest(), CancellationToken.None);

    public Task<Sac311.Data.Ingest.IngestRun> IncrementalAsync() => Get<IncrementalJob>().RunAsync(CancellationToken.None);

    public Task<Sac311.Data.Ingest.IngestRun> ReconcileAsync() => Get<ReconcileJob>().RunAsync(CancellationToken.None);

    public void Dispose()
    {
        _services.Dispose();
        Source.Dispose();
    }
}
