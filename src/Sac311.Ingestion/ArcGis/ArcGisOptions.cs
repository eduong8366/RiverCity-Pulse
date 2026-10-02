namespace Sac311.Ingestion.ArcGis;

public sealed class ArcGisOptions
{
    public const string SectionName = "ArcGis";

    /// <summary>The feature layer URL (ends in <c>/FeatureServer/0</c>). Point it at a fake server for drills and tests.</summary>
    public Uri BaseUrl { get; set; } = new("https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/SalesForce311_View/FeatureServer/0");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
