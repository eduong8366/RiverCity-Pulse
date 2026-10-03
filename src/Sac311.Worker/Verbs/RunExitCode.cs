using Sac311.Data.Ingest;

namespace Sac311.Worker.Verbs;

/// <summary>Process exit codes for verbs that run an ingestion job.</summary>
internal static class RunExitCode
{
    /// <summary>0 succeeded, 3 schema drift, 4 skipped (another job holds the lock), 1 failed.</summary>
    public static int For(IngestRun run) => run.Status switch
    {
        RunStatus.Succeeded => 0,
        RunStatus.SchemaDrift => 3,
        RunStatus.Skipped => 4,
        _ => 1,
    };
}
