using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

public sealed record BridgeAdapterSessionPolicy(
    string TaskId, string AppVersion, string PhotoshopBuild, IReadOnlyList<string> Capabilities,
    DateTimeOffset? OneTimeCodeExpiresAt = null);

public sealed record BridgeHttpResponse(int StatusCode, string Body, string? Mac = null)
{
    public override string ToString() => $"{nameof(BridgeHttpResponse)} {{ StatusCode = {StatusCode} }}";
}

public sealed record BridgePairedSession(string SessionId, string Token, string PluginInstanceId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(BridgePairedSession);
}

public sealed record BridgeSessionHealth(bool Connected, bool Ready, long Heartbeats, int LastEventSeq = 0,
    string? HostState = null, string? ModalState = null, DateTimeOffset? LastHeartbeatAt = null);

/// <summary>One task/Photoshop session; HTTP ownership and durable execution state live outside this class.</summary>
public sealed class BridgeAdapterSessions : IDisposable
{
    private readonly byte[] _secret;
    private readonly BridgeAdapterSessionPolicy _policy;
    private readonly BridgeWireValidator _wire;
    private readonly BridgeAdapterValidator _adapter;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AdapterChallenge> _challenges = new(StringComparer.Ordinal);
    private Session? _session;
    private bool _disposed;
    private bool _pairingConsumed;

    public BridgeAdapterSessions(byte[] secret, BridgeAdapterSessionPolicy policy, BridgeWireValidator wire,
        BridgeAdapterValidator adapter, TimeProvider? time = null)
    {
        if (secret.Length != 32 || string.IsNullOrWhiteSpace(policy.TaskId)
            || !policy.Capabilities.Contains(BridgeAdapterProtocol.Capability, StringComparer.Ordinal))
            throw new ArgumentException("BRIDGE_SESSION_CONFIGURATION_INVALID");
        _secret = secret.ToArray();
        _policy = policy with { Capabilities = policy.Capabilities.ToArray() };
        _wire = wire; _adapter = adapter; _time = time ?? TimeProvider.System;
    }

    public string TaskId => _policy.TaskId;

