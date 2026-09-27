using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Desktop-owned Kestrel listener. No request logging, proxy, redirects or public interface.</summary>
public sealed partial class BridgeLoopbackHost(
    BridgeAdapterSessions sessions, BridgeExecutionBroker broker, BridgeAdapterValidator adapter,
    BridgeWireValidator wire, TimeProvider? timeProvider = null, IBridgePeerGuard? peerGuard = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private WebApplication? _app;
    private CancellationTokenSource? _monitorStop;
    private Task? _monitor;
    private volatile bool _monitorFaulted;
    public int ExecutionPort { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) throw new InvalidOperationException("BRIDGE_HOST_ALREADY_STARTED");
        await broker.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ExecutionPort = 0;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = BridgeProtocol.PlanByteLimit;
            options.Limits.MaxRequestHeadersTotalSize = BridgeAdapterProtocol.HeaderByteLimit;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            options.Limits.MaxConcurrentConnections = 8;
            options.Limits.MaxConcurrentUpgradedConnections = 8;
            options.Listen(IPAddress.Loopback, BridgeProtocol.RendezvousPort);
            // Bind once through the OS dynamic allocator. Random user-space guesses
            // can hit Windows excluded/ephemeral reservations. Never scan or retry ports.
            options.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(Handle);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = app.Urls.Select(address => new Uri(address)).ToArray();
            if (addresses.Length != 2 || addresses.Any(address => address.Host != "127.0.0.1"))
                throw new InvalidOperationException("BRIDGE_LISTENER_CONFIGURATION_INVALID");
            ExecutionPort = addresses.Single(address => address.Port != BridgeProtocol.RendezvousPort).Port;
            if (ExecutionPort is < BridgeProtocol.MinExecutionPort or > BridgeProtocol.MaxExecutionPort)
                throw new InvalidOperationException("BRIDGE_EXECUTION_PORT_RANGE_INVALID");
            _app = app;
            _monitorStop = new CancellationTokenSource();
            _monitor = MonitorConnection(_monitorStop.Token);
        }
        catch (Exception error)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            // Preserve a coarse bind failure reason, never a raw hosting exception or request data.
            var socket = error.GetBaseException() as System.Net.Sockets.SocketException;
            var reason = socket is null ? "BRIDGE_LISTENER_START_FAILED"
                : $"BRIDGE_BIND_{socket.SocketErrorCode}_EXECUTION_{ExecutionPort}";
            throw new IOException("BRIDGE_PORT_UNAVAILABLE", new IOException(reason));
        }
    }

    private async Task Handle(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        // This single admission point precedes HTTP body reads and WebSocket upgrades alike.
        // Rejected, unauthenticated callers must not disconnect an existing correct session.
        if (!AdmitPeer(context))
        {
            try
            {
                await Write(context, BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH"),
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { context.Abort(); }
            catch (IOException) { context.Abort(); }
            return;
        }
        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleSocket(context).ConfigureAwait(false);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var dispatched = false;
        try
        {
            var request = context.Request;
            if (_monitorFaulted) throw new InvalidOperationException("BRIDGE_DISCONNECTED");
            var localPort = context.Connection.LocalPort;
            if (!IPAddress.Loopback.Equals(context.Connection.RemoteIpAddress)
                || request.Host.Value != $"127.0.0.1:{localPort}"
                || request.Headers.ContainsKey("Origin") || request.Headers.ContainsKey("Forwarded")
                || request.Headers.Keys.Any(k => k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
                || request.Headers.ContainsKey("Content-Encoding")
                || request.Method is not ("POST" or "GET"))
            {
                await Write(context, BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH"), deadline.Token).ConfigureAwait(false);
                return;
            }
            var body = request.Method == "GET" ? "" : await Read(request,
                localPort == BridgeProtocol.RendezvousPort ? BridgeProtocol.MessageByteLimit : BridgeProtocol.StatusByteLimit,
                deadline.Token).ConfigureAwait(false);
            if (request.Method == "GET" && (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding")))
                throw new InvalidDataException();
            var reply = await Exchange(request, localPort, body, () => dispatched = true, deadline.Token).ConfigureAwait(false);
            await Write(context, reply, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (dispatched) await DisconnectAfterFault().ConfigureAwait(false);
            context.Abort();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException
            or IOException or UnauthorizedAccessException or InvalidOperationException or BadHttpRequestException)
        {
            if (dispatched) await DisconnectAfterFault().ConfigureAwait(false);
            if (!context.Response.HasStarted)
                await Write(context, BridgeAdapterSessions.Rejection(dispatched ? "BRIDGE_EXECUTION_UNKNOWN"
                    : exception is BridgeReadException read ? read.Code : "BRIDGE_PROTOCOL_MISMATCH"), context.RequestAborted).ConfigureAwait(false);
        }
    }

    private bool AdmitPeer(HttpContext context)
    {
        // Null is retained for isolated tooling/legacy tests. Formal selection integration must
        // supply a per-owner guard; this optional parameter alone is not product host selection.
        if (peerGuard is null) return true;
        var connection = context.Connection;
        return connection.LocalIpAddress is not null && connection.RemoteIpAddress is not null
            && peerGuard.Allows(new(connection.LocalIpAddress, connection.LocalPort),
                new(connection.RemoteIpAddress, connection.RemotePort));
    }

    // Both carriers enter this one authenticated dispatcher after their own framing and peer checks.
    // The WebSocket carrier does not invoke an HTTP endpoint or rewrite an incoming Host/Origin.
    private async Task<BridgeHttpResponse> Exchange(HttpRequest request, int localPort, string body,
        Action dispatched, CancellationToken cancellationToken)
    {
            if (_monitorFaulted) throw new InvalidOperationException("BRIDGE_DISCONNECTED");
            var path = request.Path.Value!;
            if (localPort == BridgeProtocol.RendezvousPort)
            {
                if (request.Method != "POST" || request.QueryString.HasValue
                    || path is not ("/adapter/v1/challenges" or "/handshake"))
                {
                    return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
                }
                if (path == "/adapter/v1/challenges")
                {
                    var result = await sessions.ChallengeAsync(body, cancellationToken).ConfigureAwait(false);
                    return result.IsSuccess ? AdapterReply("challenge_response", result.Value!)
                        : BridgeAdapterSessions.Rejection(result.Error!.Code);
                }
                    var proof = Single(request, "X-Adapter-Handshake-Proof");
                    var previous = await sessions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                    var paired = await sessions.PairAsync(body, proof, ExecutionPort, cancellationToken).ConfigureAwait(false);
                    if (paired.StatusCode == 200 && previous is not null) await broker.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    return paired;
            }
            if (localPort != ExecutionPort) throw new InvalidDataException();
            var authorization = Single(request, "Authorization");
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)) throw new InvalidDataException();
            if (!long.TryParse(Single(request, "X-Adapter-Sequence"), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                throw new InvalidDataException();
            var auth = new AdapterRequestAuthentication(Single(request, "X-Bridge-Request-Id"),
                Single(request, "X-Bridge-Session-Id"), Single(request, "X-Adapter-Instance-Id"), sequence,
                authorization[7..], Single(request, "X-Adapter-Mac"));
            if (request.Method == "GET") auth = auth with { Query = new(Single(request, "X-Bridge-Protocol-Version"),
                Single(request, "X-Bridge-Correlation-Id"), Single(request, "X-Bridge-Sent-At"), Single(request, "X-Bridge-Idempotency-Key")) };
            if (Single(request, "X-Adapter-Version") != BridgeAdapterProtocol.Version) throw new InvalidDataException();
            var health = await sessions.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var requiresReady = path.StartsWith("/adapter/v1/claims", StringComparison.Ordinal);
            // A Photoshop-owned modal reports busy/running while the adapter polls the
            // exact execution claim for a cancellation request. Allow that read-only
            // claim query through the host-ready gate; ACK and all other claim routes
            // remain blocked until the host is ready.
            var allowsModalCancellationQuery = path == "/adapter/v1/claims"
                && health.HostState == "busy" && health.ModalState == "running";
            return await sessions.ExecuteAsync(auth, request.Method, path + request.QueryString.Value, body,
                token =>
                {
                    if (requiresReady && !health.Ready && !allowsModalCancellationQuery)
                        return Task.FromResult(BridgeAdapterSessions.Rejection(health.Connected ? "BRIDGE_HOST_BLOCKED" : "BRIDGE_DISCONNECTED"));
                    if ((path == "/adapter/v1/resources/read" || path.StartsWith("/adapter/v1/outputs/", StringComparison.Ordinal))
                        && !health.Connected)
                        return Task.FromResult(BridgeAdapterSessions.Rejection("BRIDGE_DISCONNECTED"));
                    dispatched();
                    return Dispatch(request, auth, path, body, token);
                }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectAfterFault()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await broker.DisconnectAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { }
    }

    private async Task MonitorConnection(CancellationToken cancellationToken)
    {
        string? observedSession = null;
        var disconnected = false;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var current = await sessions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (current is not null && current.SessionId != observedSession)
                {
                    observedSession = current.SessionId;
                    disconnected = false;
                }
                var health = await sessions.GetHealthAsync(cancellationToken).ConfigureAwait(false);
                if (observedSession is not null && !health.Connected && !disconnected)
                {
                    LastTransportFault ??= new("heartbeat_timeout", "none", false);
                    await broker.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    disconnected = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _monitorFaulted = true;
            await DisconnectAfterFault().ConfigureAwait(false);
        }
    }

    private async Task<BridgeHttpResponse> Dispatch(HttpRequest request, AdapterRequestAuthentication auth,
        string path, string json, CancellationToken cancellationToken)
    {
        if (request.Method == "GET") return await Query(request, auth, path, cancellationToken).ConfigureAwait(false);
        if (request.QueryString.HasValue) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
        if (path == "/adapter/v1/outputs/write")
        {
            if (!adapter.Validate(json, "output_write").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var chunk = JsonSerializer.Deserialize<AdapterEnvelope<AdapterOutputWrite>>(json, BridgeProtocol.JsonOptions)!.Body;
            var result = await broker.WriteOutputAsync(auth.SessionId, chunk, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("output_receipt", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        if (path == "/adapter/v1/outputs/seal")
        {
            if (!adapter.Validate(json, "output_seal").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var seal = JsonSerializer.Deserialize<AdapterEnvelope<AdapterOutputSeal>>(json, BridgeProtocol.JsonOptions)!.Body;
            var result = await broker.SealOutputAsync(auth.SessionId, seal, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("output_receipt", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        if (path == "/adapter/v1/resources/read")
        {
            if (!adapter.Validate(json, "resource_read").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var read = JsonSerializer.Deserialize<AdapterEnvelope<AdapterResourceRead>>(json, BridgeProtocol.JsonOptions)!.Body;
            var result = await broker.ReadResourceAsync(auth.SessionId, read, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("resource_chunk", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        if (path == "/adapter/v1/claims")
        {
            if (!adapter.Validate(json, "claim_request").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var result = await broker.ClaimAsync(auth.SessionId, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("claim_response", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        if (path.StartsWith("/adapter/v1/claims/", StringComparison.Ordinal) && path.EndsWith("/ack", StringComparison.Ordinal))
        {
            if (!adapter.Validate(json, "claim_ack").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var body = JsonSerializer.Deserialize<AdapterEnvelope<AdapterClaimAcknowledgement>>(json, BridgeProtocol.JsonOptions)!.Body;
            if (path != $"/adapter/v1/claims/{body.ClaimId}/ack") return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var result = await broker.AcknowledgeAsync(auth.SessionId, body, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("claim_response", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        if (path == "/adapter/v1/events")
        {
            if (!adapter.Validate(json, "event_report").IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
            var report = JsonSerializer.Deserialize<AdapterEnvelope<AdapterEventReport>>(json, BridgeProtocol.JsonOptions)!.Body;
            var result = await broker.ReportAsync(auth.SessionId, report, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? AdapterReply("event_receipt", result.Value!) : BridgeAdapterSessions.Rejection(result.Error!.Code);
        }
        var endpoint = path == "/heartbeat" ? BridgeEndpoint.Heartbeat : BridgeEndpoint.Release;
        if (path is not ("/heartbeat" or "/release") || !wire.Validate(json, endpoint).IsValid)
            return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
        using var document = JsonDocument.Parse(json);
        var envelope = document.RootElement.GetProperty("body");
        if (!Matches(auth, envelope)) return BridgeAdapterSessions.Rejection("BRIDGE_TOKEN_INVALID");
        if (path == "/release") await broker.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return WireReply(new(true, auth.RequestId, envelope.GetProperty("correlation_id").GetString()!, "ok", _time.GetUtcNow()));
    }

    private async Task<BridgeHttpResponse> Query(HttpRequest request, AdapterRequestAuthentication auth,
        string path, CancellationToken cancellationToken)
    {
        const string prefix = "/executions/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path[prefix.Length..].Contains('/')
            || request.Query.Keys.Any(k => k != "after_seq")) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
        var after = 0;
        if (request.Query.TryGetValue("after_seq", out var values)
            && (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
            return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
        var metadata = auth.Query!;
        var body = new BridgeRequestBodyV1<BridgeEmptyPayloadV1>(auth.RequestId,
            metadata.CorrelationId, auth.SessionId, auth.Token,
            DateTimeOffset.Parse(metadata.SentAt, CultureInfo.InvariantCulture),
            metadata.IdempotencyKey, "get_execution", new(), path[prefix.Length..]);
        var json = JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeRequestBodyV1<BridgeEmptyPayloadV1>>(
            metadata.ProtocolVersion, "request", body), BridgeProtocol.JsonOptions);
        if (!wire.Validate(json, BridgeEndpoint.Execution).IsValid) return BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH");
        var record = await broker.QueryAsync(body.ExecutionId!, cancellationToken).ConfigureAwait(false);
        return record is null ? BridgeAdapterSessions.Rejection("BRIDGE_EXECUTION_NOT_FOUND")
            : WireReply(new(true, auth.RequestId, body.CorrelationId, "ok", _time.GetUtcNow(), record.ExecutionId,
                BridgeExecutionBroker.WireState(record.State), record.EventSeq, record.Events.Where(e => e.EventSeq > after).ToArray()));
    }

    private static bool Matches(AdapterRequestAuthentication auth, JsonElement body) =>
        body.GetProperty("request_id").GetString() == auth.RequestId && body.GetProperty("session_id").GetString() == auth.SessionId
        && BridgeAdapterCryptography.FixedEquals(auth.Token, body.GetProperty("token").GetString()!);

    private static string Single(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var values) || values.Count != 1
            || string.IsNullOrEmpty(values[0]) || values[0]!.Any(char.IsControl)) throw new InvalidDataException();
        return values[0]!;
    }

    private static async Task<string> Read(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentType is not ("application/json" or "application/json; charset=utf-8")) throw new InvalidDataException();
        var result = await BridgeWireMessageReader.ReadRawAsync(request.Body, limit,
            request.ContentLength, cancellationToken).ConfigureAwait(false);
        return result.Json ?? throw new BridgeReadException(result.Error!.Code);
    }

    private static BridgeHttpResponse AdapterReply<T>(string kind, T body) => new(200,
        JsonSerializer.Serialize(new AdapterEnvelope<T>(BridgeAdapterProtocol.Version, kind, body), BridgeProtocol.JsonOptions));
    private static BridgeHttpResponse WireReply(BridgeResponseBodyV1 body) => new(200,
        JsonSerializer.Serialize(new BridgeWireEnvelope<BridgeResponseBodyV1>(BridgeProtocol.Version, "response", body), BridgeProtocol.JsonOptions));

    private static async Task Write(HttpContext context, BridgeHttpResponse reply, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = reply.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        if (reply.Mac is not null) context.Response.Headers["X-Adapter-Mac"] = reply.Mac;
        var bytes = Encoding.UTF8.GetBytes(reply.Body);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        try
        {
            _monitorStop!.Cancel();
            try { if (_monitor is not null) await _monitor.ConfigureAwait(false); }
            finally { _monitorStop.Dispose(); }
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally
            {
                try { await DisconnectAfterFault().ConfigureAwait(false); }
                finally { await _app.DisposeAsync().ConfigureAwait(false); _app = null; }
            }
        }
    }

    private sealed class BridgeReadException(string code) : IOException
    {
        public string Code { get; } = code;
    }
}
