using System.IO.Compression;
using System.Text.Json;
using Desktop.Application.Diagnostics;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class DiagnosticPackageExporterTests
{
    private static readonly string[] ExpectedEntries = ["README.txt", "manifest.json", "technical-events.jsonl"];
    private static readonly string[] RunningProcessSourceFlags = ["running_process"];
    private static readonly string[] RegisteredDirectorySourceFlags = ["registry", "standard_directory"];

    [Fact]
    public async Task ExportRebuildsOnlyKnownFieldsWithoutCredentialsPathsImagesOrArbitraryJson()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        const string secret = "PRIVATE_key_customer_product_456789";
        var log = Path.Combine(root.Path, "logs", "desktop-v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var raw = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Parse("2026-09-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            level = "error",
            event_code = "AI_TRANSPORT_FAILURE",
            error_code = "AI_PROVIDER_RESPONSE_BODY_TIMEOUT",
            api_key = secret,
            properties = new
            {
                phase = "reading_response_body", elapsed_ms = 321L, late = false,
                request_id = secret, exception = secret, path = @"C:\customer\private.psd",
                token = new { nested = secret }, payload_bytes = "not_a_number_" + secret
            }
        }) + "\n";
        raw += JsonSerializer.Serialize(new { event_code = "DESKTOP_STARTUP_FAILED", error_code = secret,
            properties = new { phase = secret, elapsed_ms = new { secret }, hresult = -42 } }) + "\n";
        raw += JsonSerializer.Serialize(new { event_code = secret, properties = new { elapsed_ms = 12 } }) + "\n";
        raw += "{broken json}\n{\"event_code\":\"AI_TRANSPORT_PHASE\"}"; // Torn tail has no newline.
        await File.WriteAllTextAsync(log, raw);
        var sentinel = Path.Combine(root.Path, "source.psd");
        await File.WriteAllTextAsync(sentinel, secret);
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path).ExportAsync(destination, CancellationToken.None);

        Assert.Equal(2, result.ExportedRecords);
        Assert.Equal(3, result.OmittedRecords);
        Assert.Equal("read", result.LogStatus);
        Assert.False(result.Truncated);
        Assert.Equal(DiagnosticPhotoshopConnectionStatus.NotCollected, result.PhotoshopConnectionStatus);
        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal(ExpectedEntries, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        var all = string.Join('\n', zip.Entries.Select(Read));
        Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", all, StringComparison.Ordinal);
        Assert.DoesNotContain("request_id", all, StringComparison.Ordinal);
        Assert.Contains("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", all, StringComparison.Ordinal);
        Assert.Contains("UNRECOGNIZED_CODE", all, StringComparison.Ordinal);
        using var record = JsonDocument.Parse(Read(zip.GetEntry("technical-events.jsonl")!).Split('\n')[0]);
        Assert.Equal(321, record.RootElement.GetProperty("properties").GetProperty("elapsed_ms").GetInt64());
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_version_status").GetString());
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_environment").GetProperty("status").GetString());
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_connection").GetProperty("status").GetString());
        Assert.False(manifest.RootElement.GetProperty("settings").GetProperty("automatic_diagnostic_upload").GetBoolean());
        Assert.Equal(raw, await File.ReadAllTextAsync(log));
        Assert.Equal(secret, await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.GetFiles(output.Path));
    }

    [Fact]
    public async Task MissingPhotoshopSnapshotIsExplicitAndExportNeverTriggersDetection()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var probe = new Probe(null);

        var result = await Exporter(root.Path, probe)
            .ExportAsync(Path.Combine(output.Path, "support.zip"), CancellationToken.None);

        Assert.Equal(0, probe.DetectCalls);
        Assert.Equal(DiagnosticPhotoshopEnvironmentStatus.NotCollected, result.PhotoshopEnvironmentStatus);
        using var zip = ZipFile.OpenRead(Path.Combine(output.Path, "support.zip"));
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        var environment = manifest.RootElement.GetProperty("photoshop_environment");
        Assert.Equal("not_collected", environment.GetProperty("status").GetString());
        Assert.False(environment.TryGetProperty("observed_at", out _));
        Assert.Contains("尚无检测快照", Read(zip.GetEntry("README.txt")!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColdStartConnectionHistoryIsExplicitAndReadingLatestNeverStartsCheck()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var connection = new ConnectionCheck(null);
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, connectionCheck: connection)
            .ExportAsync(destination, CancellationToken.None);

        Assert.Equal(1, connection.LatestReads);
        Assert.Equal(0, connection.CheckCalls);
        Assert.Equal(DiagnosticPhotoshopConnectionStatus.NotCollected, result.PhotoshopConnectionStatus);
        using var zip = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("not_collected", history.GetProperty("status").GetString());
        Assert.False(history.TryGetProperty("observed_at", out _));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_version_status").GetString());
        Assert.Contains("尚无认证连接检查历史", Read(zip.GetEntry("README.txt")!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhotoshopConnectionCheckStatus.Ready, "ready")]
    [InlineData(PhotoshopConnectionCheckStatus.HostNotReady, "host_not_ready")]
    public async Task AuthenticatedHistoryProjectsOnlyApprovedBuildAtItsObservationTime(
        PhotoshopConnectionCheckStatus status,
        string expectedStatus)
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var observedAt = DateTimeOffset.Parse("2026-09-24T15:12:30+08:00", System.Globalization.CultureInfo.InvariantCulture);
        var connection = new ConnectionCheck(new(
            status,
            observedAt,
            3,
            BridgeSessionOwnerFactory.SupportedPhotoshopBuild));
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, connectionCheck: connection)
            .ExportAsync(destination, CancellationToken.None);

        Assert.Equal(0, connection.CheckCalls);
        Assert.Equal(DiagnosticPhotoshopConnectionStatus.HistoricalAuthenticatedBuildMatch,
            result.PhotoshopConnectionStatus);
        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal(ExpectedEntries, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
            manifest.RootElement.GetProperty("photoshop_version").GetString());
        Assert.Equal("authenticated_build_match",
            manifest.RootElement.GetProperty("photoshop_version_status").GetString());
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check", history.GetProperty("status").GetString());
        Assert.Equal(expectedStatus, history.GetProperty("check_status").GetString());
        Assert.Equal(observedAt.ToUniversalTime(), history.GetProperty("observed_at").GetDateTimeOffset());
        Assert.Equal(3, history.GetProperty("authenticated_heartbeats").GetInt64());
        Assert.Equal(BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
            history.GetProperty("matched_photoshop_build").GetString());
    }

    [Theory]
    [InlineData(PhotoshopConnectionCheckStatus.Failed, "failed")]
    [InlineData(PhotoshopConnectionCheckStatus.HostSelectionRequired, "host_selection_required")]
    [InlineData(PhotoshopConnectionCheckStatus.HostSelectionInvalid, "host_selection_invalid")]
    public async Task FailedHistoryUsesFixedStatusAndCannotClaimAuthenticatedBuild(PhotoshopConnectionCheckStatus status, string expectedStatus)
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var connection = new ConnectionCheck(new(
            status,
            DateTimeOffset.Parse("2026-09-24T07:20:00Z", System.Globalization.CultureInfo.InvariantCulture),
            0));
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, connectionCheck: connection)
            .ExportAsync(destination, CancellationToken.None);

        Assert.Equal(0, connection.CheckCalls);
        Assert.Equal(DiagnosticPhotoshopConnectionStatus.HistoricalCheck, result.PhotoshopConnectionStatus);
        using var zip = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_version_status").GetString());
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check", history.GetProperty("status").GetString());
        Assert.Equal(expectedStatus, history.GetProperty("check_status").GetString());
        Assert.Equal(0, history.GetProperty("authenticated_heartbeats").GetInt64());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("matched_photoshop_build").ValueKind);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    public async Task TimedOutHistoryPreservesOnlyBoundedHeartbeatCountWithoutBuild(long heartbeats)
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var connection = new ConnectionCheck(new(
            PhotoshopConnectionCheckStatus.TimedOut,
            DateTimeOffset.Parse("2026-09-24T07:25:00Z", System.Globalization.CultureInfo.InvariantCulture),
            heartbeats));
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, connectionCheck: connection)
            .ExportAsync(destination, CancellationToken.None);

        Assert.Equal(DiagnosticPhotoshopConnectionStatus.HistoricalCheck, result.PhotoshopConnectionStatus);
        using var zip = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check", history.GetProperty("status").GetString());
        Assert.Equal("timed_out", history.GetProperty("check_status").GetString());
        Assert.Equal(heartbeats, history.GetProperty("authenticated_heartbeats").GetInt64());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("matched_photoshop_build").ValueKind);
        Assert.Equal(0, connection.CheckCalls);
    }

    [Fact]
    public async Task ArbitraryBuildStringIsRejectedWithoutLeakingOrForgingMatchedBuild()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        const string secret = @"PRIVATE_build_C:\customer\source.psd";
        var connection = new ConnectionCheck(new(
            PhotoshopConnectionCheckStatus.Ready,
            DateTimeOffset.Parse("2026-09-24T07:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            3,
            BridgeSessionOwnerFactory.SupportedPhotoshopBuild + secret));
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, connectionCheck: connection)
            .ExportAsync(destination, CancellationToken.None);

        Assert.Equal(DiagnosticPhotoshopConnectionStatus.HistoricalCheck, result.PhotoshopConnectionStatus);
        Assert.Equal(0, connection.CheckCalls);
        using var zip = ZipFile.OpenRead(destination);
        var all = string.Join('\n', zip.Entries.Select(Read));
        Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check_rejected", history.GetProperty("status").GetString());
        Assert.False(history.TryGetProperty("matched_photoshop_build", out _));
        Assert.False(history.TryGetProperty("authenticated_heartbeats", out _));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2L)]
    [InlineData(10_001L)]
    [InlineData(long.MaxValue)]
    public async Task InvalidOrInsufficientReadyHeartbeatCountsFailClosedWithoutMatchedBuild(long heartbeats)
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var connection = new ConnectionCheck(new(
            PhotoshopConnectionCheckStatus.Ready,
            DateTimeOffset.Parse("2026-09-24T07:40:00Z", System.Globalization.CultureInfo.InvariantCulture),
            heartbeats,
            BridgeSessionOwnerFactory.SupportedPhotoshopBuild));
        var destination = Path.Combine(output.Path, "support.zip");

        await Exporter(root.Path, connectionCheck: connection).ExportAsync(destination, CancellationToken.None);

        using var zip = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check_rejected", history.GetProperty("status").GetString());
        Assert.False(history.TryGetProperty("authenticated_heartbeats", out _));
        Assert.False(history.TryGetProperty("matched_photoshop_build", out _));
        Assert.Equal(0, connection.CheckCalls);
    }

    [Fact]
    public async Task UnsupportedConnectionEnumFailsClosedWithoutConnectionFields()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var connection = new ConnectionCheck(new(
            (PhotoshopConnectionCheckStatus)12345,
            DateTimeOffset.Parse("2026-09-24T07:50:00Z", System.Globalization.CultureInfo.InvariantCulture),
            3,
            BridgeSessionOwnerFactory.SupportedPhotoshopBuild));
        var destination = Path.Combine(output.Path, "support.zip");

        await Exporter(root.Path, connectionCheck: connection).ExportAsync(destination, CancellationToken.None);

        using var zip = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        var history = manifest.RootElement.GetProperty("photoshop_connection");
        Assert.Equal("historical_check_rejected", history.GetProperty("status").GetString());
        Assert.False(history.TryGetProperty("check_status", out _));
        Assert.False(history.TryGetProperty("authenticated_heartbeats", out _));
        Assert.False(history.TryGetProperty("matched_photoshop_build", out _));
        Assert.Equal(0, connection.CheckCalls);
    }

    [Fact]
    public async Task PhotoshopSnapshotExportsOnlyNormalizedMetadataWithoutPathsDisplayTextOrUnknownSources()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        const string secret = "PRIVATE_photoshop_path_and_display_456789";
        var observedAt = DateTimeOffset.Parse("2026-09-24T14:30:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = new PhotoshopEnvironmentSnapshot(
            observedAt,
            $"10.0.26100-{secret}",
            secret,
            true,
            [
                new($@"D:\{secret}\Photoshop.exe", $"26.8.1-{secret}", secret,
                    (PhotoshopDiscoverySource)0x4000, true),
                new($@"E:\{secret}\Photoshop.exe", "026.008.0001", secret,
                    PhotoshopDiscoverySource.RunningProcess | (PhotoshopDiscoverySource)0x4000, true)
            ],
            0,
            false);
        var probe = new Probe(snapshot);
        var destination = Path.Combine(output.Path, "support.zip");

        var result = await Exporter(root.Path, probe).ExportAsync(destination, CancellationToken.None);

        Assert.Equal(0, probe.DetectCalls);
        Assert.Equal(DiagnosticPhotoshopEnvironmentStatus.HistoricalExecutableMetadata, result.PhotoshopEnvironmentStatus);
        using var zip = ZipFile.OpenRead(destination);
        var all = string.Join('\n', zip.Entries.Select(Read));
        Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutablePath", all, StringComparison.Ordinal);
        Assert.DoesNotContain("FileVersionDisplay", all, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("photoshop_version").ValueKind);
        Assert.Equal("not_collected", manifest.RootElement.GetProperty("photoshop_version_status").GetString());
        var environment = manifest.RootElement.GetProperty("photoshop_environment");
        Assert.Equal("historical_executable_metadata", environment.GetProperty("status").GetString());
        Assert.Equal(observedAt.ToUniversalTime(), environment.GetProperty("observed_at").GetDateTimeOffset());
        Assert.False(environment.GetProperty("os_candidate").GetBoolean());
        var installations = environment.GetProperty("installations").EnumerateArray().ToArray();
        Assert.Single(installations);
        Assert.Equal("26.8.1", installations[0].GetProperty("numeric_file_version").GetString());
        Assert.True(installations[0].GetProperty("is_running").GetBoolean());
        Assert.Equal(RunningProcessSourceFlags, installations[0].GetProperty("source_flags")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task PhotoshopSnapshotKeepsMultipleNumericVersionsWithinIndependentExportLimit()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var installations = Enumerable.Range(0, 24)
            .Select(index => new PhotoshopInstallation(
                $@"D:\private\Photoshop-{index}.exe",
                $"{20 + index}.8.1",
                $"display-private-{index}",
                PhotoshopDiscoverySource.Registry | PhotoshopDiscoverySource.StandardDirectory,
                index % 2 == 0))
            .ToArray();
        var probe = new Probe(new PhotoshopEnvironmentSnapshot(
            DateTimeOffset.Parse("2026-09-24T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "10.0.26100",
            "X64",
            true,
            installations,
            0,
            false));
        var destination = Path.Combine(output.Path, "support.zip");

        await Exporter(root.Path, probe).ExportAsync(destination, CancellationToken.None);

        Assert.Equal(0, probe.DetectCalls);
        using var zip = ZipFile.OpenRead(destination);
        var all = string.Join('\n', zip.Entries.Select(Read));
        Assert.DoesNotContain(@"D:\private", all, StringComparison.Ordinal);
        Assert.DoesNotContain("display-private", all, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        var environment = manifest.RootElement.GetProperty("photoshop_environment");
        var exported = environment.GetProperty("installations").EnumerateArray().ToArray();
        Assert.Equal(16, exported.Length);
        Assert.Equal("20.8.1", exported[0].GetProperty("numeric_file_version").GetString());
        Assert.Equal("35.8.1", exported[^1].GetProperty("numeric_file_version").GetString());
        Assert.True(environment.GetProperty("truncated").GetBoolean());
        Assert.All(exported, item => Assert.Equal(
            RegisteredDirectorySourceFlags,
            item.GetProperty("source_flags").EnumerateArray().Select(value => value.GetString()!).ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrLockedLogIsExplicitAndDoesNotPreventMetadataPackage(bool locked)
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var log = Path.Combine(root.Path, "logs", "desktop-v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using var held = locked ? new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.None) : null;
        var result = await Exporter(root.Path).ExportAsync(Path.Combine(output.Path, "support.zip"), CancellationToken.None);
        Assert.Equal(locked ? "unavailable" : "missing", result.LogStatus);
        Assert.Equal(0, result.ExportedRecords);
    }

    [Fact]
    public async Task LargeLogTailLongLinesAndRecordCountAreBounded()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var log = Path.Combine(root.Path, "logs", "desktop-v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var raw = new string('x', 2 * 1024 * 1024 + 20) + "\n"
            + new string('y', 33 * 1024) + "\n"
            + string.Concat(Enumerable.Range(0, 2100).Select(index =>
                $"{{\"event_code\":\"AI_TRANSPORT_PHASE\",\"properties\":{{\"elapsed_ms\":{index}}}}}\n"));
        await File.WriteAllTextAsync(log, raw);
        var destination = Path.Combine(output.Path, "bounded.zip");
        var result = await Exporter(root.Path).ExportAsync(destination, CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.Equal(2000, result.ExportedRecords);
        Assert.Equal(101, result.OmittedRecords);
        using var zip = ZipFile.OpenRead(destination);
        var lines = Read(zip.GetEntry("technical-events.jsonl")!).Split('\n');
        Assert.Equal(2000, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(100, first.RootElement.GetProperty("properties").GetProperty("elapsed_ms").GetInt32());
    }

    [Fact]
    public async Task ExistingDestinationAndConcurrentCollisionNeverOverwriteOrLeaveTemporaryFiles()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        var exporter = Exporter(root.Path);
        var destination = Path.Combine(output.Path, "support.zip");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await exporter.ExportAsync(destination, CancellationToken.None); return true; }
            catch (IOException) { return false; }
        }));
        Assert.Single(outcomes, success => success);
        var before = await File.ReadAllBytesAsync(destination);
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(destination, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(destination));
        Assert.Single(Directory.GetFiles(output.Path));
    }

    [Fact]
    public async Task CancellationBeforeExportWritesNothing()
    {
        using var root = new TestDirectory();
        using var output = new TestDirectory();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Exporter(root.Path)
            .ExportAsync(Path.Combine(output.Path, "support.zip"), cancelled.Token));
        Assert.Empty(Directory.GetFiles(output.Path));
    }

    [Theory]
    [InlineData("relative.zip")]
    [InlineData(@"\\server\share\diagnostic.zip")]
    [InlineData(@"C:\temp\diagnostic.zip:stream")]
    [InlineData(@"C:\temp.\diagnostic.zip")]
    [InlineData(@"C:\temp\diagnostic.psd")]
    public async Task InvalidPathsFailWithoutExport(string path)
    {
        using var root = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => Exporter(root.Path).ExportAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ApplicationDataDestinationIsRejectedEvenForNewZip()
    {
        using var root = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => Exporter(root.Path)
            .ExportAsync(Path.Combine(root.Path, "support.zip"), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    private static LocalDiagnosticPackageExporter Exporter(
        string root,
        IPhotoshopEnvironmentProbe? probe = null,
        IPhotoshopConnectionCheckPort? connectionCheck = null) =>
        new(new LocalAppPathPolicy(root), probe, connectionCheck);

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class Probe(PhotoshopEnvironmentSnapshot? snapshot) : IPhotoshopEnvironmentProbe
    {
        public int DetectCalls { get; private set; }
        public PhotoshopEnvironmentSnapshot? Latest { get; } = snapshot;

        public Task<PhotoshopEnvironmentSnapshot> DetectAsync(
            string? additionalExecutablePath,
            CancellationToken cancellationToken)
        {
            DetectCalls++;
            throw new InvalidOperationException("Detection must not run during diagnostic export.");
        }
    }

    private sealed class ConnectionCheck(PhotoshopConnectionCheckResult? latest) : IPhotoshopConnectionCheckPort
    {
        public int LatestReads { get; private set; }
        public int CheckCalls { get; private set; }
        public PhotoshopConnectionCheckResult? Latest
        {
            get
            {
                LatestReads++;
                return latest;
            }
        }

        public Task<PhotoshopConnectionCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            throw new InvalidOperationException("Connection check must not run during diagnostic export.");
        }
    }
}
