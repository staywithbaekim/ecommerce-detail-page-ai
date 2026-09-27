using System.ComponentModel;
using System.Net;
using System.Security;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Local admission only; it never replaces message authentication or host capability checks.</summary>
public interface IBridgePeerGuard
{
    bool Allows(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint);
}

/// <summary>
/// Pins one OS process for one owner. The caller must validate and hold the selected executable's
/// file identity separately; this guard does not discover, launch, or trust a client-reported host.
/// </summary>
public sealed class BridgeSelectedHostPeerGuard : IBridgePeerGuard
{
    private readonly string _executablePath;
    private readonly IBridgePeerProcessResolver _resolver;
    private readonly object _sync = new();
    private BridgePeerProcessIdentity? _pinned;

    public BridgeSelectedHostPeerGuard(string executablePath, IBridgePeerProcessResolver resolver)
    {
        _executablePath = NormalizeExecutablePath(executablePath);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool Allows(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        if (!IPAddress.Loopback.Equals(localEndpoint.Address) || !IPAddress.Loopback.Equals(remoteEndpoint.Address)
            || localEndpoint.Port == 0 || remoteEndpoint.Port == 0) return false;
        try
        {
            var identity = _resolver.Resolve(localEndpoint, remoteEndpoint);
            if (identity is not { ProcessId: > 0, StartTimeUtcTicks: > 0 }
                || !string.Equals(_executablePath, NormalizeExecutablePath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                return false;
            lock (_sync)
            {
                if (_pinned is not null)
                    return _pinned.ProcessId == identity.ProcessId && _pinned.StartTimeUtcTicks == identity.StartTimeUtcTicks;
                _pinned = identity;
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or ArgumentException or SecurityException)
        {
            // Failure to establish OS identity is never permission to fall back to a build string.
            return false;
        }
    }

    private static string NormalizeExecutablePath(string path)
    {
        // Formal selection integration must restrict Photoshop.exe and its version. This boundary
        // accepts an already validated local executable so isolated socket tests can use testhost.
        if (string.IsNullOrWhiteSpace(path) || path.Length is < 4 or > 4096
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path[2..].Contains(':') || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '?' or '*' or '/')
            || path[3..].Split('\\').Any(part => part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("BRIDGE_HOST_IDENTITY_INVALID", nameof(path));
        return Path.GetFullPath(path);
    }
}
