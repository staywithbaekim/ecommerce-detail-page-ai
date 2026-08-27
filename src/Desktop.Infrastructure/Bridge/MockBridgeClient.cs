using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

public sealed class MockBridgeClient(
    IRenderPlanValidator renderPlanValidator,
    TimeProvider? timeProvider = null) : IBridgeClient
{
    public const int MaxPlanBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BridgeExecution> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly object _submissionGate = new();

    public Task<BridgeResult<BridgeSession>> HandshakeAsync(
        BridgeHandshakeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.ProtocolVersion, "ps-bridge/v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.ClientInstanceId)
            || request.Nonce.Length != 43
            || string.Equals(request.PairingCode, "reject", StringComparison.Ordinal))
        {
            return Task.FromResult(BridgeResults.Failure<BridgeSession>(
                "BRIDGE_HANDSHAKE_REJECTED",
                retryable: false,
                scope: "session",
                userAction: "请重新检查 Photoshop 插件版本和配对状态。"));
        }

        var now = _timeProvider.GetUtcNow();
        var token = CreateToken();
        var session = new BridgeSession(
            "ps-bridge/v1",
            $"session-{Guid.NewGuid():N}",
            token,
            now.Add(TokenLifetime));
        _sessions[token] = session;
        return Task.FromResult(BridgeResults.Success(session));
    }

    public Task<BridgeResult<BridgeExecution>> SubmitAsync(
        BridgeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(request.RenderPlanJson) > MaxPlanBytes)
        {
            return Task.FromResult(Failure(
                "BRIDGE_REQUEST_TOO_LARGE",
                retryable: false,
                "request",
                "请缩小计划数据后重新提交。"));
        }

        var tokenFailure = ValidateToken(request.AccessToken);
        if (tokenFailure is not null)
        {
            return Task.FromResult(tokenFailure);
        }

        var contractResult = renderPlanValidator.Validate(request.RenderPlanJson);
        if (!contractResult.IsValid)
        {
            return Task.FromResult(Failure(
                "BRIDGE_RENDER_PLAN_REJECTED",
                retryable: false,
                "request",
                "请修正渲染计划后重新提交。"));
        }

        lock (_submissionGate)
        {
            if (_idempotency.TryGetValue(request.IdempotencyKey, out var existingId)
                && _executions.TryGetValue(existingId, out var existingExecution))
            {
                return Task.FromResult(BridgeResults.Success(existingExecution));
            }

            var execution = new BridgeExecution(
                $"execution-{Guid.NewGuid():N}",
                BridgeExecutionState.Queued,
                _timeProvider.GetUtcNow());
            _executions[execution.ExecutionId] = execution;
            _idempotency[request.IdempotencyKey] = execution.ExecutionId;
            return Task.FromResult(BridgeResults.Success(execution));
        }
    }

    public Task<BridgeResult<BridgeExecution>> QueryAsync(
        string executionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokenFailure = ValidateToken(accessToken);
        if (tokenFailure is not null)
        {
            return Task.FromResult(tokenFailure);
        }

        return Task.FromResult(_executions.TryGetValue(executionId, out var execution)
            ? BridgeResults.Success(execution)
            : Failure(
                "BRIDGE_EXECUTION_NOT_FOUND",
                retryable: false,
                "execution",
                "请返回任务页检查最近安全检查点。"));
    }

    public Task<bool> HeartbeatAsync(string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ValidateToken(accessToken) is null);
    }

    private BridgeResult<BridgeExecution>? ValidateToken(string accessToken)
    {
        if (!_sessions.TryGetValue(accessToken, out var session)
            || session.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _sessions.TryRemove(accessToken, out _);
            return Failure(
                "BRIDGE_TOKEN_INVALID",
                retryable: false,
                "session",
                "请重新连接 Photoshop。");
        }

        return null;
    }

    private static BridgeResult<BridgeExecution> Failure(
        string code,
        bool retryable,
        string scope,
        string userAction) =>
        BridgeResults.Failure<BridgeExecution>(code, retryable, scope, userAction);

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
