using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Inputs;

public sealed class ReadOnlyFileInventory(SqliteDesktopStore? outputOwnership = null) : IFileInventory
{
    public async Task<FileInventoryReadResult> ReadApprovedLevelsAsync(
        string inputRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var exclusions = outputOwnership is null ? null
                : await OwnedOutputScanExclusions.OpenAsync(outputOwnership, inputRoot, cancellationToken).ConfigureAwait(false);
            return FileInventoryReadResult.Success(InputInventoryReader.ReadApprovedLevels(inputRoot, exclusions?.Paths));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            return FileInventoryReadResult.Blocked(MissingDirectory());
        }
        catch (FileNotFoundException)
        {
            return FileInventoryReadResult.Blocked(MissingDirectory());
        }
        catch (UnauthorizedAccessException)
        {
            return FileInventoryReadResult.Blocked(PathRejected());
        }
        catch (IOException)
        {
            return FileInventoryReadResult.Blocked(PathRejected());
        }
        catch (Exception error) when (error is InvalidDataException or System.Data.Common.DbException or InvalidOperationException)
        {
            // Missing/corrupt ownership is not permission to silently skip user input or import results.
            return FileInventoryReadResult.Blocked(ScanBlockReason.Create("INPUT_OUTPUT_OWNERSHIP_UNAVAILABLE",
                ScanBlockCategory.PathAccess, "无法核验已有输出目录的归属，请先恢复任务记录后重新扫描。"));
        }
    }

    private static ScanBlockReason MissingDirectory() => ScanBlockReason.Create(
        "INPUT_DIRECTORY_DISAPPEARED",
        ScanBlockCategory.PathAccess,
        "所选目录已不存在或暂时不可用。请重新连接存储设备或重新选择目录。");

    private static ScanBlockReason PathRejected() => ScanBlockReason.Create(
        "INPUT_DIRECTORY_ACCESS_DENIED",
        ScanBlockCategory.PathAccess,
        "无法安全读取所选目录。请检查访问权限，并选择不包含链接或受保护位置的目录。");
}

/// <summary>Scan exclusion is based on durable local facts and pinned identities, never a name or marker.</summary>
internal sealed class OwnedOutputScanExclusions : IDisposable
{
    private readonly List<RenderOutputDirectoryLease> _leases = [];
    internal HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<OwnedOutputScanExclusions> OpenAsync(SqliteDesktopStore store, string inputRoot, CancellationToken token)
    {
        var root = InputPathGuard.ValidateRoot(inputRoot);
        var result = new OwnedOutputScanExclusions();
        try
        {
            foreach (var batchId in await store.ListRenderOutputRegistrationIdsAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var registration = await store.LoadRenderOutputRegistrationAsync(batchId, token).ConfigureAwait(false)
                    ?? throw RenderStyleOutputRegistrationData.Invalid();
                foreach (var style in registration.Styles)
                {
                    var target = style.Target;
                    foreach (var path in new[] { target.StagingDirectory, target.FinalDirectory })
                    {
                        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) continue;
                        var stage = await store.LoadRenderOutputStageAsync(batchId, target.StyleId, token).ConfigureAwait(false);
                        if (stage is null) continue;
                        if (path == target.FinalDirectory)
                        {
                            var publication = await store.LoadRenderOutputPublicationAsync(batchId, target.StyleId, token).ConfigureAwait(false);
                            if (publication is null || publication.State == RenderStyleOutputPublicationData.Copying) continue;
                            if (publication.StageIdentity != stage.Identity || publication.FinalDirectory != path
                                || publication.RegistrationSha256 != RenderStyleOutputRegistrationData.Hash(RenderStyleOutputRegistrationData.Encode(registration)))
                                throw RenderStyleOutputPublicationData.Invalid();
                        }
                        result.TryPin(path, stage.Identity, style);
                    }
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void TryPin(string path, RenderOutputDirectoryIdentity identity, RegisteredRenderStyleOutput style)
    {
        var pins = new List<RenderOutputDirectoryLease>();
        try
        {
            var source = RenderOutputDirectoryLease.Open(style.Target.SourceDirectory); pins.Add(source);
            var parent = RenderOutputDirectoryLease.Open(style.Target.ParentDirectory); pins.Add(parent);
            var candidate = RenderOutputDirectoryLease.Open(path); pins.Add(candidate);
            if (source.Identity != style.SourceIdentity || parent.Identity != style.ParentIdentity || candidate.Identity != identity) return;
            Paths.Add(path); _leases.AddRange(pins); pins.Clear();
        }
        catch (RenderOutputTargetException error) when (error.Code == "RENDER_OUTPUT_DIRECTORY_UNAVAILABLE") { }
        // Reparse/access failures are not silently hidden. The inventory returns a path-access block.
        finally { foreach (var pin in pins) pin.Dispose(); }
    }
    public void Dispose() { foreach (var lease in _leases) lease.Dispose(); }
}
