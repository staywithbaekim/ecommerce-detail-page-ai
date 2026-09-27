using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Desktop.Application.Diagnostics;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Diagnostics;

/// <summary>
/// Rebuilds a small support package from fixed technical fields. The original log,
/// arbitrary strings, nested objects, paths and request references are never copied.
/// </summary>
public sealed class LocalDiagnosticPackageExporter(
    IPathPolicy paths,
    IPhotoshopEnvironmentProbe? photoshopEnvironmentProbe = null,
    IPhotoshopConnectionCheckPort? photoshopConnectionCheck = null) : IDiagnosticPackageExporter
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxLineBytes = 32 * 1024;
    private const int MaxRecords = 2000;
    private const int MaxPhotoshopInstallations = 16;
    private const long MaxAuthenticatedHeartbeats = 10_000;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    public Task<DiagnosticPackageResult> ExportAsync(string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() => ExportCoreAsync(destinationPath, cancellationToken), cancellationToken);

    private async Task<DiagnosticPackageResult> ExportCoreAsync(string destinationPath, CancellationToken token)
    {
        var destination = ValidateDestination(destinationPath);
        token.ThrowIfCancellationRequested();
        var logs = await ReadLogAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var photoshopEnvironment = ProjectPhotoshopEnvironment(photoshopEnvironmentProbe?.Latest);
        var photoshopConnection = ProjectPhotoshopConnection(photoshopConnectionCheck?.Latest);
        var result = logs.Result with
        {
            PhotoshopEnvironmentStatus = photoshopEnvironment.Status,
            PhotoshopConnectionStatus = photoshopConnection.Status
        };
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".diagnostic-{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ownsTemporary = true;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = new
                    {
                        schema = "desktop-diagnostics/v1",
                        generated_at = DateTimeOffset.UtcNow,
                        software_version = (Assembly.GetEntryAssembly() ?? typeof(LocalDiagnosticPackageExporter).Assembly)
                            .GetName().Version?.ToString() ?? "unknown",
#if DEBUG
                        build_configuration = "Debug",
#else
                        build_configuration = "Release",
#endif
                        os_version = Environment.OSVersion.Version.ToString(),
                        runtime_version = Environment.Version.ToString(),
                        photoshop_version = photoshopConnection.AuthenticatedBuild,
                        photoshop_version_status = photoshopConnection.AuthenticatedBuild is null
                            ? "not_collected" : "authenticated_build_match",
                        photoshop_environment = photoshopEnvironment.ManifestValue,
                        photoshop_connection = photoshopConnection.ManifestValue,
                        settings = new { ai_service = "VolcArk", automatic_diagnostic_upload = false },
                        log_status = result.LogStatus,
                        exported_records = result.ExportedRecords,
                        omitted_records = result.OmittedRecords,
                        truncated = result.Truncated,
                        maximum_source_bytes = MaxBytes,
                        maximum_records = MaxRecords,
                        scope = "allowlisted_desktop_technical_events_only"
                    };
                    await WriteEntryAsync(archive, "manifest.json", JsonSerializer.Serialize(manifest, OutputOptions), token).ConfigureAwait(false);
                    await WriteEntryAsync(archive, "technical-events.jsonl", string.Join('\n', logs.Records), token).ConfigureAwait(false);
                    await WriteEntryAsync(archive, "README.txt",
                        "本包由用户主动在本机导出，软件不会自动上传。\n"
                        + "仅含版本、固定运行/环境元数据和白名单技术事件；不是原始日志副本。\n"
                        + "不含Key/配对码、异常正文、路径/请求标识、数据库、PSD、商品原图、市场尺码图、正式输出或缩略图。\n"
                        + "最多读取固定日志尾部2 MiB，最多导出2000条；截断、遗漏和日志不可读会在manifest中说明。\n"
                        + "manifest中的photoshop_version仅在历史认证连接严格匹配批准完整构建且状态可信时出现；authenticated_build_match只证明该检查时间点的构建匹配。\n"
                        + $"photoshop_environment为not_collected时表示尚无检测快照；有快照时最多含{MaxPhotoshopInstallations}条检测时间点的历史可执行文件元数据，不含路径或版本显示文本，也不证明认证宿主完整构建、能力或连接。\n"
                        + "photoshop_connection为not_collected时表示尚无认证连接检查历史；historical_check_rejected表示历史值未通过固定白名单且未采信构建。历史结果不会触发连接，不代表持续连接、正式宿主选择或逐机渲染/ICC能力已验收。\n"
                        + "技术支持：请联系软件交付方；如需业务图片，请在本包之外另行明确选择提供。\n", token).ConfigureAwait(false);
                }
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ValidateDestination(destination);
            File.Move(temporary, destination, overwrite: false);
            ownsTemporary = false;
            return result;
        }
        finally
        {
            if (ownsTemporary) File.Delete(temporary);
        }
    }

    private string ValidateDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path[2..].Contains(':') || path.Any(character => char.IsControl(character)
                || character is '"' or '<' or '>' or '|' or '?' or '*'))
            throw new InvalidDataException("DIAGNOSTIC_DESTINATION_INVALID");
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase)
            || path[3..].Split('\\').Any(segment => segment.Length == 0 || segment.EndsWith(' ') || segment.EndsWith('.'))
            || paths.IsOwnedPath(full))
            throw new InvalidDataException("DIAGNOSTIC_DESTINATION_INVALID");
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("DIAGNOSTIC_DESTINATION_EXISTS");
        var directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("DIAGNOSTIC_DIRECTORY_UNAVAILABLE");
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("DIAGNOSTIC_DESTINATION_NOT_LOCAL");
        RejectReparsePoints(directory);
        return full;
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("DIAGNOSTIC_REPARSE_PATH_REJECTED");
    }

    private async Task<LogSnapshot> ReadLogAsync(CancellationToken token)
    {
        byte[] bytes;
        bool truncated;
        try
        {
            var path = paths.ResolveOwnedPath(Path.Combine("logs", "desktop-v1.jsonl"));
            RejectReparsePoints(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            truncated = length > MaxBytes;
            stream.Position = Math.Max(0, length - MaxBytes);
            bytes = new byte[(int)Math.Min(length, MaxBytes)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return LogSnapshot.Empty("missing"); }
        catch (DirectoryNotFoundException) { return LogSnapshot.Empty("missing"); }
        catch (IOException) { return LogSnapshot.Empty("unavailable"); }
        catch (UnauthorizedAccessException) { return LogSnapshot.Empty("unavailable"); }

        var records = new Queue<string>();
        var omitted = 0;
        // A tail may start in the middle of a UTF-8 character or JSON record.
        var start = truncated ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
        if (truncated && start == 0) return new([], new(0, 1, true, "read"));
        for (var cursor = start; cursor < bytes.Length; cursor++)
        {
            token.ThrowIfCancellationRequested();
            if (bytes[cursor] != (byte)'\n') continue;
            var count = cursor - start;
            if (count > 0)
            {
                var safe = count <= MaxLineBytes ? ProjectRecord(bytes.AsMemory(start, count)) : null;
                if (safe is null) omitted++;
                else
                {
                    if (records.Count == MaxRecords) { records.Dequeue(); omitted++; truncated = true; }
                    records.Enqueue(safe);
                }
            }
            start = cursor + 1;
        }
        if (start < bytes.Length) omitted++; // In-flight/torn final line is not evidence.
        return new(records.ToArray(), new(records.Count, omitted, truncated, "read"));
    }

    private static string? ProjectRecord(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event_code", out var code)
                || code.ValueKind != JsonValueKind.String || !IsEventCode(code.GetString())) return null;
            var record = new Dictionary<string, object?> { ["event_code"] = code.GetString() };
            if (root.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String
                && timestamp.TryGetDateTimeOffset(out var time)) record["timestamp"] = time.ToUniversalTime();
            if (root.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String
                && level.GetString() is "information" or "error") record["level"] = level.GetString();
            if (root.TryGetProperty("error_code", out var error) && error.ValueKind == JsonValueKind.String)
                record["error_code"] = SafeErrorCode(error.GetString());
            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var safe = new Dictionary<string, object?>();
                foreach (var property in properties.EnumerateObject())
                {
                    var value = property.Value;
                    if (property.Name is "elapsed_ms" or "payload_bytes" or "asset_count" or "requirement_count"
                        or "timeout_ms" or "upload_timeout_ms" or "upload_idle_timeout_ms" or "response_headers_timeout_ms"
                        or "response_body_timeout_ms" or "upload_bytes_written" or "upload_elapsed_ms" or "upload_idle_ms"
                        or "http_status_code" or "response_bytes" or "exception_status_code")
                    {
                        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                            && number is >= 0 and <= 1_000_000_000_000) safe[property.Name] = number;
                    }
                    else if (property.Name == "hresult" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var hresult))
                        safe[property.Name] = hresult;
                    else if (property.Name is "upload_completed" or "response_headers_received" or "response_body_completed" or "late"
                        && value.ValueKind is JsonValueKind.True or JsonValueKind.False) safe[property.Name] = value.GetBoolean();
                    else if (property.Name == "phase" && value.ValueKind == JsonValueKind.String && IsPhase(value.GetString()))
                        safe[property.Name] = value.GetString();
                }
                record["properties"] = safe;
            }
            return JsonSerializer.Serialize(record);
        }
        catch (JsonException) { return null; }
    }

    private static bool IsEventCode(string? value) => value is "AI_TRANSPORT_PHASE" or "AI_TRANSPORT_FAILURE"
        or "DESKTOP_STARTUP_FAILED" or "DESKTOP_SAFE_EXIT_RECORD_FAILED" or "DESKTOP_SERVICE_DISPOSE_FAILED"
        or "DESKTOP_SINGLE_INSTANCE_DISPOSE_FAILED";

    private static bool IsPhase(string? value) => value is "payload_ready" or "send_started" or "upload_progress"
        or "upload_completed" or "response_headers_received" or "response_body_started" or "response_body_completed"
        or "late_response_headers_received" or "late_response_body_completed" or "late_transport_failure"
        or "reading_response_body" or "waiting_response_headers" or "submitting_request" or "launch_options"
        or "single_instance" or "service_registration" or "store_initialization" or "instance_record"
        or "main_window_creation" or "main_window_show" or "safe_exit_record" or "service_dispose" or "single_instance_dispose";

    private static string SafeErrorCode(string? value) => value is "AI_TRANSPORT_LATE_FAILURE"
        or "AI_PROVIDER_RESPONSE_BODY_TIMEOUT" or "AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT" or "AI_PROVIDER_SUBMISSION_TIMEOUT"
        or "AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE" or "AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE"
        or "AI_PROVIDER_SUBMISSION_NETWORK_FAILURE" or "AI_PROVIDER_REQUEST_TOO_LARGE" or "AI_REQUEST_CANCELLED"
        or "AI_PROVIDER_RESPONSE_TOO_LARGE" or "IOException" or "InvalidDataException" or "UnauthorizedAccessException"
        or "InvalidOperationException" or "SqliteException" or "OperationCanceledException" or "ObjectDisposedException"
             ? value : "UNRECOGNIZED_CODE";

    private static PhotoshopConnectionProjection ProjectPhotoshopConnection(PhotoshopConnectionCheckResult? check)
    {
        if (check is null)
            return new(new { status = "not_collected" }, DiagnosticPhotoshopConnectionStatus.NotCollected, null);

        var checkStatus = ProjectConnectionCheckStatus(check.Status);
        if (checkStatus is null || check.AuthenticatedHeartbeats is < 0 or > MaxAuthenticatedHeartbeats)
            return RejectedPhotoshopConnection(check.ObservedAt);

        var authenticatedStatus = check.Status is PhotoshopConnectionCheckStatus.Ready
            or PhotoshopConnectionCheckStatus.HostNotReady;
        var authenticatedBuildMatch = authenticatedStatus
            && check.AuthenticatedHeartbeats >= 3
            && string.Equals(check.MatchedPhotoshopBuild, BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
                StringComparison.Ordinal);
        if (authenticatedStatus && !authenticatedBuildMatch)
            return RejectedPhotoshopConnection(check.ObservedAt);

        var matchedBuild = authenticatedBuildMatch ? BridgeSessionOwnerFactory.SupportedPhotoshopBuild : null;
        return new(new
        {
            status = "historical_check",
            observed_at = check.ObservedAt.ToUniversalTime(),
            check_status = checkStatus,
            authenticated_heartbeats = check.AuthenticatedHeartbeats,
            matched_photoshop_build = matchedBuild
        }, authenticatedBuildMatch
            ? DiagnosticPhotoshopConnectionStatus.HistoricalAuthenticatedBuildMatch
            : DiagnosticPhotoshopConnectionStatus.HistoricalCheck, matchedBuild);
    }

    private static PhotoshopConnectionProjection RejectedPhotoshopConnection(DateTimeOffset observedAt) =>
        new(new
        {
            status = "historical_check_rejected",
            observed_at = observedAt.ToUniversalTime()
        }, DiagnosticPhotoshopConnectionStatus.HistoricalCheck, null);

    private static string? ProjectConnectionCheckStatus(PhotoshopConnectionCheckStatus status) => status switch
    {
        PhotoshopConnectionCheckStatus.Ready => "ready",
        PhotoshopConnectionCheckStatus.PairingRequired => "pairing_required",
        PhotoshopConnectionCheckStatus.Busy => "busy",
        PhotoshopConnectionCheckStatus.TimedOut => "timed_out",
        PhotoshopConnectionCheckStatus.HostNotReady => "host_not_ready",
        PhotoshopConnectionCheckStatus.Failed => "failed",
        PhotoshopConnectionCheckStatus.HostSelectionRequired => "host_selection_required",
        PhotoshopConnectionCheckStatus.HostSelectionInvalid => "host_selection_invalid",
        _ => null
    };

    private static PhotoshopEnvironmentProjection ProjectPhotoshopEnvironment(PhotoshopEnvironmentSnapshot? snapshot)
    {
        if (snapshot is null)
            return new(new { status = "not_collected" }, DiagnosticPhotoshopEnvironmentStatus.NotCollected);

        var source = snapshot.Installations ?? Array.Empty<PhotoshopInstallation>();
        var count = Math.Min(source.Count, MaxPhotoshopInstallations);
        var installations = new List<object>(count);
        for (var index = 0; index < count; index++)
        {
            var installation = source[index];
            if (installation is null) continue;
            var numericVersion = NormalizeNumericFileVersion(installation.NumericFileVersion);
            if (numericVersion is null) continue;
            installations.Add(new
            {
                numeric_file_version = numericVersion,
                is_running = installation.IsRunning,
                source_flags = ProjectSourceFlags(installation.Source)
            });
        }

        return new(new
        {
            status = "historical_executable_metadata",
            observed_at = snapshot.ObservedAt.ToUniversalTime(),
            os_candidate = snapshot.IsOsCandidate,
            installations,
            truncated = snapshot.Truncated || source.Count > MaxPhotoshopInstallations
        }, DiagnosticPhotoshopEnvironmentStatus.HistoricalExecutableMetadata);
    }

    private static string? NormalizeNumericFileVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return null;
        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var normalized = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part.Length > 10 || part.Any(character => !char.IsAsciiDigit(character))
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out normalized[index]))
                return null;
        }
        return string.Join(".", normalized);
    }

    private static string[] ProjectSourceFlags(PhotoshopDiscoverySource source)
    {
        var flags = new List<string>(4);
        if ((source & PhotoshopDiscoverySource.RunningProcess) != 0) flags.Add("running_process");
        if ((source & PhotoshopDiscoverySource.Registry) != 0) flags.Add("registry");
        if ((source & PhotoshopDiscoverySource.StandardDirectory) != 0) flags.Add("standard_directory");
        if ((source & PhotoshopDiscoverySource.UserSelectedFile) != 0) flags.Add("user_selected_file");
        return flags.ToArray();
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), token).ConfigureAwait(false);
    }

    private sealed record LogSnapshot(string[] Records, DiagnosticPackageResult Result)
    {
        public static LogSnapshot Empty(string status) => new([], new(0, 0, false, status));
    }

    private sealed record PhotoshopEnvironmentProjection(
        object ManifestValue,
        DiagnosticPhotoshopEnvironmentStatus Status);

    private sealed record PhotoshopConnectionProjection(
        object ManifestValue,
        DiagnosticPhotoshopConnectionStatus Status,
        string? AuthenticatedBuild);
}
