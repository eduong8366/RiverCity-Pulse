var builder = Host.CreateApplicationBuilder(args);

// Verbs (run | migrate | backfill | incremental | reconcile | ...) are added in later sessions.

var host = builder.Build();
host.Run();
