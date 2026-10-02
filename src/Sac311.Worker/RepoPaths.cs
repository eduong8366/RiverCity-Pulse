namespace Sac311.Worker;

/// <summary>Finds the repository root (the folder holding Sac311.slnx) for dev-tooling verbs that write into the repo.</summary>
internal static class RepoPaths
{
    public static string? FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Sac311.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }
}
