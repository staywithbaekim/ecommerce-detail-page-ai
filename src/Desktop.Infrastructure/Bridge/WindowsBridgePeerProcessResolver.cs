using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Desktop.Infrastructure.Bridge;

public sealed record BridgePeerProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

public interface IBridgePeerProcessResolver
{
    BridgePeerProcessIdentity? Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint);
}

/// <summary>
/// Resolves only the Windows owner of one exact inbound IPv4 loopback TCP connection.
/// It never reads connection payloads, probes ports, or retains a machine-wide connection snapshot.
/// </summary>
public sealed class WindowsBridgePeerProcessResolver : IBridgePeerProcessResolver
{
    internal const int MaximumTcpTableBytes = 1024 * 1024;

    private const int TcpTableHeaderBytes = sizeof(uint);
    private const int TcpRowBytes = 6 * sizeof(uint);
    private const uint ErrorInsufficientBuffer = 122;
    private const uint MibTcpStateEstablished = 5;
    private const uint NoError = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int MaximumProcessImageCharacters = 32768;

    private readonly Func<IPEndPoint, IPEndPoint, int?> _peerProcessIdResolver;
    private readonly Func<int, BridgePeerProcessIdentity?> _processIdentityReader;

    public WindowsBridgePeerProcessResolver()
        : this(ResolvePeerProcessId, ReadProcessIdentity)
    {
    }

    internal WindowsBridgePeerProcessResolver(
        Func<IPEndPoint, IPEndPoint, int?> peerProcessIdResolver,
        Func<int, BridgePeerProcessIdentity?> processIdentityReader)
    {
        _peerProcessIdResolver = peerProcessIdResolver ?? throw new ArgumentNullException(nameof(peerProcessIdResolver));
        _processIdentityReader = processIdentityReader ?? throw new ArgumentNullException(nameof(processIdentityReader));
    }

    public BridgePeerProcessIdentity? Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        if (!IsAcceptedEndpoint(localEndpoint) || !IsAcceptedEndpoint(remoteEndpoint)) return null;

        try
        {
            var processId = _peerProcessIdResolver(localEndpoint, remoteEndpoint);
            if (!processId.HasValue || processId.Value <= 0) return null;

            var identity = _processIdentityReader(processId.Value);
            if (identity is null
                || identity.ProcessId != processId.Value
                || identity.StartTimeUtcTicks <= 0
                || identity.StartTimeUtcTicks > DateTime.MaxValue.Ticks
                || string.IsNullOrWhiteSpace(identity.ExecutablePath)
                || identity.ExecutablePath.Contains('\0')) return null;

            return identity;
        }
        catch (Exception error) when (error is not OutOfMemoryException
                                      and not StackOverflowException
                                      and not AccessViolationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a MIB_TCPTABLE_OWNER_PID snapshot without retaining rows. The requested peer row must be
    /// ESTABLISHED and exactly reverse the server-side endpoints: client local equals remoteEndpoint,
    /// and client remote equals localEndpoint.
    /// </summary>
    internal static int? ParseOwnerPidTable(
        ReadOnlySpan<byte> table,
        IPEndPoint localEndpoint,
        IPEndPoint remoteEndpoint)
    {
        if (!IsAcceptedEndpoint(localEndpoint) || !IsAcceptedEndpoint(remoteEndpoint)
            || table.Length < TcpTableHeaderBytes || table.Length > MaximumTcpTableBytes) return null;

        var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var availableRows = (table.Length - TcpTableHeaderBytes) / TcpRowBytes;
        if (rowCount > (uint)availableRows) return null;

        Span<byte> serverAddress = stackalloc byte[4];
        Span<byte> clientAddress = stackalloc byte[4];
        if (!localEndpoint.Address.TryWriteBytes(serverAddress, out var serverAddressLength)
            || serverAddressLength != serverAddress.Length
            || !remoteEndpoint.Address.TryWriteBytes(clientAddress, out var clientAddressLength)
            || clientAddressLength != clientAddress.Length) return null;

        int? uniqueProcessId = null;
        for (var index = 0; index < (int)rowCount; index++)
        {
            var row = table.Slice(TcpTableHeaderBytes + (index * TcpRowBytes), TcpRowBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row) != MibTcpStateEstablished
                || !MatchesEndpoint(row, 4, 8, clientAddress, remoteEndpoint.Port)
                || !MatchesEndpoint(row, 12, 16, serverAddress, localEndpoint.Port)) continue;

            var owner = BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            if (owner == 0 || owner > int.MaxValue) return null;

            var candidate = (int)owner;
            if (uniqueProcessId.HasValue && uniqueProcessId.Value != candidate) return null;
            uniqueProcessId = candidate;
        }

        return uniqueProcessId;
    }

    private static bool MatchesEndpoint(
        ReadOnlySpan<byte> row,
        int addressOffset,
        int portOffset,
        ReadOnlySpan<byte> address,
        int port) =>
        row.Slice(addressOffset, 4).SequenceEqual(address)
        && BinaryPrimitives.ReadUInt16BigEndian(row.Slice(portOffset, sizeof(ushort))) == port;

    private static bool IsAcceptedEndpoint(IPEndPoint? endpoint) =>
        endpoint is not null
        && endpoint.AddressFamily == AddressFamily.InterNetwork
        && endpoint.Address.Equals(IPAddress.Loopback)
        && endpoint.Port is >= 1 and <= ushort.MaxValue;

    private static int? ResolvePeerProcessId(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        var table = ReadOwnerPidTable();
        return table.HasValue ? ParseOwnerPidTable(table.Value.Span, localEndpoint, remoteEndpoint) : null;
    }

    private static ReadOnlyMemory<byte>? ReadOwnerPidTable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        uint requiredBytes = 0;
        var status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref requiredBytes,
            order: false,
            (int)AddressFamily.InterNetwork,
            TcpTableClass.OwnerPidAll,
            reserved: 0);
        if (status is not (NoError or ErrorInsufficientBuffer)
            || requiredBytes < TcpTableHeaderBytes
            || requiredBytes > MaximumTcpTableBytes) return null;

        var buffer = new byte[checked((int)requiredBytes)];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var writtenBytes = requiredBytes;
            status = GetExtendedTcpTable(
                pinned.AddrOfPinnedObject(),
                ref writtenBytes,
                order: false,
                (int)AddressFamily.InterNetwork,
                TcpTableClass.OwnerPidAll,
                reserved: 0);
            if (status != NoError
                || writtenBytes < TcpTableHeaderBytes
                || writtenBytes > requiredBytes) return null;

            return new ReadOnlyMemory<byte>(buffer, 0, checked((int)writtenBytes));
        }
        finally
        {
            pinned.Free();
        }
    }

    private static BridgePeerProcessIdentity? ReadProcessIdentity(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return null;

        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, (uint)processId);
        if (process.IsInvalid) return null;

        var image = new char[MaximumProcessImageCharacters];
        var imageLength = (uint)image.Length;
        if (!QueryFullProcessImageNameW(process, flags: 0, image, ref imageLength)
            || imageLength == 0 || imageLength >= image.Length) return null;

        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return null;
        if (!GetExitCodeProcess(process, out var exitCode) || exitCode != StillActive) return null;

        var creationFileTime = ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        if (creationFileTime <= 0) return null;

        var executablePath = new string(image, 0, checked((int)imageLength));
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('\0')) return null;

        long startTimeUtcTicks;
        try
        {
            startTimeUtcTicks = DateTime.FromFileTimeUtc(creationFileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return new BridgePeerProcessIdentity(processId, startTimeUtcTicks, executablePath);
    }

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
