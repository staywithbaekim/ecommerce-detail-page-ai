namespace Desktop.Application.Rendering;

public enum PhotoshopConnectionCheckStatus
{
    Ready,
    PairingRequired,
    Busy,
    TimedOut,
    HostNotReady,
    Failed,
    HostSelectionRequired,
    HostSelectionInvalid
}

/// <summary>A completed, historical check, not a retained connection or production admission.</summary>
public sealed record PhotoshopConnectionCheckResult(
    PhotoshopConnectionCheckStatus Status,
    DateTimeOffset ObservedAt,
    long AuthenticatedHeartbeats = 0,
    string? MatchedPhotoshopBuild = null,
    bool InstanceIdentityRequired = false);

/// <summary>Explicit, bounded authentication check using an existing device pairing. Never submits rendering work.</summary>
public interface IPhotoshopConnectionCheckPort
{
    /// <summary>Process-local history only. Reading this must not start a check or access protected storage.</summary>
    PhotoshopConnectionCheckResult? Latest { get; }

    Task<PhotoshopConnectionCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Same authenticated check with the production page-instance contract required. Legacy implementations fail closed.</summary>
    Task<PhotoshopConnectionCheckResult> CheckInstanceIdentityAsync(CancellationToken cancellationToken) =>
        Task.FromException<PhotoshopConnectionCheckResult>(new InvalidOperationException("BRIDGE_INSTANCE_CHECK_UNSUPPORTED"));
}
