using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Bridge;

public sealed record BridgeHandshakeRequest(
    string ProtocolVersion,
    string ClientInstanceId,
    string Nonce,
    string? PairingCode = null);

public sealed record BridgeSession(
    string ProtocolVersion,
    string SessionId,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record BridgeExecutionRequest(
    string RequestId,
    string IdempotencyKey,
    string CorrelationId,
    string RenderPlanJson,
    string AccessToken);

public enum BridgeExecutionState
{
    Queued = 0,
    Running,
    Succeeded,
    Failed,
    Unknown
}

public sealed record BridgeExecution(
    string ExecutionId,
    BridgeExecutionState State,
    DateTimeOffset UpdatedAt);

public sealed record BridgeResult<T>(T? Value, ContractError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class BridgeResults
{
    public static BridgeResult<T> Success<T>(T value) => new(value, null);

    public static BridgeResult<T> Failure<T>(
        string code,
        bool retryable,
        string scope,
        string userAction) =>
        new(default, new ContractError(code, retryable, scope, userAction));
}
