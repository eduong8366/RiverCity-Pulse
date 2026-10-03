using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Sac311.Data.Ingest;

/// <summary>
/// The single-writer guard shared by every ingestion job: <c>sp_getapplock 'sac311-ingest'</c>, exclusive, owned by
/// the session, with a timeout of 0. The lock lives on its own unpooled connection held open for the whole job, so a
/// killed process releases it when its connection drops, and a returned-to-pool connection can never keep it.
/// </summary>
public sealed class IngestLock(Sac311Db db)
{
    public const string Resource = "sac311-ingest";

    /// <summary>Takes the lock without waiting. Returns null when another job holds it; dispose the result to release it.</summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var conn = await db.OpenUnpooledAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var p = new DynamicParameters();
            p.Add("@Resource", Resource);
            p.Add("@LockMode", "Exclusive");
            p.Add("@LockOwner", "Session");
            p.Add("@LockTimeout", 0);
            p.Add("@result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
            await conn.ExecuteAsync(new CommandDefinition("sp_getapplock", p, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // 0 = granted, 1 = granted after waiting; negative = timeout, deadlock, cancelled or error.
            if (p.Get<int>("@result") >= 0)
            {
                return new Held(conn);
            }

            await conn.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Held(SqlConnection conn) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "sp_releaseapplock", new { Resource, LockOwner = "Session" }, commandType: CommandType.StoredProcedure)).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // Closing the (unpooled) connection below ends the session, which releases the lock anyway.
            }
            finally
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
