using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Diagnostics;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Local preference and per-owner file identity lease; never launches or authenticates Photoshop.</summary>
public sealed class PhotoshopHostSelectionService : IPhotoshopHostSelectionPort, IAsyncDisposable
{
    internal const string RecordRelativePath = "settings/photoshop-host-v1.json";
    internal const int MaximumRecordBytes = 16 * 1024;
    private static readonly string[] RecordFields = ["version", "executable_path", "numeric_file_version", "length", "sha256", "selected_at"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 4
    };
    private readonly IPathPolicy _paths;
    private readonly TimeProvider _time;
    private readonly ISelectedPhotoshopExecutableReader _reader;
    private readonly IBridgePeerProcessResolver _peers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _leased;
    private int _disposed;

    public PhotoshopHostSelectionService(IPathPolicy paths, TimeProvider timeProvider, IBridgePeerProcessResolver peers)
        : this(paths, timeProvider, new WindowsSelectedPhotoshopExecutableReader(), peers) { }

    internal PhotoshopHostSelectionService(IPathPolicy paths, TimeProvider timeProvider,
        ISelectedPhotoshopExecutableReader reader, IBridgePeerProcessResolver peers)
    { _paths = paths; _time = timeProvider; _reader = reader; _peers = peers; }

    public Task<PhotoshopHostSelectionSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            SelectionRecord? record;
            try { record = await ReadRecordAsync(token).ConfigureAwait(false); }
            catch (InvalidDataException) { return new(PhotoshopHostSelectionStatus.InvalidRecord); }
            catch (Exception error) when (IsFileFailure(error)) { return new(PhotoshopHostSelectionStatus.Unavailable); }
            if (record is null) return new(PhotoshopHostSelectionStatus.NotSelected);
            try
            {
                using var executable = await _reader.OpenAsync(record.ExecutablePath, token).ConfigureAwait(false);
                return Snapshot(record, Matches(record, executable) ? PhotoshopHostSelectionStatus.Valid : PhotoshopHostSelectionStatus.Changed);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { return Snapshot(record, PhotoshopHostSelectionStatus.Missing); }
            catch (InvalidOperationException error) when (error.Message is "PHOTOSHOP_HOST_UNSUPPORTED" or "PHOTOSHOP_HOST_PATH_INVALID")
            { return Snapshot(record, PhotoshopHostSelectionStatus.Changed); }
            catch (Exception error) when (IsFileFailure(error))
            { return Snapshot(record, PhotoshopHostSelectionStatus.Unavailable); }
        }, cancellationToken);

    public Task<PhotoshopHostSelectionSnapshot> SelectAsync(string executablePath, CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            if (Volatile.Read(ref _leased) != 0) throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_BUSY");
            var path = Normalize(executablePath);
            try
            {
                using var executable = await _reader.OpenAsync(path, token).ConfigureAwait(false);
                ValidateExecutable(path, executable);
                var record = new SelectionRecord(1, path, executable.NumericFileVersion, executable.Length,
                    executable.Sha256, _time.GetUtcNow());
                await SaveRecordAsync(record, token).ConfigureAwait(false);
                return Snapshot(record, PhotoshopHostSelectionStatus.Valid);
            }
            catch (InvalidOperationException error) when (error.Message is "PHOTOSHOP_HOST_UNSUPPORTED" or "PHOTOSHOP_HOST_PATH_INVALID") { throw; }
            catch (Exception error) when (IsFileFailure(error))
            { throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_UNAVAILABLE"); }
        }, cancellationToken);

    internal Task<PhotoshopSelectedHostLease> AcquireAsync(CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            if (Volatile.Read(ref _leased) != 0) throw new InvalidOperationException("BRIDGE_BUSY");
            SelectionRecord? record;
            try { record = await ReadRecordAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (IsFileFailure(error))
            { throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_INVALID"); }
            if (record is null) throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_REQUIRED");
            SelectedPhotoshopExecutable? executable = null;
            try
            {
                executable = await _reader.OpenAsync(record.ExecutablePath, token).ConfigureAwait(false);
                ValidateExecutable(record.ExecutablePath, executable);
                if (!Matches(record, executable)) throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_INVALID");
                token.ThrowIfCancellationRequested();
                var guard = new BridgeSelectedHostPeerGuard(record.ExecutablePath, _peers);
                Volatile.Write(ref _leased, 1);
                var lease = new PhotoshopSelectedHostLease(guard, executable, () => Volatile.Write(ref _leased, 0));
                executable = null; // transferred until the owner's complete disposal, not just startup
                return lease;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (IsFileFailure(error))
            { throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_INVALID"); }
            finally { executable?.Dispose(); }
        }, cancellationToken);

    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Hashing and native file metadata must never run on the WPF dispatcher.
        return Task.Run(async () =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                return await work(linked.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }, cancellationToken);
    }

    private PhotoshopHostSelectionSnapshot Snapshot(SelectionRecord record, PhotoshopHostSelectionStatus status) =>
        new(status, record.ExecutablePath, record.NumericFileVersion, record.SelectedAt, Volatile.Read(ref _leased) != 0);

    private async Task<SelectionRecord?> ReadRecordAsync(CancellationToken token)
    {
        var path = _paths.ResolveOwnedPath(RecordRelativePath);
        BridgeOutputReservation.ValidatePath(path);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        await using (stream.ConfigureAwait(false))
        {
            BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path);
            BridgeOutputReservation.ValidatePath(path);
            if (stream.Length is < 2 or > MaximumRecordBytes) throw new InvalidDataException();
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Length != RecordFields.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                    || names.Any(name => !RecordFields.Contains(name, StringComparer.Ordinal))) throw new InvalidDataException();
                var record = JsonSerializer.Deserialize<SelectionRecord>(bytes, JsonOptions) ?? throw new InvalidDataException();
                if (record.Version != 1 || record.Length <= 0 || !IsHash(record.Sha256)
                    || record.SelectedAt == default || !IsSupportedVersion(record.NumericFileVersion)
                    || !string.Equals(Normalize(record.ExecutablePath), record.ExecutablePath, StringComparison.Ordinal))
                    throw new InvalidDataException();
                return record;
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("PHOTOSHOP_HOST_SELECTION_INVALID"); }
        }
    }

    private async Task SaveRecordAsync(SelectionRecord record, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException();
        var path = _paths.ResolveOwnedPath(RecordRelativePath);
        BridgeOutputReservation.ValidatePath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var parents = SelectedPhotoshopDirectoryLocks.Open(path, allowOwnedDirectoryMapping: true);
        BridgeOutputReservation.ValidatePath(path);
        var temporary = Path.Combine(directory, $".photoshop-host-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, temporary);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            BridgeOutputReservation.ValidatePath(path);
            // Publish only the application-owned preference, atomically on the same volume.
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Normalize(string path)
    {
        try { return WindowsPhotoshopEnvironmentProbe.NormalizeExecutablePath(path); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        { throw new InvalidOperationException("PHOTOSHOP_HOST_PATH_INVALID"); }
    }

    private static void ValidateExecutable(string path, SelectedPhotoshopExecutable executable)
    {
        if (!string.Equals(path, executable.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || executable.Length <= 0 || !IsHash(executable.Sha256) || !IsSupportedVersion(executable.NumericFileVersion))
            throw new InvalidOperationException("PHOTOSHOP_HOST_UNSUPPORTED");
    }

    private static bool Matches(SelectionRecord record, SelectedPhotoshopExecutable executable) =>
        string.Equals(record.ExecutablePath, executable.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && record.NumericFileVersion == executable.NumericFileVersion && record.Length == executable.Length
        && string.Equals(record.Sha256, executable.Sha256, StringComparison.OrdinalIgnoreCase);
    private static bool IsSupportedVersion(string version) => Version.TryParse(version, out var value)
        && value.Major == 26 && value.Minor == 8 && value.Build == 1;
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsFileFailure(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
        _lifetime.Dispose();
        // Active file handles still belong to the owner and are released only when its listener is gone.
    }

    private sealed record SelectionRecord(int Version, string ExecutablePath, string NumericFileVersion,
        long Length, string Sha256, DateTimeOffset SelectedAt);
}

internal sealed class PhotoshopSelectedHostLease(IBridgePeerGuard guard, IDisposable executable, Action release)
    : IBridgePeerGuard, IDisposable
{
    private int _disposed;
    public bool Allows(System.Net.IPEndPoint localEndpoint, System.Net.IPEndPoint remoteEndpoint) =>
        Volatile.Read(ref _disposed) == 0 && guard.Allows(localEndpoint, remoteEndpoint);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { executable.Dispose(); }
        finally { release(); }
    }
}
