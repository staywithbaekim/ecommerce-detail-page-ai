using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Microsoft.AspNetCore.Http;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeLoopbackHost
{
    public sealed record TransportFault(string Stage, string ExceptionType, bool Dispatched)
    {
        public string? Operation { get; init; }
        public long ElapsedMilliseconds { get; init; }
        public bool RequestAborted { get; init; }
        public string? SocketState { get; init; }
        public int? CloseStatus { get; init; }
    }
    public TransportFault? LastTransportFault { get; private set; }
    private const string SocketProtocol = "ps-adapter-rpc.v1";
    private const string SocketVersion = "ps-adapter-rpc/v1";
    private const int SocketChunkLimit = 128 * 1024;
    private static readonly UTF8Encoding SocketUtf8 = new(false, true);
    private static readonly HashSet<string> SocketHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "X-Adapter-Handshake-Proof", "Authorization", "X-Adapter-Version", "X-Bridge-Request-Id",
        "X-Bridge-Session-Id", "X-Adapter-Instance-Id", "X-Adapter-Sequence", "X-Adapter-Mac",
        "X-Bridge-Protocol-Version", "X-Bridge-Correlation-Id", "X-Bridge-Sent-At", "X-Bridge-Idempotency-Key"
    };

    private async Task HandleSocket(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var dispatched = false;
        var stage = "upgrade";
        var operation = "unvalidated";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        WebSocket? socket = null;
        TransportFault Fault(Exception error) => new(stage, error.GetType().Name, dispatched)
        {
            Operation = operation, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
            RequestAborted = context.RequestAborted.IsCancellationRequested,
            SocketState = socket?.State.ToString(), CloseStatus = (int?)socket?.CloseStatus
        };
        try
        {
            var upgrade = context.Request;
            var port = context.Connection.LocalPort;
            if (_monitorFaulted || !IPAddress.Loopback.Equals(context.Connection.RemoteIpAddress)
                || upgrade.Host.Value != $"localhost:{port}" || Single(upgrade, "Origin") != "file://"
                || upgrade.Headers.ContainsKey("Forwarded")
                || upgrade.Headers.Keys.Any(k => k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
                || upgrade.Headers.ContainsKey("Cookie") || upgrade.Headers.ContainsKey("Authorization")
                || upgrade.Headers.ContainsKey("Content-Encoding") || upgrade.Headers.ContainsKey("Sec-WebSocket-Extensions")
                || upgrade.ContentLength is > 0 || upgrade.Headers.ContainsKey("Transfer-Encoding")
                || Single(upgrade, "Sec-WebSocket-Protocol") != SocketProtocol)
                throw new InvalidDataException();

            socket = await context.WebSockets.AcceptWebSocketAsync(SocketProtocol).ConfigureAwait(false);
            var headerBytes = await ReceiveSocketMessage(socket, WebSocketMessageType.Text,
                BridgeAdapterProtocol.HeaderByteLimit, deadline.Token).ConfigureAwait(false);
            HttpRequest request;
            int byteLength;
            try
            {
                using var header = JsonDocument.Parse(SocketUtf8.GetString(headerBytes));
                var root = header.RootElement;
                ExactProperties(root, "version", "method", "headers", "byte_length");
                if (root.GetProperty("version").GetString() != SocketVersion) throw new InvalidDataException();
                // A request-shaped value carries only operation metadata. Peer headers are never synthesized.
                request = new DefaultHttpContext().Request;
                request.Method = root.GetProperty("method").GetString()!;
                if (request.Method is not ("POST" or "GET")) throw new InvalidDataException();
                request.Path = upgrade.Path;
                request.QueryString = upgrade.QueryString;
                var headers = root.GetProperty("headers");
                if (headers.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                foreach (var property in headers.EnumerateObject())
                {
                    var value = property.Value.GetString();
                    if (!SocketHeaderNames.Contains(property.Name) || request.Headers.ContainsKey(property.Name)
                        || string.IsNullOrEmpty(value) || value.Any(char.IsControl)) throw new InvalidDataException();
                    request.Headers[property.Name] = value;
                }
                if (request.ContentType is not ("application/json" or "application/json; charset=utf-8"))
                    throw new InvalidDataException();
                byteLength = root.GetProperty("byte_length").GetInt32();
                var limit = port == BridgeProtocol.RendezvousPort ? BridgeProtocol.MessageByteLimit : BridgeProtocol.StatusByteLimit;
                if (byteLength < 0 || byteLength > limit || (request.Method == "GET" && byteLength != 0))
                    throw new InvalidDataException();
            }
            finally { CryptographicOperations.ZeroMemory(headerBytes); }

            var bytes = new byte[byteLength];
            string body;
            try
            {
                var offset = 0;
                while (offset < byteLength)
                {
                    var chunk = await ReceiveSocketMessage(socket, WebSocketMessageType.Binary,
                        Math.Min(SocketChunkLimit, byteLength - offset), deadline.Token).ConfigureAwait(false);
                    try
                    {
                        if (chunk.Length == 0) throw new InvalidDataException();
                        chunk.CopyTo(bytes, offset);
                        offset += chunk.Length;
                    }
                    finally { CryptographicOperations.ZeroMemory(chunk); }
                }
                body = SocketUtf8.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            BridgeHttpResponse reply;
            stage = "dispatch";
            operation = request.Path.Value switch
            {
                "/heartbeat" => "heartbeat", "/release" => "release", "/handshake" => "handshake",
                "/adapter/v1/challenges" => "challenge", "/adapter/v1/claims" => "claim",
                "/adapter/v1/events" => "event", "/adapter/v1/resources/read" => "resource_read",
                "/adapter/v1/outputs/write" => "output_write", "/adapter/v1/outputs/seal" => "output_seal",
                _ => "other"
            };
            try { reply = await Exchange(request, port, body, () => dispatched = true, deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidDataException or JsonException or FormatException or IOException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                LastTransportFault = Fault(e);
                if (dispatched) await DisconnectAfterFault().ConfigureAwait(false);
                reply = BridgeAdapterSessions.Rejection(dispatched ? "BRIDGE_EXECUTION_UNKNOWN" : "BRIDGE_PROTOCOL_MISMATCH");
            }
            stage = "response_send";
            await SendSocketReply(socket, reply, deadline.Token).ConfigureAwait(false);
            stage = "response_close";
            // The existing client closes only after receiving the complete framed response.
            // Read that receipt before acknowledging close; do not race two close initiators.
            var close = await socket.ReceiveAsync(new byte[1].AsMemory(), deadline.Token).ConfigureAwait(false);
            if (close.MessageType != WebSocketMessageType.Close || close.Count != 0
                || socket.CloseStatus != WebSocketCloseStatus.NormalClosure) throw new InvalidDataException();
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or FormatException or InvalidOperationException
            or IOException or UnauthorizedAccessException or OperationCanceledException or WebSocketException
            or BadHttpRequestException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            LastTransportFault = Fault(e);
            if (dispatched) await DisconnectAfterFault().ConfigureAwait(false);
            if (socket is not null) socket.Abort();
            else if (!context.Response.HasStarted)
                await Write(context, BridgeAdapterSessions.Rejection("BRIDGE_PROTOCOL_MISMATCH"), context.RequestAborted).ConfigureAwait(false);
        }
        finally { socket?.Dispose(); }
    }

    private static void ExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!expected.Contains(p.Name, StringComparer.Ordinal) || !found.Add(p.Name)) throw new InvalidDataException();
        if (found.Count != expected.Length) throw new InvalidDataException();
    }

    private static async Task<byte[]> ReceiveSocketMessage(WebSocket socket, WebSocketMessageType type,
        int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(16 * 1024, limit + 1)];
        using var result = new MemoryStream();
        try
        {
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - result.Length + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read.MessageType != type || result.Length + read.Count > limit) throw new InvalidDataException();
                result.Write(buffer, 0, read.Count);
                if (read.EndOfMessage) return result.ToArray();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(result.GetBuffer().AsSpan(0, (int)result.Length));
        }
    }

    private static async Task SendSocketReply(WebSocket socket, BridgeHttpResponse reply, CancellationToken cancellationToken)
    {
        var bytes = SocketUtf8.GetBytes(reply.Body);
        try
        {
            if (bytes.Length > BridgeProtocol.PlanByteLimit + 65536) throw new InvalidDataException();
            var header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = SocketVersion, status = reply.StatusCode, byte_length = bytes.Length, mac = reply.Mac ?? ""
            });
            await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            for (var offset = 0; offset < bytes.Length; offset += SocketChunkLimit)
                await socket.SendAsync(bytes.AsMemory(offset, Math.Min(SocketChunkLimit, bytes.Length - offset)),
                    WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
