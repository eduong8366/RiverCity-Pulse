using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Sac311.Data.Migrations;
using Xunit;

namespace Sac311.Testing;

/// <summary>
/// A throwaway database per test class: <c>Sac311_Test_&lt;guid&gt;</c> on the server in <c>SAC311_TEST_SQL</c> (default:
/// the local SQL Express with Windows auth; CI points it at a SQL Server service container). Migrations run once when
/// the class starts, <see cref="ResetAsync"/> empties the data tables between tests (the ref seeds stay), and the
/// database is dropped at the end.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string DefaultServer = @"Server=localhost\SQLEXPRESS;Integrated Security=True;TrustServerCertificate=True";

    public SqlServerFixture()
    {
        var server = Environment.GetEnvironmentVariable("SAC311_TEST_SQL");
        DatabaseName = "Sac311_Test_" + Guid.NewGuid().ToString("N");
        ConnectionString = new SqlConnectionStringBuilder(string.IsNullOrWhiteSpace(server) ? DefaultServer : server) { InitialCatalog = DatabaseName }
            .ConnectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    /// <summary>What the first migration run did (every script, since the database was new).</summary>
    public MigrationResult? FirstMigration { get; private set; }

    public async Task InitializeAsync() =>
        FirstMigration = await Task.Run(() => DatabaseMigrator.Migrate(ConnectionString, NullLogger.Instance));

    public async Task DisposeAsync()
    {
        using (var pooled = new SqlConnection(ConnectionString))
        {
            SqlConnection.ClearPool(pooled);
        }

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"""
            IF DB_ID('{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """);
    }

    /// <summary>Deletes every row the pipeline writes, children first; ref tables and the migration journal stay.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("""
            DELETE FROM ops.dq_result;
            DELETE FROM ops.ingest_reject;
            DELETE FROM ops.ingest_checkpoint;
            DELETE FROM raw.page;
            DELETE FROM dbo.service_request_history;
            DELETE FROM dbo.service_request;
            TRUNCATE TABLE stg.request;
            TRUNCATE TABLE stg.reconcile_key;
            DELETE FROM ops.ingest_run;
            """);
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, param))!;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<T>(sql, param)).AsList();
    }
}
