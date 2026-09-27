using System.Collections.Concurrent;
using Desktop.Application.Diagnostics;
using Desktop.Infrastructure.Diagnostics;
using Xunit.Sdk;

namespace Desktop.Infrastructure.Tests;

public sealed class PhotoshopEnvironmentProbeTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NonSystemDriveCandidatesAreMergedCaseInsensitivelyAcrossSources()
    {
        const string manualPath = @"D:\Adobe\Adobe Photoshop 2025\Photoshop.exe";
        const string otherPath = @"E:\Creative Apps\Adobe Photoshop 2025\Photoshop.exe";
        var reader = new StubReader
        {
            Discovery = SupportedDiscovery(
                new(manualPath.ToUpperInvariant(), PhotoshopDiscoverySource.RunningProcess),
                new(manualPath, PhotoshopDiscoverySource.Registry),
                new(otherPath, PhotoshopDiscoverySource.StandardDirectory),
                new(otherPath.ToUpperInvariant(), PhotoshopDiscoverySource.Registry))
        };
        var probe = CreateProbe(reader);

        var snapshot = await probe.DetectAsync(manualPath, CancellationToken.None);

        Assert.Equal(2, snapshot.Installations.Count);
        var manual = Assert.Single(snapshot.Installations, item =>
            string.Equals(item.ExecutablePath, Path.GetFullPath(manualPath), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            PhotoshopDiscoverySource.UserSelectedFile | PhotoshopDiscoverySource.RunningProcess | PhotoshopDiscoverySource.Registry,
            manual.Source);
        Assert.True(manual.IsRunning);
        var other = Assert.Single(snapshot.Installations, item =>
            string.Equals(item.ExecutablePath, Path.GetFullPath(otherPath), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PhotoshopDiscoverySource.StandardDirectory | PhotoshopDiscoverySource.Registry, other.Source);
        Assert.False(other.IsRunning);
        Assert.Equal(1, reader.ReadCalls.Count(call =>
            string.Equals(call.Path, manualPath, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, reader.ReadCalls.Count(call =>
            string.Equals(call.Path, otherPath, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NumericVersionMarksCandidateWhileOriginalDisplayRemainsUnalteredAndDoesNotClaimRunningHost()
    {
        const string path = @"D:\Adobe\Photoshop.exe";
        const string display = "26.8 20250807.r.44 x64";
        var reader = new StubReader
        {
            Discovery = SupportedDiscovery(new PhotoshopPathCandidate(path, PhotoshopDiscoverySource.Registry)),
            ReadInstallationHandler = (normalizedPath, source) =>
                new(normalizedPath, "26.8.1.8", display, source, IsRunning: false)
        };

        var snapshot = await CreateProbe(reader).DetectAsync(null, CancellationToken.None);

        var installation = Assert.Single(snapshot.Installations);
        Assert.True(installation.IsVersionCandidate);
        Assert.Equal("26.8.1.8", installation.NumericFileVersion);
        Assert.Equal(display, installation.FileVersionDisplay);
        Assert.Equal(PhotoshopDiscoverySource.Registry, installation.Source);
        Assert.False(installation.IsRunning);
    }

    [Fact]
    public void SupportedWindowsClientBuildIsAnOsCandidate()
    {
        Assert.True(Snapshot("10.0.26100", "X64", isWindowsClient: true).IsOsCandidate);
    }

    [Theory]
    [InlineData("10.0.19045", "X64", true)]
    [InlineData("10.0.26100", "X86", true)]
    [InlineData("10.0.26100", "Arm64", true)]
    [InlineData("10.0.26100", "X64", false)]
    public void Windows10OrUnsupportedArchitectureOrNonClientIsOutsideOsRange(
        string version,
        string architecture,
        bool isWindowsClient)
    {
        Assert.False(Snapshot(version, architecture, isWindowsClient).IsOsCandidate);
    }

    [Theory]
    [InlineData(@"\\server\share\Photoshop.exe")]
    [InlineData(@"\\?\C:\Adobe\Photoshop.exe")]
    [InlineData(@"\\.\C:\Adobe\Photoshop.exe")]
    [InlineData(@"C:\Adobe\Photoshop.exe:Zone.Identifier")]
    [InlineData(@"C:\Adobe\NotPhotoshop.exe")]
    [InlineData(@"C:\Adobe \Photoshop.exe")]
    [InlineData(@"C:\Adobe.\Photoshop.exe")]
    [InlineData(@"C:\Adobe\Photoshop.exe ")]
    [InlineData(@"C:\Adobe\Photoshop.exe.")]
    [InlineData(@"C:\Adobe\\Photoshop.exe")]
    [InlineData(@"C:/Adobe/Photoshop.exe")]
    [InlineData("Photoshop.exe")]
    public void UnsafeOrNonPhotoshopExecutablePathsAreRejected(string path)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsPhotoshopEnvironmentProbe.NormalizeExecutablePath(path));

        Assert.Equal("PHOTOSHOP_LOCAL_EXECUTABLE_REQUIRED", error.Message);
    }

    [Fact]
    public async Task InvalidAndUnreadableDiscoveredCandidatesAreIsolatedFromReadableInstallations()
    {
        const string firstPath = @"D:\Adobe\First\Photoshop.exe";
        const string unreadablePath = @"E:\Adobe\Unreadable\Photoshop.exe";
        const string lastPath = @"F:\Adobe\Last\Photoshop.exe";
        var reader = new StubReader
        {
            Discovery = new(
                [
                    new(firstPath, PhotoshopDiscoverySource.Registry),
                    new(@"\\server\share\Photoshop.exe", PhotoshopDiscoverySource.Registry),
                    new(unreadablePath, PhotoshopDiscoverySource.StandardDirectory),
                    new(lastPath, PhotoshopDiscoverySource.RunningProcess)
                ],
                "10.0.26100",
                "X64",
                IsWindowsClient: true,
                UnavailableSources: 2),
            ReadInstallationHandler = (path, source) =>
                string.Equals(path, unreadablePath, StringComparison.OrdinalIgnoreCase)
                    ? throw new IOException("unreadable test candidate")
                    : Installation(path, source)
        };
        var probe = CreateProbe(reader);

        var snapshot = await probe.DetectAsync(null, CancellationToken.None);

        Assert.Equal(2, snapshot.Installations.Count);
        Assert.Contains(snapshot.Installations, item =>
            string.Equals(item.ExecutablePath, firstPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Installations, item =>
            string.Equals(item.ExecutablePath, lastPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, snapshot.UnavailableSources);
        Assert.Same(snapshot, probe.Latest);
    }

    [Fact]
    public async Task ManualReadFailureLeavesThePreviousLatestSnapshotIntact()
    {
        const string discoveredPath = @"D:\Adobe\Installed\Photoshop.exe";
        const string manualPath = @"E:\Adobe\Unreadable\Photoshop.exe";
        var reader = new StubReader
        {
            Discovery = SupportedDiscovery(
                new PhotoshopPathCandidate(discoveredPath, PhotoshopDiscoverySource.Registry)),
            ReadInstallationHandler = (path, source) =>
                string.Equals(path, manualPath, StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("manual test failure")
                    : Installation(path, source)
        };
        var probe = CreateProbe(reader);
        var previous = await probe.DetectAsync(null, CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            probe.DetectAsync(manualPath, CancellationToken.None));

        Assert.Same(previous, probe.Latest);
        Assert.Equal(1, reader.DiscoverCalls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotReadOrPublish()
    {
        var reader = new StubReader();
        var probe = CreateProbe(reader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.DetectAsync(null, cancellation.Token));

        Assert.Equal(0, reader.DiscoverCalls);
        Assert.Null(probe.Latest);
    }

    [Fact]
    public async Task CancellationDuringDiscoveryDoesNotPublishAndReleasesSingleFlightGate()
    {
        using var reader = new ControlledReader();
        var probe = CreateProbe(reader);
        using var cancellation = new CancellationTokenSource();
        var cancelledDetection = probe.DetectAsync(null, cancellation.Token);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledDetection);

        Assert.Null(probe.Latest);
        var retry = await probe.DetectAsync(null, CancellationToken.None);
        Assert.Same(retry, probe.Latest);
    }

    [Fact]
    public async Task ConcurrentDetectionIsRejectedUntilTheActiveDetectionCompletes()
    {
        using var reader = new ControlledReader();
        var probe = CreateProbe(reader);
        var firstDetection = probe.DetectAsync(null, CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                probe.DetectAsync(null, CancellationToken.None));
            Assert.Equal("PHOTOSHOP_DETECTION_BUSY", error.Message);
        }
        finally
        {
            reader.Release();
        }

        var first = await firstDetection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(first, probe.Latest);
    }

    [Fact]
    public async Task CancellationAfterSnapshotCreationDoesNotReplaceLatest()
    {
        var reader = new StubReader();
        using var lateCancellation = new CancellationTokenSource();
        var clock = new CancelOnSecondObservationTimeProvider(ObservedAt, lateCancellation);
        var probe = new WindowsPhotoshopEnvironmentProbe(reader, clock);
        var previous = await probe.DetectAsync(null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.DetectAsync(null, lateCancellation.Token));

        Assert.Same(previous, probe.Latest);
        Assert.Equal(2, reader.DiscoverCalls);
    }

    [Fact]
    public async Task CandidateCollectionIsBoundedAndReportsTruncation()
    {
        var candidates = Enumerable.Range(0, WindowsPhotoshopEnvironmentProbe.MaximumCandidates + 17)
            .Select(index => new PhotoshopPathCandidate(
                $@"D:\Adobe\Photoshop-{index:D3}\Photoshop.exe",
                PhotoshopDiscoverySource.Registry))
            .ToArray();
        var reader = new StubReader
        {
            Discovery = SupportedDiscovery(candidates),
            ReadInstallationHandler = (path, source) => Installation(path, source, "25.0.0.0")
        };

        var snapshot = await CreateProbe(reader).DetectAsync(null, CancellationToken.None);

        Assert.Equal(WindowsPhotoshopEnvironmentProbe.MaximumCandidates, snapshot.Installations.Count);
        Assert.Equal(WindowsPhotoshopEnvironmentProbe.MaximumCandidates, reader.ReadCalls.Count);
        Assert.True(snapshot.Truncated);
        var excluded = Path.GetFullPath(
            $@"D:\Adobe\Photoshop-{WindowsPhotoshopEnvironmentProbe.MaximumCandidates:D3}\Photoshop.exe");
        Assert.DoesNotContain(reader.ReadCalls, call =>
            string.Equals(call.Path, excluded, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallationsAreOrderedByRunningThenVersionCandidateThenPath()
    {
        const string runningCandidate = @"D:\Adobe\B\Photoshop.exe";
        const string runningOther = @"D:\Adobe\M\Photoshop.exe";
        const string stoppedCandidate = @"D:\Adobe\A\Photoshop.exe";
        const string stoppedOther = @"D:\Adobe\Z\Photoshop.exe";
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(runningCandidate),
            Path.GetFullPath(stoppedCandidate)
        };
        var reader = new StubReader
        {
            Discovery = SupportedDiscovery(
                new(stoppedOther, PhotoshopDiscoverySource.Registry),
                new(stoppedCandidate, PhotoshopDiscoverySource.StandardDirectory),
                new(runningOther, PhotoshopDiscoverySource.RunningProcess),
                new(runningCandidate, PhotoshopDiscoverySource.RunningProcess)),
            ReadInstallationHandler = (path, source) => Installation(
                path,
                source,
                candidatePaths.Contains(path) ? "26.8.1.8" : "25.0.0.0")
        };

        var snapshot = await CreateProbe(reader).DetectAsync(null, CancellationToken.None);

        Assert.Equal(
            [
                Path.GetFullPath(runningCandidate),
                Path.GetFullPath(runningOther),
                Path.GetFullPath(stoppedCandidate),
                Path.GetFullPath(stoppedOther)
            ],
            snapshot.Installations.Select(item => item.ExecutablePath));
    }

    [Fact]
    public void RealReaderRejectsLocalExecutableWithNonPhotoshopMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Local executable metadata validation requires Windows.");
        }

        using var directory = new TestDirectory();
        var disguisedExecutable = Path.Combine(directory.Path, "Photoshop.exe");
        File.Copy(LocalNonPhotoshopExecutable(), disguisedExecutable);

        var error = Assert.Throws<InvalidDataException>(() =>
            new WindowsPhotoshopEnvironmentReader().ReadInstallation(
                disguisedExecutable,
                PhotoshopDiscoverySource.UserSelectedFile));

        Assert.Equal("PHOTOSHOP_EXECUTABLE_METADATA_UNRECOGNIZED", error.Message);
    }

    [Fact(Skip = "Requires directory symlink creation rights; current runner does not support dynamic skip. Validate an owned junction through the desktop separately without changing system permissions.")]
    public void RealReaderRejectsOwnedReparseDirectoryWhenPlatformAllowsCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Reparse-point rejection requires Windows.");
        }

        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, "target");
        var link = Path.Combine(directory.Path, "link");
        Directory.CreateDirectory(target);
        File.Copy(
            LocalNonPhotoshopExecutable(),
            Path.Combine(target, "Photoshop.exe"));
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Could not create an owned directory symbolic link without changing permissions: {error.GetType().Name}");
        }

        try
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new WindowsPhotoshopEnvironmentReader().ReadInstallation(
                    Path.Combine(link, "Photoshop.exe"),
                    PhotoshopDiscoverySource.UserSelectedFile));

            Assert.Equal("PHOTOSHOP_REPARSE_PATH_REJECTED", error.Message);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    private static WindowsPhotoshopEnvironmentProbe CreateProbe(IPhotoshopEnvironmentReader reader) =>
        new(reader, new FixedTimeProvider(ObservedAt));

    private static string LocalNonPhotoshopExecutable()
    {
        var path = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
        Assert.False(string.Equals("Photoshop.exe", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        return path!;
    }

    private static PhotoshopDiscovery SupportedDiscovery(params PhotoshopPathCandidate[] candidates) =>
        new(candidates, "10.0.26100", "X64", IsWindowsClient: true);

    private static PhotoshopEnvironmentSnapshot Snapshot(
        string osVersion,
        string architecture,
        bool isWindowsClient) =>
        new(ObservedAt, osVersion, architecture, isWindowsClient, [], 0, Truncated: false);

    private static PhotoshopInstallation Installation(
        string path,
        PhotoshopDiscoverySource source,
        string numericVersion = "26.8.1.8") =>
        new(
            Path.GetFullPath(path),
            numericVersion,
            $"original display {numericVersion}",
            source,
            source.HasFlag(PhotoshopDiscoverySource.RunningProcess));

    private sealed class StubReader : IPhotoshopEnvironmentReader
    {
        private int _discoverCalls;

        public PhotoshopDiscovery Discovery { get; init; } = SupportedDiscovery();

        public Func<string, PhotoshopDiscoverySource, PhotoshopInstallation> ReadInstallationHandler { get; init; } =
            static (path, source) => Installation(path, source);

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public ConcurrentQueue<(string Path, PhotoshopDiscoverySource Source)> ReadCalls { get; } = new();

        public PhotoshopDiscovery Discover(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _discoverCalls);
            return Discovery;
        }

        public PhotoshopInstallation ReadInstallation(string path, PhotoshopDiscoverySource source)
        {
            ReadCalls.Enqueue((path, source));
            return ReadInstallationHandler(path, source);
        }
    }

    private sealed class ControlledReader : IPhotoshopEnvironmentReader, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _discoverCalls;

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PhotoshopDiscovery Discover(CancellationToken token)
        {
            var call = Interlocked.Increment(ref _discoverCalls);
            if (call == 1)
            {
                Entered.TrySetResult(true);
                _release.Wait(token);
            }

            return SupportedDiscovery();
        }

        public PhotoshopInstallation ReadInstallation(string path, PhotoshopDiscoverySource source) =>
            Installation(path, source);

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CancelOnSecondObservationTimeProvider(
        DateTimeOffset value,
        CancellationTokenSource cancellation) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                cancellation.Cancel();
            }

            return value.AddMinutes(call - 1);
        }
    }
}
