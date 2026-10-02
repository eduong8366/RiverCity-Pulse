namespace Sac311.Worker.Verbs;

/// <summary>A one-shot command run as <c>worker &lt;name&gt; [args]</c>. Returns the process exit code.</summary>
internal interface IVerb
{
    string Name { get; }

    string Usage { get; }

    Task<int> RunAsync(string[] args, CancellationToken cancellationToken);
}
