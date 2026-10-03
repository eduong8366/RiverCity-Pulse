using Microsoft.Data.SqlClient;

namespace Sac311.Data;

/// <summary>Opens connections to the Sac311 database. The connection string may be missing until a verb actually needs it.</summary>
public sealed class Sac311Db(string? connectionString)
{
    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) => OpenAsync(pooling: true, cancellationToken);

    /// <summary>A connection outside the pool, for session state that must end with it (the ingest applock).</summary>
    public Task<SqlConnection> OpenUnpooledAsync(CancellationToken cancellationToken) => OpenAsync(pooling: false, cancellationToken);

    private async Task<SqlConnection> OpenAsync(bool pooling, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No connection string 'Sac311'. Set ConnectionStrings__Sac311 or use the Development environment.");
        }

        var cs = pooling ? connectionString : new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        var connection = new SqlConnection(cs);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
