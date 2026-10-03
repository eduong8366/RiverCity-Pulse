namespace Sac311.Integration.Tests.Fakes;

/// <summary>
/// Base for tests against SQL Server: one database per test class (<see cref="SqlServerFixture"/>), emptied before
/// each test, and a fresh <see cref="TestPipeline"/> with its own fake source per test.
/// </summary>
public abstract class DatabaseTest(SqlServerFixture db) : IClassFixture<SqlServerFixture>, IAsyncLifetime, IDisposable
{
    private TestPipeline? _pipeline;

    protected SqlServerFixture Db { get; } = db;

    internal TestPipeline Worker => _pipeline ?? throw new InvalidOperationException("Not initialized.");

    internal FakeArcGis Source => Worker.Source;

    protected DateTime NowUtc => Worker.NowUtc;

    public async Task InitializeAsync()
    {
        await Db.ResetAsync();
        _pipeline = new TestPipeline(Db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _pipeline?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Asserts a run's status, showing its error when it differs.</summary>
    protected static void AssertStatus(string expected, Sac311.Data.Ingest.IngestRun run) =>
        Assert.True(run.Status == expected, $"Expected {expected}, got {run.Status}: {run.Error}");

    protected Task<int> CountAsync(string table, string where = "1=1") => Db.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {where};");
}
