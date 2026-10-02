using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace Sac311.Data.Migrations;

/// <summary>Routes DbUp's composite-format log calls to an <see cref="ILogger"/>.</summary>
internal sealed class UpgradeLogAdapter(ILogger logger) : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => Write(LogLevel.Trace, null, format, args);

    public void LogDebug(string format, params object[] args) => Write(LogLevel.Debug, null, format, args);

    public void LogInformation(string format, params object[] args) => Write(LogLevel.Information, null, format, args);

    public void LogWarning(string format, params object[] args) => Write(LogLevel.Warning, null, format, args);

    public void LogError(string format, params object[] args) => Write(LogLevel.Error, null, format, args);

    public void LogError(Exception ex, string format, params object[] args) => Write(LogLevel.Error, ex, format, args);

    private void Write(LogLevel level, Exception? ex, string format, object[] args)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var message = args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
#pragma warning disable CA1848, CA2254 // DbUp hands us preformatted text; a LoggerMessage delegate adds nothing here.
        logger.Log(level, ex, "{Message}", message);
#pragma warning restore CA1848, CA2254
    }
}
