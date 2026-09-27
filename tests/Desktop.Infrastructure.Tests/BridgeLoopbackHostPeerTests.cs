using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

// Same collection/class as all fixed-port bridge tests; never competes with another owner fixture.
public sealed partial class BridgeLoopbackHostTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PeerAdmissionRejectsBothCarriersBeforeBodyRead(bool websocket, bool executionPort)
    {
        var admission = new RecordingPeerGuard { Allowed = false };
        var journal = new BridgeExecutionBrokerTests.MemoryJournal();
        using var fixture = new BridgeAdapterSessionsTests.Fixture();
        using var broker = BridgeExecutionBrokerTests.Create(journal);
        var adapter = new BridgeAdapterValidator(new JsonSchemaContractValidator(), fixture.Wire);
        await using var host = new BridgeLoopbackHost(fixture.Sessions, broker, adapter, fixture.Wire, peerGuard: admission);
        await host.StartAsync(CancellationToken.None);
        var port = executionPort ? host.ExecutionPort : BridgeProtocol.RendezvousPort;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (websocket)
        {
            using var socket = NewSocket();
            socket.Options.SetRequestHeader("Host", $"localhost:{port}");
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new($"ws://127.0.0.1:{port}/handshake"), stop.Token));
            Assert.Equal(HttpStatusCode.BadRequest, socket.HttpStatusCode);
        }
        else
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            var stream = client.GetStream();
            // Advertise a body and never send it. A rejection proves admission precedes body reads.
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /handshake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n"), stop.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            Assert.Equal("HTTP/1.1 400 Bad Request", await reader.ReadLineAsync(stop.Token));
        }
        var observed = Assert.Single(admission.Endpoints);
        Assert.Equal(new(IPAddress.Loopback, port), observed.Local);
        Assert.Equal(IPAddress.Loopback, observed.Remote.Address);
        Assert.InRange(observed.Remote.Port, 1, 65535);
        Assert.Null(await fixture.Sessions.GetCurrentAsync(stop.Token));
        Assert.Null(journal.Value);
        Assert.Null(host.LastTransportFault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerAdmissionRealWindowsIdentityCoversHandshakeAndDynamicHeartbeat(bool websocket)
    {
        using var process = Process.GetCurrentProcess();
        var path = process.MainModule!.FileName;
        var resolver = new RecordingProcessResolver(new WindowsBridgePeerProcessResolver());
        var admission = new BridgeSelectedHostPeerGuard(path, resolver);
        var secret = RandomNumberGenerator.GetBytes(32);
        await using var owner = new BridgeSessionOwner(secret,
            new("task_peer_identity", "0.1.0", BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
                [BridgeAdapterProtocol.Capability]), new BridgeExecutionBrokerTests.MemoryJournal(), peerGuard: admission);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.StartAsync(stop.Token);
        await AuthenticateDevice(secret, () => { }, stop.Token, useSocket: websocket);
        var ready = await owner.GetStatusAsync(stop.Token);
        Assert.True(ready.Ready);
        Assert.Equal(3, ready.Heartbeats);
        Assert.Contains(BridgeProtocol.RendezvousPort, resolver.LocalPorts);
        Assert.Contains(resolver.LocalPorts, port => port is >= BridgeProtocol.MinExecutionPort and <= BridgeProtocol.MaxExecutionPort);
        Assert.All(resolver.Identities, identity =>
        {
            Assert.NotNull(identity);
            Assert.Equal(process.Id, identity.ProcessId);
            Assert.Equal(process.StartTime.ToUniversalTime().Ticks, identity.StartTimeUtcTicks);
            Assert.Equal(path, identity.ExecutablePath, ignoreCase: true);
        });
        Assert.Null(owner.LastTransportFault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerAdmissionWrongSelectedExecutableRejectsActualWindowsClient(bool websocket)
    {
        var admission = new BridgeSelectedHostPeerGuard(@"C:\NotTheSelectedInstallation\Photoshop.exe",
            new WindowsBridgePeerProcessResolver());
        await using var owner = new BridgeSessionOwner(new byte[32], OwnerPolicy(),
            new BridgeExecutionBrokerTests.MemoryJournal(), peerGuard: admission);
        await owner.StartAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = new HttpClient(websocket ? new SocketHttpHandler() : new HttpClientHandler { UseProxy = false });
        using var reply = await client.PostAsync("http://127.0.0.1:17842/adapter/v1/challenges", Content("{}"), stop.Token);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.False((await owner.GetStatusAsync(stop.Token)).Paired);
        Assert.Null(owner.LastTransportFault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerAdmissionRejectedCallerDoesNotDisconnectAuthenticatedHost(bool websocket)
    {
        var admission = new RecordingPeerGuard { Allowed = true };
        var secret = RandomNumberGenerator.GetBytes(32);
        await using var owner = new BridgeSessionOwner(secret,
            new("task_peer_rejection", "0.1.0", BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
                [BridgeAdapterProtocol.Capability]), new BridgeExecutionBrokerTests.MemoryJournal(), peerGuard: admission);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.StartAsync(stop.Token);
        await AuthenticateDevice(secret, () => { }, stop.Token);
        var before = await owner.GetStatusAsync(stop.Token);
        Assert.True(before.Ready);
        admission.Allowed = false;
        using var client = new HttpClient(websocket ? new SocketHttpHandler() : new HttpClientHandler { UseProxy = false });
        foreach (var port in admission.Endpoints.Select(pair => pair.Local.Port).Distinct().ToArray())
        {
            using var reply = await client.PostAsync($"http://127.0.0.1:{port}/adapter/v1/claims", Content("{}"), stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        }
        Assert.Equal(before, await owner.GetStatusAsync(stop.Token));
        Assert.Empty(await owner.ListExecutionsAsync(stop.Token));
        Assert.Null(owner.LastTransportFault);
    }

    private sealed class RecordingPeerGuard : IBridgePeerGuard
    {
        private readonly List<(IPEndPoint Local, IPEndPoint Remote)> _endpoints = [];
        public bool Allowed { get; set; }
        public IReadOnlyList<(IPEndPoint Local, IPEndPoint Remote)> Endpoints { get { lock (_endpoints) return _endpoints.ToArray(); } }
        public bool Allows(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
        {
            lock (_endpoints) _endpoints.Add((localEndpoint, remoteEndpoint));
            return Allowed;
        }
    }

    private sealed class RecordingProcessResolver(IBridgePeerProcessResolver inner) : IBridgePeerProcessResolver
    {
        public List<int> LocalPorts { get; } = [];
        public List<BridgePeerProcessIdentity?> Identities { get; } = [];
        public BridgePeerProcessIdentity? Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
        {
            var result = inner.Resolve(localEndpoint, remoteEndpoint);
            LocalPorts.Add(localEndpoint.Port);
            Identities.Add(result);
            return result;
        }
    }
}
