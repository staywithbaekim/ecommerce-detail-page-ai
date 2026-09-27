using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Desktop.Infrastructure.Bridge;
using Xunit.Sdk;

namespace Desktop.Infrastructure.Tests;

public sealed class WindowsBridgePeerProcessResolverTests
{
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 0x1234);
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 0xabcd);

    [Fact]
    public void ParserReturnsOwnerOfExactReversedEstablishedTupleWithNetworkOrderPorts()
    {
        var table = Table(
            new(5, Server, Client, 11),
            new(2, Client, Server, 22),
            new(5, new(IPAddress.Parse("127.0.0.2"), Client.Port), Server, 33),
            new(5, Client, new(IPAddress.Loopback, Server.Port + 1), 44),
            new(5, Client, Server, 4242));

        Assert.Equal(4242, WindowsBridgePeerProcessResolver.ParseOwnerPidTable(table, Server, Client));
    }

    [Fact]
    public void ParserRequiresOneUniqueOwnerForAllExactRows()
    {
        var duplicate = Table(
            new(5, Client, Server, 4242),
            new(5, Client, Server, 4242));
        var ambiguous = Table(
            new(5, Client, Server, 4242),
            new(5, Client, Server, 4243));

        Assert.Equal(4242, WindowsBridgePeerProcessResolver.ParseOwnerPidTable(duplicate, Server, Client));
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(ambiguous, Server, Client));
    }

    [Fact]
    public void ParserRejectsMalformedRowsInvalidOwnersAndOversizedSnapshots()
    {
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable([], Server, Client));
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(new byte[3], Server, Client));

        var truncated = Table(new TcpRow(5, Client, Server, 4242));
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, 2);
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(truncated, Server, Client));

        var impossibleCount = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(impossibleCount, uint.MaxValue);
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(impossibleCount, Server, Client));
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(
            Table(new TcpRow(5, Client, Server, 0)), Server, Client));
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(
            Table(new TcpRow(5, Client, Server, (uint)int.MaxValue + 1)), Server, Client));

        var maximum = Table(new TcpRow(5, Client, Server, 4242));
        Array.Resize(ref maximum, WindowsBridgePeerProcessResolver.MaximumTcpTableBytes);
        Assert.Equal(4242, WindowsBridgePeerProcessResolver.ParseOwnerPidTable(maximum, Server, Client));
        Array.Resize(ref maximum, WindowsBridgePeerProcessResolver.MaximumTcpTableBytes + 1);
        Assert.Null(WindowsBridgePeerProcessResolver.ParseOwnerPidTable(maximum, Server, Client));
    }

    [Fact]
    public void ParserAcceptsTheNonzeroPortBoundaries()
    {
        var server = new IPEndPoint(IPAddress.Loopback, 1);
        var client = new IPEndPoint(IPAddress.Loopback, ushort.MaxValue);

        Assert.Equal(77, WindowsBridgePeerProcessResolver.ParseOwnerPidTable(
            Table(new TcpRow(5, client, server, 77)), server, client));
    }

    [Fact]
    public void ResolveRejectsInvalidEndpointsBeforeReadingTheTcpTable()
    {
        var tableReads = 0;
        var identityReads = 0;
        var resolver = new WindowsBridgePeerProcessResolver(
            (_, _) => { tableReads++; return 42; },
            processId => { identityReads++; return new(processId, 1, @"C:\testhost.exe"); });
        var valid = new IPEndPoint(IPAddress.Loopback, 12345);

        Assert.Null(resolver.Resolve(null!, valid));
        Assert.Null(resolver.Resolve(valid, null!));
        Assert.Null(resolver.Resolve(new(IPAddress.Any, 12345), valid));
        Assert.Null(resolver.Resolve(new(IPAddress.Parse("127.0.0.2"), 12345), valid));
        Assert.Null(resolver.Resolve(new(IPAddress.IPv6Loopback, 12345), valid));
        Assert.Null(resolver.Resolve(valid, new(IPAddress.Any, 12345)));
        Assert.Null(resolver.Resolve(valid, new(IPAddress.IPv6Loopback, 12345)));
        Assert.Null(resolver.Resolve(new(IPAddress.Loopback, 0), valid));
        Assert.Null(resolver.Resolve(valid, new(IPAddress.Loopback, 0)));
        Assert.Equal(0, tableReads);
        Assert.Equal(0, identityReads);
    }

    [Fact]
    public void ResolveFailsClosedForLookupAndProcessIdentityErrors()
    {
        var valid = new IPEndPoint(IPAddress.Loopback, 12345);
        var identityReads = 0;
        var noOwner = new WindowsBridgePeerProcessResolver(
            (_, _) => null,
            _ => { identityReads++; return new(1, 1, @"C:\testhost.exe"); });

        Assert.Null(noOwner.Resolve(valid, valid));
        Assert.Equal(0, identityReads);
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => throw new IOException("table read failed"),
            _ => throw new InvalidOperationException()).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => null).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => throw new Win32Exception()).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => new(43, 1, @"C:\testhost.exe")).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => new(42, 0, @"C:\testhost.exe")).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => new(42, DateTime.MaxValue.Ticks + 1, @"C:\testhost.exe")).Resolve(valid, valid));
        Assert.Null(new WindowsBridgePeerProcessResolver(
            (_, _) => 42,
            _ => new(42, 1, " ")).Resolve(valid, valid));
    }

    [Fact]
    public void ResolveReturnsTheValidatedIdentityOnce()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 23456);
        var remote = new IPEndPoint(IPAddress.Loopback, 34567);
        var expected = new BridgePeerProcessIdentity(42, 638000000000000000, @"C:\testhost.exe");
        var tableReads = 0;
        var identityReads = 0;
        var resolver = new WindowsBridgePeerProcessResolver(
            (actualLocal, actualRemote) =>
            {
                tableReads++;
                Assert.Equal(local, actualLocal);
                Assert.Equal(remote, actualRemote);
                return expected.ProcessId;
            },
            processId =>
            {
                identityReads++;
                Assert.Equal(expected.ProcessId, processId);
                return expected;
            });

        Assert.Equal(expected, resolver.Resolve(local, remote));
        Assert.Equal(1, tableReads);
        Assert.Equal(1, identityReads);
    }

    [Fact]
    public async Task RealResolverReturnsCurrentTesthostForAnEphemeralLoopbackConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows TCP owner and process identity APIs require Windows.");
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var listenerEndpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, listenerEndpoint.Port);
            using var accepted = await accept;

            var localEndpoint = Assert.IsType<IPEndPoint>(accepted.Client.LocalEndPoint);
            var remoteEndpoint = Assert.IsType<IPEndPoint>(accepted.Client.RemoteEndPoint);
            var identity = new WindowsBridgePeerProcessResolver().Resolve(localEndpoint, remoteEndpoint);

            Assert.NotNull(identity);
            using var current = Process.GetCurrentProcess();
            Assert.Equal(current.Id, identity.ProcessId);
            Assert.Equal(current.StartTime.ToUniversalTime().Ticks, identity.StartTimeUtcTicks);
            var expectedPath = current.MainModule?.FileName;
            Assert.False(string.IsNullOrWhiteSpace(expectedPath));
            Assert.True(
                string.Equals(
                    Path.GetFullPath(expectedPath!),
                    Path.GetFullPath(identity.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase),
                $"Expected current testhost path '{expectedPath}', resolved '{identity.ExecutablePath}'.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] Table(params TcpRow[] rows)
    {
        var table = new byte[checked(sizeof(uint) + (rows.Length * 6 * sizeof(uint)))];
        BinaryPrimitives.WriteUInt32LittleEndian(table, checked((uint)rows.Length));
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var bytes = table.AsSpan(sizeof(uint) + (index * 6 * sizeof(uint)), 6 * sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, row.State);
            WriteAddress(bytes[4..8], row.Local.Address);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[8..10], checked((ushort)row.Local.Port));
            WriteAddress(bytes[12..16], row.Remote.Address);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[16..18], checked((ushort)row.Remote.Port));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..24], row.ProcessId);
        }
        return table;
    }

    private static void WriteAddress(Span<byte> destination, IPAddress address)
    {
        Assert.True(address.TryWriteBytes(destination, out var written));
        Assert.Equal(destination.Length, written);
    }

    private readonly record struct TcpRow(uint State, IPEndPoint Local, IPEndPoint Remote, uint ProcessId);
}
