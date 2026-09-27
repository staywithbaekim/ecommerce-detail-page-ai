using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeLoopbackHostTests
{
    // Runs the existing full authenticated listener/claim/resource/query scenario through the new carrier.
    private sealed class SocketHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var socket = NewSocket();
            if (request.Headers.TryGetValues("Origin", out var origin)) socket.Options.SetRequestHeader("Origin", origin.Single());
            var address = new UriBuilder(request.RequestUri!) { Scheme = "ws", Host = "127.0.0.1" }.Uri;
            socket.Options.SetRequestHeader("Host", $"localhost:{address.Port}");
            try { await socket.ConnectAsync(address, cancellationToken); }
            catch (WebSocketException) when ((int)socket.HttpStatusCode >= 400)
            {
                return new HttpResponseMessage(socket.HttpStatusCode);
            }
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.Where(h => h.Key != "Origin").ToDictionary(h => h.Key, h => h.Value.Single());
            headers["Content-Type"] = "application/json";
            var header = JsonSerializer.SerializeToUtf8Bytes(new
            { version = "ps-adapter-rpc/v1", method = request.Method.Method, headers, byte_length = body.Length });
            await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
            for (var offset = 0; offset < body.Length; offset += 128 * 1024)
                await socket.SendAsync(body.AsMemory(offset, Math.Min(128 * 1024, body.Length - offset)), WebSocketMessageType.Binary, true, cancellationToken);
            using var metadata = JsonDocument.Parse(await ReadSocket(socket, WebSocketMessageType.Text, 8192, cancellationToken));
            var root = metadata.RootElement;
            Assert.Equal("ps-adapter-rpc/v1", root.GetProperty("version").GetString());
            var length = root.GetProperty("byte_length").GetInt32();
            Assert.InRange(length, 0, BridgeProtocol.PlanByteLimit + 65536);
            using var bytes = new MemoryStream();
            while (bytes.Length < length)
            {
                var chunk = await ReadSocket(socket, WebSocketMessageType.Binary, 128 * 1024, cancellationToken);
                Assert.NotEmpty(chunk);
                bytes.Write(chunk);
                Assert.True(bytes.Length <= length);
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
            var response = new HttpResponseMessage((HttpStatusCode)root.GetProperty("status").GetInt32())
                { Content = new ByteArrayContent(bytes.ToArray()) };
            var mac = root.GetProperty("mac").GetString();
            if (!string.IsNullOrEmpty(mac)) response.Headers.Add("X-Adapter-Mac", mac);
            return response;
        }
    }

    private static ClientWebSocket NewSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.AddSubProtocol("ps-adapter-rpc.v1");
        socket.Options.SetRequestHeader("Origin", "file://");
        socket.Options.SetRequestHeader("Host", "localhost:17842");
        return socket;
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("extra_frame")]
    [InlineData("abnormal")]
    public async Task ResponseWaitsForClientCloseReceiptAndRejectsExtraOrAbnormalFrames(string ending)
    {
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(CancellationToken.None);
        using var socket = NewSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new("ws://127.0.0.1:17842/handshake"), stop.Token);
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        { version = "ps-adapter-rpc/v1", method = "POST", headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }, byte_length = 2 });
        await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Text, true, stop.Token);
        await socket.SendAsync("{}"u8.ToArray().AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
        using var metadata = JsonDocument.Parse(await ReadSocket(socket, WebSocketMessageType.Text, 8192, stop.Token));
        var length = metadata.RootElement.GetProperty("byte_length").GetInt32();
        Assert.Equal(length, (await ReadSocket(socket, WebSocketMessageType.Binary, 128 * 1024, stop.Token)).Length);
        var dispatchFault = owner.LastTransportFault;
        Assert.Equal("dispatch", dispatchFault?.Stage);
        Assert.Equal("InvalidDataException", dispatchFault?.ExceptionType);
        var received = socket.ReceiveAsync(new byte[1].AsMemory(), stop.Token).AsTask();
        await Task.Delay(100, stop.Token);
        Assert.False(received.IsCompleted); // A complete body alone cannot trigger server-initiated close.
        if (ending == "extra_frame")
            await socket.SendAsync("x"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, stop.Token);
        else
            await socket.CloseOutputAsync(ending == "normal" ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation, "", stop.Token);
        if (ending == "normal")
        {
            Assert.Equal(WebSocketMessageType.Close, (await received).MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
            Assert.Same(dispatchFault, owner.LastTransportFault);
        }
        else
        {
            await Assert.ThrowsAsync<WebSocketException>(async () => await received);
            Assert.Equal("response_close", owner.LastTransportFault?.Stage);
            Assert.Equal("handshake", owner.LastTransportFault?.Operation);
        }
    }

    private static async Task<byte[]> ReadSocket(ClientWebSocket socket, WebSocketMessageType type, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var bytes = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Assert.Equal(type, result.MessageType);
            Assert.True(bytes.Length + result.Count <= limit);
            bytes.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return bytes.ToArray();
    }

    [Theory]
    [InlineData("Origin", "null")]
    [InlineData("Origin", "http://localhost:17842")]
    [InlineData("Origin", "file://,file://")]
    [InlineData("Origin", "http://untrusted.invalid")]
    [InlineData("Host", "127.0.0.1:17842")]
    [InlineData("Host", "localhost:17843")]
    [InlineData("Host", "localhost.invalid:17842")]
    [InlineData("Host", "untrusted.invalid:17842")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Forwarded-Host", "localhost:17842")]
    [InlineData("Cookie", "synthetic=1")]
    [InlineData("Authorization", "Bearer synthetic")]
    [InlineData("Sec-WebSocket-Extensions", "permessage-deflate")]
    public async Task SocketUpgradeRejectsUnexpectedPeerMetadata(string name, string value)
    {
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(CancellationToken.None);
        using var socket = NewSocket();
        socket.Options.SetRequestHeader(name, value);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new("ws://127.0.0.1:17842/handshake"), stop.Token));
        Assert.Equal(HttpStatusCode.BadRequest, socket.HttpStatusCode);
        Assert.False((await owner.GetStatusAsync(stop.Token)).Paired);
    }

    [Theory]
    [InlineData("missing_origin")]
    [InlineData("missing_protocol")]
    [InlineData("wrong_protocol")]
    public async Task SocketUpgradeRequiresExactOriginAndSubprotocol(string mutation)
    {
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(CancellationToken.None);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Host", "localhost:17842");
        if (mutation != "missing_origin") socket.Options.SetRequestHeader("Origin", "file://");
        if (mutation != "missing_protocol") socket.Options.AddSubProtocol(mutation == "wrong_protocol" ? "unknown.v1" : "ps-adapter-rpc.v1");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new("ws://127.0.0.1:17842/handshake"), stop.Token));
        Assert.Equal(HttpStatusCode.BadRequest, socket.HttpStatusCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversized_header")]
    [InlineData("oversized_body")]
    [InlineData("unsafe_header")]
    [InlineData("duplicate_header")]
    [InlineData("wrong_type")]
    [InlineData("invalid_utf8")]
    [InlineData("extra_bytes")]
    [InlineData("oversized_chunk")]
    [InlineData("truncated")]
    public async Task SocketFramingRejectsMalformedInputWithoutPairing(string mutation)
    {
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(CancellationToken.None);
        using var socket = NewSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new("ws://127.0.0.1:17842/handshake"), stop.Token);
        var header = "{\"version\":\"ps-adapter-rpc/v1\",\"method\":\"POST\",\"headers\":{\"Content-Type\":\"application/json\"},\"byte_length\":2}";
        header = mutation switch
        {
            "duplicate" => header.Replace("\"byte_length\":2", "\"byte_length\":0,\"byte_length\":2"),
            "unknown" => header.Replace("\"byte_length\":2", "\"unknown\":true,\"byte_length\":2"),
            "oversized_header" => header + new string(' ', 8192),
            "oversized_body" => header.Replace("\"byte_length\":2", "\"byte_length\":1048577"),
            "oversized_chunk" => header.Replace("\"byte_length\":2", "\"byte_length\":131073"),
            "unsafe_header" => header.Replace("\"Content-Type\":", "\"Host\":\"localhost:17842\",\"Content-Type\":"),
            "duplicate_header" => header.Replace("\"Content-Type\":", "\"content-type\":\"application/json\",\"Content-Type\":"),
            _ => header
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(header).AsMemory(), WebSocketMessageType.Text, true, stop.Token);
        if (mutation is "wrong_type" or "invalid_utf8" or "extra_bytes" or "oversized_chunk")
        {
            byte[] body = mutation switch { "invalid_utf8" => [0xc0, 0xaf], "extra_bytes" => [123, 125, 32],
                "oversized_chunk" => new byte[131073], _ => [123, 125] };
            try { await socket.SendAsync(body.AsMemory(), mutation == "wrong_type" ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, stop.Token); }
            catch (WebSocketException) { }
        }
        var receive = async () => await socket.ReceiveAsync(new byte[8192].AsMemory(), stop.Token);
        await Assert.ThrowsAsync<WebSocketException>(async () => { await receive(); });
        Assert.False((await owner.GetStatusAsync(CancellationToken.None)).Paired);
    }

    [Fact]
    public async Task SocketUpgradeRejectsContentEncodingOnActualWire()
    {
        // ClientWebSocket drops content headers on a bodyless upgrade, so assert the real wire explicitly.
        await using var owner = CreateOwner(new byte[32]);
        await owner.StartAsync(CancellationToken.None);
        using var client = new TcpClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, 17842, stop.Token);
        await using var stream = client.GetStream();
        const string request = "GET /handshake HTTP/1.1\r\nHost: localhost:17842\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
            + "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
            + "Sec-WebSocket-Protocol: ps-adapter-rpc.v1\r\nOrigin: file://\r\nContent-Encoding: gzip\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), stop.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("HTTP/1.1 400 Bad Request", await reader.ReadLineAsync(stop.Token));
    }
}
