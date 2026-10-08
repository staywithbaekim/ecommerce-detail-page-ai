using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.App;

internal static class StartupDiagnostics
{
    internal const string DisplayLogPath = "logs\\desktop-v1.jsonl";

    internal static bool TryLogFailure(
        DesktopDataOptions? dataOptions,
        string eventCode,
        string phase,
        Exception exception)
    {
        // Argument rejection has no trusted root. Never invent a default log path.
        if (dataOptions is null) return false;
        try
        {
            var logger = new RedactingFileLogger(
                new LocalAppPathPolicy(dataOptions.RootDirectory),
                DisplayLogPath);
            logger.LogError(
                eventCode,
                exception.GetType().Name,
                new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["hresult"] = exception.HResult,
                    ["exception"] = exception.ToString()
                });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
