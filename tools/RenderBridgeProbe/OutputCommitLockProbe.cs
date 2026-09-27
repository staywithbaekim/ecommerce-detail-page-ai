using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Infrastructure.Files;

/// <summary>
/// Probe-only commit-store wrapper. After the selected page has a durable
/// Verified checkpoint, it holds that page's staged manifest open without
/// delete sharing. The production Directory.Move must therefore encounter a
/// real Windows sharing violation at the publication boundary.
/// </summary>
internal sealed class OutputCommitLockProbe : IRenderPageCommitStore, IDisposable
{
    private const string InvalidCode = "RENDER_OUTPUT_LOCK_PROBE_INVALID";
    private readonly IRenderPageCommitStore _inner;
    private readonly LocalAppPathPolicy _paths;
    private readonly string _batchId;
    private readonly string _productId;
    private readonly string _pageId;
    private readonly string _planDigest;
    private readonly string _manifestRelativePath;
    private readonly string _stagedManifestRelativePath;
    private FileStream? _manifestLock;
    private bool _disposed;

    internal OutputCommitLockProbe(IRenderPageCommitStore inner, string outputRoot, string targetPlanJson)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!Path.IsPathFullyQualified(outputRoot) || string.IsNullOrWhiteSpace(targetPlanJson)) throw Invalid();

        _inner = inner;
        _paths = new LocalAppPathPolicy(outputRoot);
        try
        {
            using var document = JsonDocument.Parse(targetPlanJson);
            var plan = document.RootElement;
            _batchId = Required(plan, "batch_id");
            _productId = Required(plan, "product_id");
            _pageId = Required(plan.GetProperty("document"), "page_id");
            _planDigest = Required(plan, "plan_digest");
            var artifact = Required(plan.GetProperty("output"), "artifact_set_id");
            if (_planDigest.Length != 71 || !_planDigest.StartsWith("sha256:", StringComparison.Ordinal)
                || _planDigest.Skip(7).Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();

            var outputDirectory = Path.Combine("rendered", Component(_batchId));
            _manifestRelativePath = Normalize(Path.Combine(outputDirectory, artifact, "manifest.json"));
            _stagedManifestRelativePath = Normalize(Path.Combine(outputDirectory, ".app-staging",
                Component(_batchId), Component(_productId + "/" + _pageId), _planDigest[7..], "page", "manifest.json"));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Invalid(error);
        }
        AppDomain.CurrentDomain.FirstChanceException += ObservePublicationFailure;
    }

    internal bool WasArmed { get; private set; }
    internal bool CompletionReached { get; private set; }
    internal bool IsLockHeld => _manifestLock is not null;
    internal string StagedManifestRelativePath => _stagedManifestRelativePath;
    internal int? PublicationFailureHResult { get; private set; }
    internal RenderPageCommitCheckpoint? TargetCheckpoint { get; private set; }

    private void ObservePublicationFailure(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_manifestLock is not null && e.Exception is IOException or UnauthorizedAccessException
            && e.Exception.StackTrace?.Contains("PreparedRenderPage.CommitAsync", StringComparison.Ordinal) == true
            && e.Exception.StackTrace.Contains("MoveDirectory", StringComparison.Ordinal))
            PublicationFailureHResult = e.Exception.HResult;
    }

    public async Task PrepareRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _inner.PrepareRenderCommitAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        if (!Matches(checkpoint)) return;
        if (_manifestLock is not null || WasArmed) throw Invalid();

        var path = _paths.ResolveOwnedPath(_stagedManifestRelativePath);
        FileStream? stream = null;
        try
        {
            // FileShare.Read remains compatible with the committer's verification
            // leases, but intentionally denies the delete/rename access required
            // to move the containing page directory on Windows.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != checkpoint.ManifestByteLength || stream.Length is < 1 or > 1_048_576) throw Invalid();
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, checkpoint.ManifestSha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            stream.Position = 0;
            _manifestLock = stream;
            stream = null;
            WasArmed = true;
            TargetCheckpoint = checkpoint;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw Invalid(error);
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task CompleteRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Matches(checkpoint)) CompletionReached = true;
        await _inner.CompleteRenderCommitAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task<OutputCommitState?> ReadRenderCommitAsync(
        RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _inner.ReadRenderCommitAsync(checkpoint, cancellationToken);
    }

    internal void Release()
    {
        _manifestLock?.Dispose();
        _manifestLock = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        AppDomain.CurrentDomain.FirstChanceException -= ObservePublicationFailure;
        Release();
        _disposed = true;
    }

    private bool Matches(RenderPageCommitCheckpoint checkpoint) =>
        checkpoint.Page.TaskId == _batchId
        && checkpoint.Page.StyleId == _productId
        && checkpoint.Page.PageId == _pageId
        && checkpoint.Page.PlanDigest == _planDigest
        && Normalize(checkpoint.ManifestRelativePath) == _manifestRelativePath;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string Required(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property).GetString();
        return string.IsNullOrWhiteSpace(value) ? throw Invalid() : value;
    }

    private static string Component(string value) => "id-"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static InvalidDataException Invalid(Exception? inner = null) => new(InvalidCode, inner);
}
