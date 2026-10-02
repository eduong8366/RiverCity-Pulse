using DbUp;
using DbUp.Engine;
using DbUp.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Sac311.Data.Migrations;

/// <summary>What one <see cref="DatabaseMigrator.Migrate"/> call did.</summary>
/// <param name="Database">The target database name.</param>
/// <param name="Migrations">One-time migrations applied by this call (empty when the schema was current).</param>
/// <param name="RepeatableScripts">Programmable and seed scripts run (they run on every call).</param>
public sealed record MigrationResult(string Database, IReadOnlyList<string> Migrations, int RepeatableScripts);

/// <summary>
/// Brings a database up to date from the SQL embedded in this assembly (copied from /db at build time):
/// <list type="number">
/// <item><c>migrations/*.sql</c>: one-time, journaled in <c>dbo.schema_version</c>.</item>
/// <item><c>programmable/*.sql</c>: <c>CREATE OR ALTER</c> procs and views, run every time.</item>
/// <item><c>seed/*.sql</c>: idempotent upserts of the <c>ref</c> tables, run every time.</item>
/// </list>
/// The database is created first if it doesn't exist. Each script runs in its own transaction.
/// </summary>
public static class DatabaseMigrator
{
    private const int MigrationGroup = 1;
    private const int ProgrammableGroup = 2;
    private const int SeedGroup = 3;

    public static MigrationResult Migrate(string connectionString, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        var log = new UpgradeLogAdapter(logger);
        EnsureDatabase.For.SqlDatabase(connectionString, log);

        var assembly = typeof(DatabaseMigrator).Assembly;
        var engine = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(assembly, n => n.StartsWith("migrations.", StringComparison.Ordinal),
                new SqlScriptOptions { ScriptType = ScriptType.RunOnce, RunGroupOrder = MigrationGroup })
            .WithScriptsEmbeddedInAssembly(assembly, n => n.StartsWith("programmable.", StringComparison.Ordinal),
                new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = ProgrammableGroup })
            .WithScriptsEmbeddedInAssembly(assembly, n => n.StartsWith("seed.", StringComparison.Ordinal),
                new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = SeedGroup })
            .JournalToSqlTable("dbo", "schema_version")
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .WithExecutionTimeout(TimeSpan.FromMinutes(10))
            .LogTo(log)
            .Build();

        var result = engine.PerformUpgrade();
        if (!result.Successful)
        {
            throw new MigrationException(
                $"Migration failed in script '{result.ErrorScript?.Name ?? "(none)"}': {result.Error?.Message}", result.Error);
        }

        var applied = result.Scripts.Where(s => s.SqlScriptOptions.ScriptType == ScriptType.RunOnce).Select(s => s.Name).ToList();
        var repeatable = result.Scripts.Count(s => s.SqlScriptOptions.ScriptType == ScriptType.RunAlways);
        return new MigrationResult(new SqlConnectionStringBuilder(connectionString).InitialCatalog, applied, repeatable);
    }
}

public sealed class MigrationException(string message, Exception? inner) : Exception(message, inner);
