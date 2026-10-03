using Microsoft.Data.SqlClient;

namespace Sac311.Data;

/// <summary>Opens connections to the Sac311 database. The connection string may be missing until a verb actually needs it.</summary>
public sealed class Sac311Db(string? connectionString)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No connection string 'Sac311'. Set ConnectionStrings__Sac311 or use the Development environment.");
        }

        var connection = new SqlConnection(connectionString);
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
