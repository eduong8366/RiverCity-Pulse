using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sac311.Ingestion;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Integration.Tests.ArcGis;

/// <summary>Answers each request with the next canned response and records the form fields it was sent.</summary>
internal sealed class StubHandler(params Func<Dictionary<string, string>, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _next;

    public List<(HttpMethod Method, Dictionary<string, string> Form)> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
        Requests.Add((request.Method, form));
        var i = Math.Min(_next++, responses.Length - 1);
        return responses[i](form);
    }

    /// <summary>An <see cref="ArcGisClient"/> wired exactly as the worker wires it, with this stub as the network.</summary>
    public ServiceProvider BuildClient(int pageSize = 2, int maxRetries = 2)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ArcGis:BaseUrl"] = "https://example.test/arcgis/rest/services/Fake/FeatureServer/0",
            ["ArcGis:PageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ArcGis:PageDelay"] = "00:00:00",
            ["ArcGis:RetryDelay"] = "00:00:00.001",
            ["ArcGis:MaxRetries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddArcGisClient();
        services.AddHttpClient<ArcGisClient>().ConfigurePrimaryHttpMessageHandler(() => this);
        return services.BuildServiceProvider();
    }
}
