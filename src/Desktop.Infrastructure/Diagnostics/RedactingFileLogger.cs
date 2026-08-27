using System.Text.Json;
using System.Text.RegularExpressions;
using Desktop.Application.Diagnostics;
using Desktop.Application.Files;

namespace Desktop.Infrastructure.Diagnostics;

public sealed partial class RedactingFileLogger(IPathPolicy pathPolicy, string relativeLogPath) : IRedactedLogger
{
    private readonly object _gate = new();

    public void Information(string eventCode, IReadOnlyDictionary<string, object?> properties) =>
        Write("information", eventCode, null, properties);

    public void LogError(
        string eventCode,
        string errorCode,
        IReadOnlyDictionary<string, object?> properties) =>
        Write("error", eventCode, errorCode, properties);

    private void Write(
        string level,
        string eventCode,
        string? errorCode,
        IReadOnlyDictionary<string, object?> properties)
    {
        var path = pathPolicy.ResolveOwnedPath(relativeLogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sanitized = properties.ToDictionary(
            pair => pair.Key,
            pair => Sanitize(pair.Key, pair.Value),
            StringComparer.Ordinal);
        var record = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level,
            event_code = eventCode,
            error_code = errorCode,
            properties = sanitized
        };
        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        lock (_gate)
        {
            File.AppendAllText(path, line);
        }
    }

    private static object? Sanitize(string key, object? value)
    {
        if (SensitiveKeyRegex().IsMatch(key))
        {
            return "[REDACTED]";
        }

        if (value is not string text)
        {
            return value;
        }

        var redacted = BearerRegex().Replace(text, "[REDACTED]");
        redacted = WindowsPathRegex().Replace(redacted, "[PATH]");
        redacted = UncPathRegex().Replace(redacted, "[PATH]");
        redacted = MediaNameRegex().Replace(redacted, "[MEDIA]");
        return redacted;
    }

    [GeneratedRegex("(?i)(api.?key|authorization|bearer|credential|password|secret|token)")]
    private static partial Regex SensitiveKeyRegex();

    [GeneratedRegex("(?i)bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)[A-Z]:\\\\[^\\r\\n\"']+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("\\\\\\\\[^\\s\\\"']+")]
    private static partial Regex UncPathRegex();

    [GeneratedRegex("(?i)\\b[^\\s\\\"']+\\.(psd|jpg|jpeg|png|webp|tif|tiff)\\b")]
    private static partial Regex MediaNameRegex();
}
