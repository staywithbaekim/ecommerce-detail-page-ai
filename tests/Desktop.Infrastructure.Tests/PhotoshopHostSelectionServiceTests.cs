using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Xunit.Sdk;

namespace Desktop.Infrastructure.Tests;

public sealed class PhotoshopHostSelectionServiceTests
{
    private const string SupportedVersion = "26.8.1.8";
    private const long ExecutableLength = 24_681_357;
    private const string ExecutableHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset SelectedAt = new(2026, 9, 25, 2, 30, 0, TimeSpan.Zero);
    private static readonly IPEndPoint LocalEndpoint = new(IPAddress.Loopback, 17842);
    private static readonly IPEndPoint RemoteEndpoint = new(IPAddress.Loopback, 55123);

    [Fact]
    public async Task ReadAndAcquireReportThatNoHostHasBeenSelected()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var reader = new StubExecutableReader((_, _) =>
            throw new InvalidOperationException("Reader must not run without a selection record."));
        await using var service = CreateService(paths, reader);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(PhotoshopHostSelectionStatus.NotSelected, snapshot.Status);
        Assert.Null(snapshot.ExecutablePath);
        Assert.Null(snapshot.NumericFileVersion);
        Assert.Null(snapshot.SelectedAt);
        Assert.False(snapshot.IsInUse);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcquireAsync(CancellationToken.None));
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_REQUIRED", error.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ExplicitSelectionPersistsStrictRecordAndANewServiceReadsIt()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "selected");
        var holds = new ConcurrentQueue<CountingDisposable>();
        var reader = new StubExecutableReader((path, token) =>
        {
            token.ThrowIfCancellationRequested();
            var hold = new CountingDisposable();
            holds.Enqueue(hold);
            return Task.FromResult(Executable(path, hold));
        });

        await using (var service = CreateService(paths, reader))
        {
            var selected = await service.SelectAsync(selectedPath, CancellationToken.None);

            Assert.Equal(PhotoshopHostSelectionStatus.Valid, selected.Status);
            Assert.Equal(selectedPath, selected.ExecutablePath);
            Assert.Equal(SupportedVersion, selected.NumericFileVersion);
            Assert.Equal<DateTimeOffset?>(SelectedAt, selected.SelectedAt);
            Assert.False(selected.IsInUse);
        }

        var recordPath = RecordPath(paths);
        Assert.True(File.Exists(recordPath));
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(recordPath)))
        {
            var root = document.RootElement;
            Assert.Equal(6, root.EnumerateObject().Count());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(selectedPath, root.GetProperty("executable_path").GetString());
            Assert.Equal(SupportedVersion, root.GetProperty("numeric_file_version").GetString());
            Assert.Equal(ExecutableLength, root.GetProperty("length").GetInt64());
            Assert.Equal(ExecutableHash, root.GetProperty("sha256").GetString());
            Assert.Equal(SelectedAt, root.GetProperty("selected_at").GetDateTimeOffset());
        }

        await using (var reopened = CreateService(paths, reader))
        {
            var restored = await reopened.ReadAsync(CancellationToken.None);

            Assert.Equal(PhotoshopHostSelectionStatus.Valid, restored.Status);
            Assert.Equal(selectedPath, restored.ExecutablePath);
            Assert.Equal(SupportedVersion, restored.NumericFileVersion);
            Assert.Equal<DateTimeOffset?>(SelectedAt, restored.SelectedAt);
            Assert.False(restored.IsInUse);
        }

        Assert.Equal(2, reader.Calls);
        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.Equal(1, hold.DisposeCalls));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task UnsupportedSelectionPreservesOldRecordAndReleasesCandidateHold()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var oldBytes = await SeedSelectionAsync(paths, PhotoshopPath(directory, "old"));
        var hold = new CountingDisposable();
        var reader = new StubExecutableReader((path, _) => Task.FromResult(
            Executable(path, hold, version: "25.12.0.0")));
        await using var service = CreateService(paths, reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync(PhotoshopPath(directory, "unsupported"), CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_UNSUPPORTED", error.Message);
        Assert.Equal(1, hold.DisposeCalls);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task InvalidSelectionPathPreservesOldRecordWithoutCallingReader()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var oldBytes = await SeedSelectionAsync(paths, PhotoshopPath(directory, "old"));
        var reader = new StubExecutableReader((_, _) =>
            throw new InvalidOperationException("Invalid paths must be rejected before metadata is read."));
        await using var service = CreateService(paths, reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync(@"\\server\share\Photoshop.exe", CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_PATH_INVALID", error.Message);
        Assert.Equal(0, reader.Calls);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task CancelledSelectionPreservesOldRecordAndReleasesReaderHold()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var oldBytes = await SeedSelectionAsync(paths, PhotoshopPath(directory, "old"));
        var reader = new BlockingExecutableReader();
        await using var service = CreateService(paths, reader);
        using var cancellation = new CancellationTokenSource();

        var selection = service.SelectAsync(PhotoshopPath(directory, "cancelled"), cancellation.Token);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(1, reader.Hold.DisposeCalls);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
        reader.Release();
    }

    [Fact]
    public async Task ExecutableReadFailurePreservesOldRecord()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var oldBytes = await SeedSelectionAsync(paths, PhotoshopPath(directory, "old"));
        var reader = new StubExecutableReader((_, _) =>
            Task.FromException<SelectedPhotoshopExecutable>(new IOException("metadata unavailable")));
        await using var service = CreateService(paths, reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync(PhotoshopPath(directory, "unreadable"), CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_SELECTION_UNAVAILABLE", error.Message);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task AtomicWriteFailurePreservesOldRecordAndRemovesTemporaryFile()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var oldBytes = await SeedSelectionAsync(paths, PhotoshopPath(directory, "old"));
        var hold = new CountingDisposable();
        var reader = new StubExecutableReader((path, _) => Task.FromResult(Executable(path, hold)));
        await using var service = CreateService(paths, reader);
        await using var recordLock = new FileStream(
            RecordPath(paths), FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync(PhotoshopPath(directory, "replacement"), CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_SELECTION_UNAVAILABLE", error.Message);
        Assert.Equal(1, hold.DisposeCalls);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task RecordSizeBoundaryAcceptsExactlySixteenKiBAndRejectsOneByteMore()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "boundary");
        WriteRecord(paths, ValidRecordJson(selectedPath), PhotoshopHostSelectionService.MaximumRecordBytes);
        var reader = MatchingReader();
        await using var service = CreateService(paths, reader);

        var atLimit = await service.ReadAsync(CancellationToken.None);
        Assert.Equal(PhotoshopHostSelectionStatus.Valid, atLimit.Status);

        await File.AppendAllTextAsync(RecordPath(paths), " ", new UTF8Encoding(false));
        var overLimit = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(PhotoshopHostSelectionStatus.InvalidRecord, overLimit.Status);
        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [InlineData("record_version")]
    [InlineData("numeric_version")]
    [InlineData("duplicate_field")]
    [InlineData("bad_hash")]
    [InlineData("unknown_field")]
    public async Task ReadRejectsInvalidVersionDuplicateBadHashAndUnknownFields(string corruption)
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "strict-record");
        var valid = ValidRecordJson(selectedPath);
        var invalid = corruption switch
        {
            "record_version" => valid.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            "numeric_version" => valid.Replace(SupportedVersion, "26.8.2.0", StringComparison.Ordinal),
            "duplicate_field" => valid.Replace(
                "\"version\":1,", "\"version\":1,\"version\":1,", StringComparison.Ordinal),
            "bad_hash" => valid.Replace(ExecutableHash, "not-a-sha256", StringComparison.Ordinal),
            "unknown_field" => valid[..^1] + ",\"future_field\":true}",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        WriteRecord(paths, invalid);
        var reader = new StubExecutableReader((_, _) =>
            throw new InvalidOperationException("Invalid records must be rejected before opening an executable."));
        await using var service = CreateService(paths, reader);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(PhotoshopHostSelectionStatus.InvalidRecord, snapshot.Status);
        Assert.Null(snapshot.ExecutablePath);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("missing", PhotoshopHostSelectionStatus.Missing)]
    [InlineData("changed", PhotoshopHostSelectionStatus.Changed)]
    [InlineData("unavailable", PhotoshopHostSelectionStatus.Unavailable)]
    public async Task ReadSeparatesMissingChangedAndUnavailableExecutable(string failure,
        PhotoshopHostSelectionStatus expectedStatus)
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "classified");
        WriteRecord(paths, ValidRecordJson(selectedPath));
        var changedHold = new CountingDisposable();
        var reader = new StubExecutableReader((path, _) => failure switch
        {
            "missing" => Task.FromException<SelectedPhotoshopExecutable>(new FileNotFoundException()),
            "changed" => Task.FromResult(Executable(path, changedHold, length: ExecutableLength + 1)),
            "unavailable" => Task.FromException<SelectedPhotoshopExecutable>(new UnauthorizedAccessException()),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });
        await using var service = CreateService(paths, reader);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, snapshot.Status);
        Assert.Equal(selectedPath, snapshot.ExecutablePath);
        Assert.Equal(SupportedVersion, snapshot.NumericFileVersion);
        Assert.Equal<DateTimeOffset?>(SelectedAt, snapshot.SelectedAt);
        Assert.False(snapshot.IsInUse);
        Assert.Equal(failure == "changed" ? 1 : 0, changedHold.DisposeCalls);
    }

    [Fact]
    public async Task AcquireRejectsIdentityMismatchWithoutLeakingHoldOrBusyState()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "selected");
        WriteRecord(paths, ValidRecordJson(selectedPath));
        var mismatchedHold = new CountingDisposable();
        var reader = new StubExecutableReader((path, _) => Task.FromResult(
            Executable(path, mismatchedHold, length: ExecutableLength + 1)));
        await using var service = CreateService(paths, reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcquireAsync(CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_SELECTION_INVALID", error.Message);
        Assert.Equal(1, mismatchedHold.DisposeCalls);

        reader.Handler = (path, _) => Task.FromResult(Executable(path));
        var replacement = await service.SelectAsync(
            PhotoshopPath(directory, "replacement"), CancellationToken.None);
        Assert.Equal(PhotoshopHostSelectionStatus.Valid, replacement.Status);
    }

    [Fact]
    public async Task ActiveLeaseBlocksSelectionAndSecondAcquireUntilIdempotentDispose()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "selected");
        var replacementPath = PhotoshopPath(directory, "replacement");
        WriteRecord(paths, ValidRecordJson(selectedPath));
        var holds = new ConcurrentQueue<CountingDisposable>();
        var reader = new StubExecutableReader((path, _) =>
        {
            var hold = new CountingDisposable();
            holds.Enqueue(hold);
            return Task.FromResult(Executable(path, hold));
        });
        await using var service = CreateService(paths, reader);
        var lease = await service.AcquireAsync(CancellationToken.None);

        Assert.Equal(0, holds.Single().DisposeCalls);
        var selectionBusy = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync(replacementPath, CancellationToken.None));
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_BUSY", selectionBusy.Message);
        var acquireBusy = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcquireAsync(CancellationToken.None));
        Assert.Equal("BRIDGE_BUSY", acquireBusy.Message);
        Assert.Equal(1, reader.Calls);

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, holds.Single().DisposeCalls);

        var replacement = await service.SelectAsync(replacementPath, CancellationToken.None);
        Assert.Equal(replacementPath, replacement.ExecutablePath);
        Assert.Equal(2, reader.Calls);
        Assert.All(holds, hold => Assert.Equal(1, hold.DisposeCalls));
    }

    [Fact]
    public async Task AcquiredScopeOnlyGuardsSelectedPeerAndDoesNotResolveBeforeUse()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var selectedPath = PhotoshopPath(directory, "selected");
        WriteRecord(paths, ValidRecordJson(selectedPath));
        var hold = new CountingDisposable();
        var reader = new StubExecutableReader((path, _) => Task.FromResult(Executable(path, hold)));
        var resolver = new RecordingResolver
        {
            Identity = new(42, 638_944_128_000_000_000, PhotoshopPath(directory, "other"))
        };
        await using var service = CreateService(paths, reader, resolver);
        var lease = await service.AcquireAsync(CancellationToken.None);

        Assert.Equal(0, resolver.Calls);
        Assert.False(lease.Allows(LocalEndpoint, RemoteEndpoint));
        resolver.Identity = new(42, 638_944_128_000_000_000, selectedPath);
        Assert.True(lease.Allows(LocalEndpoint, RemoteEndpoint));
        Assert.Equal(2, resolver.Calls);

        lease.Dispose();
        Assert.False(lease.Allows(LocalEndpoint, RemoteEndpoint));
        Assert.Equal(2, resolver.Calls);
        Assert.Equal(1, hold.DisposeCalls);
    }

    [Fact]
    public async Task PreCancelledSelectionDoesNotReadExecutableOrWriteRecord()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var reader = MatchingReader();
        await using var service = CreateService(paths, reader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.SelectAsync(PhotoshopPath(directory, "cancelled"), cancellation.Token));

        Assert.Equal(0, reader.Calls);
        Assert.False(File.Exists(RecordPath(paths)));
        AssertNoTemporaryRecord(paths);
    }

    [Fact]
    public async Task DisposeCancelsInFlightReadAndQueuedAcquireWithoutLeakingReaderHold()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        WriteRecord(paths, ValidRecordJson(PhotoshopPath(directory, "selected")));
        var reader = new BlockingExecutableReader();
        var service = CreateService(paths, reader);
        Task? disposeTask = null;
        try
        {
            var activeRead = service.ReadAsync(CancellationToken.None);
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedAcquire = service.AcquireAsync(CancellationToken.None);
            var activeErrorTask = Record.ExceptionAsync(async () => await activeRead);
            var queuedErrorTask = Record.ExceptionAsync(async () => await queuedAcquire);

            disposeTask = service.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            var activeError = await activeErrorTask.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedError = await queuedErrorTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsAssignableFrom<OperationCanceledException>(activeError);
            Assert.True(queuedError is OperationCanceledException or ObjectDisposedException,
                $"Expected cancellation or disposal, got {queuedError?.GetType().Name ?? "no exception"}.");
            Assert.Equal(1, reader.Calls);
            Assert.Equal(1, reader.Hold.DisposeCalls);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await service.ReadAsync(CancellationToken.None));
        }
        finally
        {
            reader.Release();
            if (disposeTask is not null)
            {
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await service.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(@"\\server\share\Photoshop.exe")]
    [InlineData(@"\\?\C:\Adobe\Photoshop.exe")]
    [InlineData(@"C:\Adobe\Photoshop.exe:Zone.Identifier")]
    [InlineData(@"C:\Adobe\NotPhotoshop.exe")]
    [InlineData("Photoshop.exe")]
    public async Task WindowsReaderRejectsUnsafeOrNonPhotoshopPathsBeforeOpening(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("The selected Photoshop executable reader requires Windows paths.");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new WindowsSelectedPhotoshopExecutableReader().OpenAsync(path, CancellationToken.None));

        Assert.Equal("PHOTOSHOP_LOCAL_EXECUTABLE_REQUIRED", error.Message);
    }

    [Fact]
    public async Task WindowsReaderRejectsCurrentTesthostDisguisedAsPhotoshopAndReleasesLocks()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows executable metadata and directory locks require Windows.");
        }

        using var directory = new TestDirectory();
        var hostDirectory = Path.Combine(directory.Path, "not-photoshop");
        Directory.CreateDirectory(hostDirectory);
        var disguised = Path.Combine(hostDirectory, "Photoshop.exe");
        File.Copy(LocalNonPhotoshopExecutable(), disguised);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WindowsSelectedPhotoshopExecutableReader().OpenAsync(disguised, CancellationToken.None));

        Assert.Equal("PHOTOSHOP_HOST_UNSUPPORTED", error.Message);
        var renamed = Path.Combine(directory.Path, "reader-lock-released");
        Directory.Move(hostDirectory, renamed);
        Assert.True(File.Exists(Path.Combine(renamed, "Photoshop.exe")));
    }

    [Fact]
    public async Task WindowsReaderAndOwnedStorageRejectControlledJunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Reparse-point rejection requires Windows.");
        }

        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, "target");
        var link = Path.Combine(directory.Path, "link");
        Directory.CreateDirectory(target);
        File.Copy(LocalNonPhotoshopExecutable(), Path.Combine(target, "Photoshop.exe"));
        // A directory junction needs no symlink privilege; creation failure is not a passing
        // negative test. Both paths are fresh children of this test's owned directory.
        var start = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new WindowsSelectedPhotoshopExecutableReader().OpenAsync(
                    Path.Combine(link, "Photoshop.exe"), CancellationToken.None));

            Assert.Equal("PHOTOSHOP_HOST_PATH_INVALID", error.Message);
            Assert.Throws<InvalidDataException>(() =>
                SelectedPhotoshopDirectoryLocks.Open(Path.Combine(link, "record.json"), allowOwnedDirectoryMapping: true));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task SelectionPersistsInLocalAppDataWithMappedParentsAndKeepsDirectoryIdentity()
    {
        var testRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EcommerceDetailPage", "HostSelectionTests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        try
        {
            var paths = new LocalAppPathPolicy(testRoot);
            var selectedPath = Path.Combine(testRoot, "selected", "Photoshop.exe");
            await using (var service = CreateService(paths, MatchingReader()))
                Assert.Equal(PhotoshopHostSelectionStatus.Valid,
                    (await service.SelectAsync(selectedPath, CancellationToken.None)).Status);
            await using (var reopened = CreateService(paths, MatchingReader()))
                Assert.Equal(PhotoshopHostSelectionStatus.Valid,
                    (await reopened.ReadAsync(CancellationToken.None)).Status);

            var settings = Path.GetDirectoryName(RecordPath(paths))!;
            var renamed = Path.Combine(testRoot, "renamed-settings");
            using (SelectedPhotoshopDirectoryLocks.Open(RecordPath(paths), allowOwnedDirectoryMapping: true))
            {
                var error = Record.Exception(() => Directory.Move(settings, renamed));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.True(File.Exists(RecordPath(paths)));
                Assert.Empty(Directory.GetFiles(settings, "*.tmp"));
            }
            Directory.Move(settings, renamed);
            Assert.True(File.Exists(Path.Combine(renamed, "photoshop-host-v1.json")));
        }
        finally { Directory.Delete(testRoot, recursive: true); }
    }

    [Fact]
    public void SelectedDirectoryLocksBlockRenameUntilReleased()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows directory handle sharing semantics require Windows.");
        }

        using var directory = new TestDirectory();
        var hostDirectory = Path.Combine(directory.Path, "locked-host");
        Directory.CreateDirectory(hostDirectory);
        var executable = Path.Combine(hostDirectory, "Photoshop.exe");
        File.WriteAllBytes(executable, [1, 2, 3]);
        var renamed = Path.Combine(directory.Path, "renamed-host");

        using (SelectedPhotoshopDirectoryLocks.Open(executable))
        {
            var error = Record.Exception(() => Directory.Move(hostDirectory, renamed));
            Assert.True(error is IOException or UnauthorizedAccessException,
                "The selected host directory must not be renameable while its identity lease is held.");
            Assert.True(File.Exists(executable));
            Assert.False(Directory.Exists(renamed));
        }

        Directory.Move(hostDirectory, renamed);
        Assert.True(File.Exists(Path.Combine(renamed, "Photoshop.exe")));
    }

    private static PhotoshopHostSelectionService CreateService(
        IPathPolicy paths,
        ISelectedPhotoshopExecutableReader reader,
        IBridgePeerProcessResolver? resolver = null) =>
        new(paths, new FixedTimeProvider(SelectedAt), reader, resolver ?? new RecordingResolver());

    private static StubExecutableReader MatchingReader() =>
        new((path, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Executable(path));
        });

    private static SelectedPhotoshopExecutable Executable(
        string path,
        IDisposable? hold = null,
        string version = SupportedVersion,
        long length = ExecutableLength,
        string hash = ExecutableHash) =>
        new(Path.GetFullPath(path), version, length, hash, hold ?? NullDisposable.Instance);

    private static async Task<byte[]> SeedSelectionAsync(LocalAppPathPolicy paths, string path)
    {
        await using var service = CreateService(paths, MatchingReader());
        var snapshot = await service.SelectAsync(path, CancellationToken.None);
        Assert.Equal(PhotoshopHostSelectionStatus.Valid, snapshot.Status);
        return await File.ReadAllBytesAsync(RecordPath(paths));
    }

    private static string PhotoshopPath(TestDirectory directory, string name) =>
        Path.GetFullPath(Path.Combine(directory.Path, name, "Photoshop.exe"));

    private static string RecordPath(IPathPolicy paths) =>
        paths.ResolveOwnedPath(PhotoshopHostSelectionService.RecordRelativePath);

    private static string ValidRecordJson(
        string path,
        int recordVersion = 1,
        string numericVersion = SupportedVersion,
        long length = ExecutableLength,
        string hash = ExecutableHash) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = recordVersion,
            ["executable_path"] = Path.GetFullPath(path),
            ["numeric_file_version"] = numericVersion,
            ["length"] = length,
            ["sha256"] = hash,
            ["selected_at"] = SelectedAt
        });

    private static void WriteRecord(IPathPolicy paths, string json, int? totalBytes = null)
    {
        var path = RecordPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        if (totalBytes is null)
        {
            File.WriteAllBytes(path, jsonBytes);
            return;
        }

        if (jsonBytes.Length > totalBytes.Value)
        {
            throw new InvalidOperationException("Test record is larger than its requested padded length.");
        }

        var bytes = new byte[totalBytes.Value];
        Array.Fill(bytes, (byte)' ');
        jsonBytes.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }

    private static void AssertNoTemporaryRecord(IPathPolicy paths)
    {
        var directory = Path.GetDirectoryName(RecordPath(paths))!;
        if (Directory.Exists(directory))
        {
            Assert.Empty(Directory.EnumerateFiles(directory, ".photoshop-host-*.tmp", SearchOption.TopDirectoryOnly));
        }
    }

    private static string LocalNonPhotoshopExecutable()
    {
        var path = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
        Assert.False(string.Equals("Photoshop.exe", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        return path!;
    }

    private sealed class StubExecutableReader(
        Func<string, CancellationToken, Task<SelectedPhotoshopExecutable>> handler)
        : ISelectedPhotoshopExecutableReader
    {
        private int _calls;

        public Func<string, CancellationToken, Task<SelectedPhotoshopExecutable>> Handler { get; set; } = handler;

        public int Calls => Volatile.Read(ref _calls);

        public Task<SelectedPhotoshopExecutable> OpenAsync(string path, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Handler(path, token);
        }
    }

    private sealed class BlockingExecutableReader : ISelectedPhotoshopExecutableReader
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountingDisposable Hold { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public async Task<SelectedPhotoshopExecutable> OpenAsync(string path, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                return Executable(path, Hold);
            }
            catch
            {
                Hold.Dispose();
                throw;
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingResolver : IBridgePeerProcessResolver
    {
        private int _calls;

        public BridgePeerProcessIdentity? Identity { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public BridgePeerProcessIdentity? Resolve(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
        {
            Interlocked.Increment(ref _calls);
            return Identity;
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        private int _disposeCalls;

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
