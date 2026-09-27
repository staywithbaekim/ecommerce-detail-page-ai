namespace Desktop.Application.Diagnostics;

public interface IRedactedLogger
{
    void Information(string eventCode, IReadOnlyDictionary<string, object?> properties);

    void LogError(string eventCode, string errorCode, IReadOnlyDictionary<string, object?> properties);
}

public sealed record AppInstanceRecord(
    string InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    bool SafeExit);

public interface IAppInstanceStore
{
    Task RecordStartAsync(AppInstanceRecord instance, CancellationToken cancellationToken);

    Task RecordSafeExitAsync(string instanceId, DateTimeOffset exitedAt, CancellationToken cancellationToken);
}

public enum DiagnosticPhotoshopEnvironmentStatus
{
    NotCollected = 0,
    HistoricalExecutableMetadata = 1
}

public enum DiagnosticPhotoshopConnectionStatus
{
    NotCollected = 0,
    HistoricalCheck = 1,
    HistoricalAuthenticatedBuildMatch = 2
}

public sealed record DiagnosticPackageResult(
    int ExportedRecords,
    int OmittedRecords,
    bool Truncated,
    string LogStatus,
    DiagnosticPhotoshopEnvironmentStatus PhotoshopEnvironmentStatus = DiagnosticPhotoshopEnvironmentStatus.NotCollected,
    DiagnosticPhotoshopConnectionStatus PhotoshopConnectionStatus = DiagnosticPhotoshopConnectionStatus.NotCollected);

/// <summary>Explicit, local-only export. Never reads credentials or business media.</summary>
public interface IDiagnosticPackageExporter
{
    Task<DiagnosticPackageResult> ExportAsync(string destinationPath, CancellationToken cancellationToken);
}
