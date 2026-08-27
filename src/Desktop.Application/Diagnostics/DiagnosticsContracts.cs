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
