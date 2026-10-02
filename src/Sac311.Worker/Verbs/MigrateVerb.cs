using Sac311.Data.Migrations;

namespace Sac311.Worker.Verbs;

internal sealed partial class MigrateVerb(IConfiguration configuration, ILogger<MigrateVerb> logger) : IVerb
{
    public string Name => "migrate";

    public string Usage => "migrate                 create the database if missing, apply migrations, programmable objects and seeds";

    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("Sac311");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogMissingConnectionString(logger);
            return Task.FromResult(1);
        }

        try
        {
            var result = DatabaseMigrator.Migrate(connectionString, logger);
            LogDone(logger, result.Database, result.Migrations.Count, result.RepeatableScripts);
            return Task.FromResult(0);
        }
        catch (MigrationException ex)
        {
            LogFailed(logger, ex);
            return Task.FromResult(1);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No connection string 'Sac311'. Set ConnectionStrings__Sac311 or use the Development environment.")]
    private static partial void LogMissingConnectionString(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {Database} is up to date: {Migrations} new migration(s), {Repeatable} programmable/seed script(s) run.")]
    private static partial void LogDone(ILogger logger, string database, int migrations, int repeatable);

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration failed.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