    public async Task<BridgeResult<AdapterChallenge>> ChallengeAsync(string json, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanPair()) return Failure<AdapterChallenge>("BRIDGE_PAIRING_REJECTED");
            if (!_adapter.Validate(json, "challenge_request").IsValid) return Failure<AdapterChallenge>("BRIDGE_PROTOCOL_MISMATCH");
            var request = JsonSerializer.Deserialize<AdapterEnvelope<AdapterChallengeRequest>>(json, BridgeProtocol.JsonOptions)!.Body;
            if (request.PluginVersion != BridgeAdapterProtocol.PluginVersion || request.PhotoshopBuild != _policy.PhotoshopBuild
                || !_policy.Capabilities.All(c => request.Capabilities.Contains(c, StringComparer.Ordinal)))
                return Failure<AdapterChallenge>("BRIDGE_PROTOCOL_MISMATCH");
            try { _ = BridgeAdapterCryptography.Decode32(request.ClientNonce); }
            catch (FormatException) { return Failure<AdapterChallenge>("BRIDGE_PAIRING_REJECTED"); }
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            foreach (var expired in _challenges.Where(x => x.Value.ExpiresAtUnixSeconds <= now).Select(x => x.Key).ToArray())
                _challenges.Remove(expired);
            if (_challenges.Count >= 4 || _challenges.Values.Any(c => c.ClientNonce == request.ClientNonce))
                return Failure<AdapterChallenge>("BRIDGE_PAIRING_REJECTED");
            var challenge = new AdapterChallenge(request.RequestId, request.ClientNonce,
                BridgeAdapterCryptography.RandomToken(), $"session_{Guid.NewGuid():N}", request.PluginInstanceId,
                BridgeAdapterCryptography.RandomToken(), _policy.AppVersion, BridgeAdapterProtocol.PluginId,
                BridgeAdapterProtocol.PluginVersion, request.PhotoshopBuild, request.UxpRuntimeVersion,
                now, now + 30, _policy.Capabilities.Order(StringComparer.Ordinal).ToArray(), "");
            challenge = challenge with { ServerProof = BridgeAdapterCryptography.ChallengeProof(_secret, challenge) };
            _challenges.Add(challenge.SessionId, challenge);
            return BridgeResults.Success(challenge);
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeHttpResponse> PairAsync(string json, string contextProof, int executionPort,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanPair()) return Rejection("BRIDGE_PAIRING_REJECTED");
            if (!_wire.Validate(json, BridgeEndpoint.Handshake).IsValid)
            {
                DiscardChallenge(json);
                return Rejection("BRIDGE_PROTOCOL_MISMATCH");
            }
            BridgeHandshakeBodyV1 request;
            try { request = JsonSerializer.Deserialize<BridgeWireEnvelope<BridgeHandshakeBodyV1>>(json, BridgeProtocol.JsonOptions)!.Body; }
            catch (JsonException) { return Rejection("BRIDGE_PROTOCOL_MISMATCH"); }
            if (!_challenges.Remove(request.SessionId, out var challenge)) return Rejection("BRIDGE_PAIRING_REJECTED");
            var now = _time.GetUtcNow();
            if (now.ToUnixTimeSeconds() >= challenge.ExpiresAtUnixSeconds || now.ToUnixTimeSeconds() < challenge.IssuedAtUnixSeconds
                || Math.Abs((now - request.SentAt).TotalSeconds) > 30
                || executionPort is < BridgeProtocol.MinExecutionPort or > BridgeProtocol.MaxExecutionPort
                || request.Nonce != challenge.Nonce || request.RequestId != challenge.RequestId
                || request.Client.AppVersion != challenge.AppVersion || request.Client.ProtocolVersion != BridgeProtocol.Version
                || request.Host.PluginManifestVersion != challenge.PluginVersion || request.Host.PhotoshopBuild != challenge.PhotoshopBuild
                || request.Host.UxpRuntimeVersion != challenge.UxpRuntimeVersion
                || request.Client.RequestedCapabilities is null
                || !request.Client.RequestedCapabilities.Order(StringComparer.Ordinal).SequenceEqual(challenge.Capabilities)
                || !BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.HandshakeContextProof(_secret, challenge, json), contextProof)
                || !BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.PairingProof(_secret, request), request.PairingProof))
                return Rejection("BRIDGE_PAIRING_REJECTED");
            Invalidate();
            var token = BridgeAdapterCryptography.RandomToken();
            _session = new Session(challenge.SessionId, challenge.PluginInstanceId, token,
                BridgeAdapterCryptography.SessionKey(_secret, challenge), now);
            if (_policy.OneTimeCodeExpiresAt is not null)
            {
                _pairingConsumed = true;
                _challenges.Clear();
                CryptographicOperations.ZeroMemory(_secret);
            }
            var response = new BridgeHandshakeResultV1(true, request.RequestId, request.CorrelationId, "paired", now,
                _session.Id, executionPort, token, now.AddMinutes(15), challenge.Capabilities);
            var body = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeHandshakeResultV1>(
                BridgeProtocol.Version, "handshake_response", response), BridgeProtocol.JsonOptions);
            return new(200, body, BridgeAdapterCryptography.HandshakeResponseProof(_session.Key, request.RequestId, body));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Serializes authenticated operations and caches the last response for safe same-request replay.</summary>
    public async Task<BridgeHttpResponse> ExecuteAsync(AdapterRequestAuthentication auth, string method, string path,
        string body, Func<CancellationToken, Task<BridgeHttpResponse>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = LiveSession();
            if (session is null || auth.SessionId != session.Id || auth.PluginInstanceId != session.Instance
                || auth.Sequence < 1 || auth.Sequence > 9007199254740991
                || (method == "GET" && auth.Query is null)
                || !BridgeAdapterCryptography.FixedEquals(session.Token, auth.Token)
                || !BridgeAdapterCryptography.FixedEquals(BridgeAdapterCryptography.RequestMac(session.Key, auth, method, path, body), auth.Mac))
                return Rejection("BRIDGE_TOKEN_INVALID");
            // MAC covers direction, method, exact route/query, identity, sequence, token and raw UTF-8 body.
            if (auth.Sequence == session.LastSequence && session.LastRequestMac == auth.Mac && session.LastResponse is not null)
                return session.LastResponse;
            if (auth.Sequence != session.LastSequence + 1) return Rejection("BRIDGE_TOKEN_INVALID");
            BridgeHttpResponse result;
            try
            {
                result = await operation(cancellationToken).ConfigureAwait(false);
                if (System.Text.Encoding.UTF8.GetByteCount(result.Body) > ResponseByteLimit(method, path))
                    throw new InvalidDataException("BRIDGE_REQUEST_TOO_LARGE");
            }
            catch
            {
                // After dispatch, caller cannot safely reuse sequence or assume no side effects.
                Invalidate();
                throw;
            }
            result = result with { Mac = BridgeAdapterCryptography.ResponseMac(session.Key, auth, result.StatusCode, result.Body) };
            session.LastSequence = auth.Sequence;
            session.LastRequestMac = auth.Mac;
            session.LastResponse = result;
            session.LastActivity = _time.GetUtcNow();
            if (method == "POST" && path == "/heartbeat" && result.StatusCode == 200)
            {
                using var heartbeat = JsonDocument.Parse(body);
                var payload = heartbeat.RootElement.GetProperty("body").GetProperty("payload");
                session.LastHeartbeat = session.LastActivity;
                session.Heartbeats++;
                session.LastEventSeq = payload.GetProperty("last_event_seq").GetInt32();
                session.HostState = payload.GetProperty("host_state").GetString();
                session.ModalState = payload.GetProperty("modal_state").GetString();
                session.Ready = session.HostState == "ready" && session.ModalState == "idle";
            }
            if (method == "POST" && path == "/release") Invalidate();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal static int ResponseByteLimit(string method, string path) => path == "/adapter/v1/claims"
        ? BridgeAdapterValidator.ByteLimit("claim_response")
        : method == "GET" || path is "/heartbeat" or "/adapter/v1/resources/read"
            || path.StartsWith("/adapter/v1/events", StringComparison.Ordinal)
            ? BridgeProtocol.StatusByteLimit : BridgeProtocol.MessageByteLimit;

    public async Task<BridgePairedSession?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = LiveSession();
            return session is null ? null : new(session.Id, session.Token, session.Instance,
                session.LastActivity.AddMinutes(15) < session.Created.AddHours(8)
                    ? session.LastActivity.AddMinutes(15) : session.Created.AddHours(8));
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeSessionHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = LiveSession();
            var connected = session is not null && _time.GetUtcNow() >= session.LastHeartbeat
                && _time.GetUtcNow() - session.LastHeartbeat < TimeSpan.FromSeconds(15);
            return new(connected, connected && session!.Ready, session?.Heartbeats ?? 0, session?.LastEventSeq ?? 0,
                connected ? session!.HostState : null, connected ? session!.ModalState : null,
                session is { Heartbeats: > 0 } ? session.LastHeartbeat : null);
        }
        finally { _gate.Release(); }
    }

    private Session? LiveSession()
    {
        var now = _time.GetUtcNow();
        if (_session is not null && (now < _session.Created || now >= _session.Created.AddHours(8)
            || now >= _session.LastActivity.AddMinutes(15))) Invalidate();
        return _session;
    }

    private bool CanPair() => !_pairingConsumed
        && (_policy.OneTimeCodeExpiresAt is null || _time.GetUtcNow() < _policy.OneTimeCodeExpiresAt);

    private void DiscardChallenge(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MessageByteLimit) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String)
                _challenges.Remove(id.GetString()!);
        }
        catch (JsonException) { }
    }

    private void Invalidate()
    {
        if (_session is not null) CryptographicOperations.ZeroMemory(_session.Key);
        _session = null;
    }

    public static BridgeHttpResponse Rejection(string code) => new(code == "BRIDGE_TOKEN_INVALID" ? 401 : 400,
        JsonSerializer.Serialize(BridgeWireErrors.Create(code), BridgeProtocol.JsonOptions));

    private static BridgeResult<T> Failure<T>(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return BridgeResults.Failure<T>(code, error.Retryable, error.Scope, error.UserAction);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true; Invalidate(); _challenges.Clear(); CryptographicOperations.ZeroMemory(_secret);
        }
        finally { _gate.Release(); }
    }

    private sealed class Session(string id, string instance, string token, byte[] key, DateTimeOffset created)
    {
        public string Id { get; } = id;
        public string Instance { get; } = instance;
        public string Token { get; } = token;
        public byte[] Key { get; } = key;
        public DateTimeOffset Created { get; } = created;
        public DateTimeOffset LastActivity { get; set; } = created;
        public DateTimeOffset LastHeartbeat { get; set; } = created;
        public long Heartbeats { get; set; }
        public int LastEventSeq { get; set; }
        public bool Ready { get; set; }
        public string? HostState { get; set; }
        public string? ModalState { get; set; }
        public long LastSequence { get; set; }
        public string? LastRequestMac { get; set; }
        public BridgeHttpResponse? LastResponse { get; set; }
    }
}
