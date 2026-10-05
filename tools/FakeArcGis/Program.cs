using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

// The incident-drill stand-in for the city's ArcGIS layer (docs/incidents/). It proxies the live layer, so the worker
// sees real data, except for the layer description (layer?f=json), which it serves with one field renamed while drift
// is on. It also takes the API's alert webhook, so the drill shows the alerts without an outside service.
//
//   dotnet run --project tools/FakeArcGis [-- --port 5280] [--rename PublicStatus=Status] [--healthy] [--upstream <layer url>]
//
// Point the worker at http://localhost:<port><layer path> (ArcGis__BaseUrl) and the API at
// http://localhost:<port>/drill/alerts (Alerts__WebhookUrl). POST /drill/drift and /drill/heal switch the rename on and
// off; GET /drill/status and GET /drill/alerts show the state and the alerts received.

const string LiveLayer = "https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/SalesForce311_View/FeatureServer/0";

var port = int.Parse(Option("--port") ?? "5280", CultureInfo.InvariantCulture);
var upstream = new Uri(Option("--upstream") ?? LiveLayer);
var rename = (Option("--rename") ?? "PublicStatus=Status").Split('=', 2);
var drift = !args.Contains("--healthy");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var alerts = new List<JsonNode?>();
var gate = new Lock();

// The layer description doesn't change during a drill, so it is fetched once.
using var http = new HttpClient();
using var layerResponse = await http.PostAsync(upstream, new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }));
var layerJson = await layerResponse.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
var layer = JsonNode.Parse(layerJson)!;
if (layer["fields"]?.AsArray().Any(f => (string?)f?["name"] == rename[0]) != true)
{
    await Console.Error.WriteLineAsync($"The upstream layer has no field '{rename[0]}' to rename.");
    return 1;
}

var server = WireMockServer.Start(new WireMockServerSettings { Port = port, StartAdminInterface = false });
var path = upstream.AbsolutePath.TrimEnd('/');

server.Given(Request.Create().WithPath(path, path + "/").UsingPost())
    .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_ => Layer()));
server.Given(Request.Create().WithPath(path + "/query").UsingPost())
    .RespondWith(Response.Create().WithProxy(upstream.GetLeftPart(UriPartial.Authority)));

server.Given(Request.Create().WithPath("/drill/drift").UsingPost())
    .RespondWith(Response.Create().WithStatusCode(200).WithBody(_ => Switch(on: true)));
server.Given(Request.Create().WithPath("/drill/heal").UsingPost())
    .RespondWith(Response.Create().WithStatusCode(200).WithBody(_ => Switch(on: false)));
server.Given(Request.Create().WithPath("/drill/status").UsingGet())
    .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_ => Status()));
server.Given(Request.Create().WithPath("/drill/alerts").UsingPost())
    .RespondWith(Response.Create().WithStatusCode(204).WithBody(r => Receive(r.Body)));
server.Given(Request.Create().WithPath("/drill/alerts").UsingGet())
    .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_ => Alerts()));

server.LogEntriesChanged += (_, e) =>
{
    foreach (var entry in e.NewItems?.OfType<WireMock.Logging.ILogEntry>() ?? [])
    {
        Print($"{entry.RequestMessage?.Method} {entry.RequestMessage?.Path} -> {entry.ResponseMessage?.StatusCode}");
    }
};

Print($"Fake ArcGIS on {server.Url}{path}, proxying {upstream.Host}");
Print($"Drift {(drift ? "ON" : "off")}: layer?f=json lists {rename[0]} as {rename[1]} while drift is on");
Print($"Alert webhook: {server.Url}/drill/alerts. Ctrl+C to stop.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

server.Stop();
return 0;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Layer()
{
    lock (gate)
    {
        if (!drift)
        {
            return layerJson;
        }

        var copy = layer.DeepClone();
        foreach (var field in copy["fields"]!.AsArray())
        {
            if ((string?)field?["name"] == rename[0])
            {
                field!["name"] = rename[1];
            }
        }

        return copy.ToJsonString();
    }
}

string Switch(bool on)
{
    lock (gate)
    {
        drift = on;
    }

    Print(on ? $"Drift ON: {rename[0]} is now served as {rename[1]}" : "Drift off: the layer description matches the live layer again");
    return Status();
}

string Status()
{
    lock (gate)
    {
        return JsonSerializer.Serialize(new { drift, renamed = new { from = rename[0], to = rename[1] }, upstream, alertsReceived = alerts.Count }, json);
    }
}

string Receive(string? body)
{
    var alert = body is null ? null : JsonNode.Parse(body);
    lock (gate)
    {
        alerts.Add(alert);
    }

    Print("ALERT RECEIVED " + ((string?)alert?["text"])?.Replace("\n", " | ", StringComparison.Ordinal));
    return "";
}

string Alerts()
{
    lock (gate)
    {
        return new JsonArray([.. alerts.Select(a => a?.DeepClone())]).ToJsonString(json);
    }
}

static void Print(string message) =>
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss}] {message}"));
