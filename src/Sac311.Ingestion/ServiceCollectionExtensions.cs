using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ArcGisClient"/> bound to the <c>ArcGis</c> configuration section.</summary>
    public static IServiceCollection AddArcGisClient(this IServiceCollection services)
    {
        services.AddOptions<ArcGisOptions>().BindConfiguration(ArcGisOptions.SectionName);
        services.AddHttpClient<ArcGisClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<ArcGisOptions>>().Value;
                // Trailing slash so "query" resolves to .../FeatureServer/0/query.
                var url = options.BaseUrl.ToString();
                http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
                http.Timeout = options.Timeout;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("rivercity-pulse/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        return services;
    }
}
