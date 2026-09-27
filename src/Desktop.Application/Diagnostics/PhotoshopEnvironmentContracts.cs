namespace Desktop.Application.Diagnostics;

[Flags]
public enum PhotoshopDiscoverySource
{
    None = 0,
    RunningProcess = 1,
    Registry = 2,
    StandardDirectory = 4,
    UserSelectedFile = 8
}

/// <summary>Executable metadata only. It does not attest an authenticated host or production readiness.</summary>
public sealed record PhotoshopInstallation(
    string ExecutablePath,
    string NumericFileVersion,
    string FileVersionDisplay,
    PhotoshopDiscoverySource Source,
    bool IsRunning)
{
    public bool IsVersionCandidate => Version.TryParse(NumericFileVersion, out var version)
        && version.Major == 26 && version.Minor == 8 && version.Build == 1;
}

public sealed record PhotoshopEnvironmentSnapshot(
    DateTimeOffset ObservedAt,
    string OsVersion,
    string OsArchitecture,
    bool IsWindowsClient,
    IReadOnlyList<PhotoshopInstallation> Installations,
    int UnavailableSources,
    bool Truncated)
{
    public bool IsOsCandidate => IsWindowsClient && OsArchitecture == "X64"
        && Version.TryParse(OsVersion, out var version) && version.Major == 10 && version.Build >= 22000;
}

/// <summary>Explicit local read only discovery; Latest is an in-process historical observation, never live readiness.</summary>
public interface IPhotoshopEnvironmentProbe
{
    PhotoshopEnvironmentSnapshot? Latest { get; }

    Task<PhotoshopEnvironmentSnapshot> DetectAsync(string? additionalExecutablePath, CancellationToken cancellationToken);
}
