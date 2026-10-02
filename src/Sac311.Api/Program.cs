var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "RiverCity Pulse API");

app.Run();
