using Microsoft.Extensions.Logging;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <summary>The first step of every run: fetch <c>layer?f=json</c> and check it against <see cref="SchemaContract"/>.</summary>
internal static partial class SchemaGuard
{
    /// <summary>Returns the check result, logging drift (critical) and added fields (warning).</summary>
    public static async Task<SchemaCheckResult> CheckAsync(ArcGisClient client, ILogger logger, CancellationToken cancellationToken)
    {
        using var layer = await client.GetLayerAsync(cancellationToken).ConfigureAwait(false);
        var contract = SchemaContract.Check(layer.RootElement);
        if (contract.IsDrift)
        {
            LogSchemaDrift(logger, contract);
        }
        else if (contract.Added.Count > 0)
        {
            LogAddedFields(logger, string.Join(", ", contract.Added));
        }

        return contract;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Schema drift, stopping without writing: {Contract}")]
    private static partial void LogSchemaDrift(ILogger logger, SchemaCheckResult contract);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The layer has fields the contract doesn't know: {Fields}")]
    private static partial void LogAddedFields(ILogger logger, string fields);
}
