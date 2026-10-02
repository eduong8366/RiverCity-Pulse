namespace Sac311.Ingestion.ArcGis;

public sealed class ArcGisOptions
{
    public const string SectionName = "ArcGis";

    /// <summary>The feature layer URL (ends in <c>/FeatureServer/0</c>). Point it at a fake server for drills and tests.</summary>
    public Uri BaseUrl { get; set; } = new("https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/SalesForce311_View/FeatureServer/0");

    /// <summary>Rows per keyset page. The layer's maxRecordCount is 2,000.</summary>
    public int PageSize { get; set; } = 2000;

    /// <summary>Pause between pages, to stay polite to a free public service.</summary>
    public TimeSpan PageDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Timeout for one HTTP attempt, including reading the body.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after the first attempt (exponential backoff with jitter).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Base delay of the exponential backoff.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap on one logical request across all its attempts.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
