using System.ComponentModel;
using System.Net;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeSelectedHostPeerGuardTests
{
    private const string PhotoshopPath = @"G:\Adobe Photoshop 2025\Photoshop.exe";
    private static readonly IPEndPoint Local = new(IPAddress.Loopback, 17842);
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 55123);

    [Fact]
    public void PeerGuardPinsProcessAndCreationTimeAcrossNewConnections()
    {
        var resolver = new Resolver { Identity = new(42, 100, PhotoshopPath.ToUpperInvariant()) };
        var guard = new BridgeSelectedHostPeerGuard(PhotoshopPath, resolver);
        Assert.True(guard.Allows(Local, Remote));
        Assert.True(guard.Allows(new(IPAddress.Loopback, 50123), new(IPAddress.Loopback, 55300)));
        resolver.Identity = new(43, 100, PhotoshopPath);
        Assert.False(guard.Allows(Local, Remote));
        resolver.Identity = new(42, 101, PhotoshopPath);
        Assert.False(guard.Allows(Local, Remote));
        resolver.Identity = new(42, 100, @"C:\Other\Photoshop.exe");
        Assert.False(guard.Allows(Local, Remote));
        resolver.Identity = null;
        Assert.False(guard.Allows(Local, Remote));
        resolver.Identity = new(42, 100, PhotoshopPath);
        Assert.True(guard.Allows(Local, Remote)); // A rejected caller cannot replace or clear the pin.
    }

    [Theory]
    [InlineData("127.0.0.2", 17842, "127.0.0.1", 55123)]
    [InlineData("127.0.0.1", 17842, "127.0.0.2", 55123)]
    [InlineData("::1", 17842, "::1", 55123)]
    [InlineData("127.0.0.1", 0, "127.0.0.1", 55123)]
    [InlineData("127.0.0.1", 17842, "127.0.0.1", 0)]
    public void PeerGuardRejectsInvalidEndpointBeforeResolving(string local, int localPort, string remote, int remotePort)
    {
        var resolver = new Resolver { Identity = new(42, 100, PhotoshopPath) };
        var guard = new BridgeSelectedHostPeerGuard(PhotoshopPath, resolver);
        Assert.False(guard.Allows(new(IPAddress.Parse(local), localPort), new(IPAddress.Parse(remote), remotePort)));
        Assert.Equal(0, resolver.Reads);
    }

    [Theory]
    [InlineData(@"\\server\share\Photoshop.exe")]
    [InlineData(@"C:\Photoshop.exe:stream")]
    [InlineData(@"C:\trailing.\Photoshop.exe")]
    [InlineData(@"C:\double\\Photoshop.exe")]
    [InlineData("Photoshop.exe")]
    [InlineData("C:/Photoshop.exe")]
    [InlineData("C:\\bad\u0001\\Photoshop.exe")]
    public void PeerGuardRejectsUnsafeExecutablePath(string path)
    {
        Assert.Throws<ArgumentException>(() => new BridgeSelectedHostPeerGuard(path, new Resolver()));
        var resolver = new Resolver { Identity = new(42, 100, path) };
        Assert.False(new BridgeSelectedHostPeerGuard(PhotoshopPath, resolver).Allows(Local, Remote));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(42, 0)]
    [InlineData(-1, 100)]
    public void PeerGuardDoesNotPinInvalidIdentity(int processId, long ticks)
    {
        var resolver = new Resolver { Identity = new(processId, ticks, PhotoshopPath) };
        var guard = new BridgeSelectedHostPeerGuard(PhotoshopPath, resolver);
        Assert.False(guard.Allows(Local, Remote));
        resolver.Identity = new(42, 100, PhotoshopPath);
        Assert.True(guard.Allows(Local, Remote));
    }

    [Fact]
    public void PeerGuardFailsClosedWhenOsIdentityCannotBeRead()
    {
        var resolver = new Resolver { Error = new Win32Exception(5) };
        var guard = new BridgeSelectedHostPeerGuard(PhotoshopPath, resolver);
        Assert.False(guard.Allows(Local, Remote));
        resolver.Error = null;
        resolver.Identity = new(42, 100, PhotoshopPath);
        Assert.True(guard.Allows(Local, Remote));
    }

    [Fact]
    public async Task PeerGuardConcurrentFirstConnectionsPinExactlyOneProcess()
    {
        var guard = new BridgeSelectedHostPeerGuard(PhotoshopPath, new PerPortResolver());
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(55201, 2).Select(port => Task.Run(async () =>
        {
            await go.Task;
            return (Port: port, Allowed: guard.Allows(Local, new(IPAddress.Loopback, port)));
        })).ToArray();
        go.SetResult();
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results, item => item.Allowed);
        Assert.True(guard.Allows(Local, new(IPAddress.Loopback, winner.Port)));
        Assert.False(guard.Allows(Local, new(IPAddress.Loopback, results.Single(item => !item.Allowed).Port)));
    }

    private sealed class Resolver : IBridgePeerProcessResolver
    {
        public BridgePeerProcessIdentity? Identity { get; set; }
        public Exception? Error { get; set; }
        public int Reads { get; private set; }
        public BridgePeerProcessIdentity? Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
        {
            Reads++;
            if (Error is not null) throw Error;
            return Identity;
        }
    }

    private sealed class PerPortResolver : IBridgePeerProcessResolver
    {
        public BridgePeerProcessIdentity Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint) =>
            new(remoteEndpoint.Port, 100, PhotoshopPath);
    }
}
