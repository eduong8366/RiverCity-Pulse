using Sac311.Ingestion;
using Sac311.Worker.Verbs;
using Serilog;

// The first argument picks the verb; with none (or only options) the worker runs as a host.
var verbName = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";
var verbArgs = args.Length > 0 && args[0] == verbName ? args[1..] : args;

var builder = Host.CreateApplicationBuilder(verbArgs);
// Sinks and levels come from the Serilog section: readable console, compact JSON (CLEF) files under logs/.
builder.Services.AddSerilog((services, log) => log.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services));
builder.Services.AddIngestion();
builder.Services.AddTransient<IVerb, MigrateVerb>();
builder.Services.AddTransient<IVerb, CaptureFixtureVerb>();
builder.Services.AddTransient<IVerb, VerifySourceVerb>();
builder.Services.AddTransient<IVerb, BackfillVerb>();
builder.Services.AddTransient<IVerb, IncrementalVerb>();

using var host = builder.Build();

if (verbName == "run")
{
    // The scheduler (BackgroundService + PeriodicTimer) is added in a later session.
    await host.RunAsync();
    return 0;
}

var verbs = host.Services.GetServices<IVerb>().ToList();
var verb = verbs.Find(v => v.Name == verbName);
if (verb is null)
{
    await Console.Error.WriteLineAsync($"Unknown verb '{verbName}'. Usage: worker <verb> [args]");
    await Console.Error.WriteLineAsync("  run                     run the scheduler host (default)");
    foreach (var v in verbs)
    {
        await Console.Error.WriteLineAsync("  " + v.Usage);
    }

    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
return await verb.RunAsync(verbArgs, cts.Token);
