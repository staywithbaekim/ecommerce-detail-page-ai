using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using Desktop.Domain.Inputs;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Application.Inputs;

public sealed class BatchInputService(
    IFileInventory fileInventory,
    IAssetMetadataReader metadataReader,
    IBatchRepository batches,
    IBatchScanCommitter scanCommitter,
    TimeProvider timeProvider,
    BatchScanOptions? options = null,
    ITemplateRepository? templates = null)
{
    private readonly BatchScanOptions _options = options ?? new BatchScanOptions();

    public async Task<BatchDraft> CreateDraftAsync(
        CreateBatchDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var draft = BatchDraft.Create(
            command.BatchId,
            command.InputRoot,
            command.OutputRoot,
            command.TemplateSnapshot,
            command.SelectionMode,
            command.IncludeEditablePsd,
            timeProvider.GetUtcNow(),
            command.OutputSelection);
        await batches.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        return draft;
    }

    public async Task<BatchDraft> CreateDraftFromTemplateAsync(
        CreateBatchFromTemplateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (templates is null)
        {
            throw new InvalidOperationException("INPUT_TEMPLATE_REPOSITORY_UNAVAILABLE");
        }

        var group = await templates.GetGroupAsync(command.TemplateGroupId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InputRuleViolationException("INPUT_TEMPLATE_GROUP_NOT_FOUND");
        if (group.Status != TemplateGroupStatus.Active || group.CurrentRevisionId is null)
        {
            throw new InputRuleViolationException("INPUT_TEMPLATE_GROUP_NOT_READY");
        }

        var revision = await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InputRuleViolationException("INPUT_TEMPLATE_REVISION_NOT_FOUND");
        var now = timeProvider.GetUtcNow();
        var existing = await batches.GetAsync(command.BatchId, cancellationToken).ConfigureAwait(false);
        if (existing?.ScanStatus == BatchScanStatus.Scanning)
        {
            throw new InputRuleViolationException("INPUT_DRAFT_CONFIGURATION_FROZEN");
        }

        if (existing?.ScanStatus == BatchScanStatus.ScanReady)
        {
            if (MatchesReadyConfiguration(existing, command, revision.RevisionId))
            {
                return existing;
            }

            throw new InputRuleViolationException("INPUT_DRAFT_CONFIGURATION_FROZEN");
        }

        var snapshotId = existing?.TemplateSnapshot.SnapshotId
            ?? InputStableIds.ForTemplateSnapshot(command.BatchId, revision.RevisionId, command.SelectedPageIds);
        IBatchTemplateContext snapshot = revision.RuleWorkspace?.Rules is not null
            ? BatchRuleTemplateContext.Create(snapshotId, command.BatchId, revision, command.SelectedPageIds,
                TemplateRuleCodec.BusinessDigest(revision), existing?.TemplateSnapshot.CreatedAt ?? now)
            : BatchTemplateSnapshot.Create(
            existing?.TemplateSnapshot.SnapshotId
                ?? InputStableIds.ForTemplateSnapshot(command.BatchId, revision.RevisionId, command.SelectedPageIds),
            command.BatchId,
            revision,
            command.SelectedPageIds,
            existing?.TemplateSnapshot.CreatedAt ?? now);
        if (existing is not null)
        {
            var reconfigured = existing.Reconfigure(
                command.InputRoot,
                command.OutputRoot,
                snapshot,
                command.SelectionMode,
                command.IncludeEditablePsd,
                now,
                command.OutputSelection);
            await batches.SaveAsync(reconfigured, cancellationToken).ConfigureAwait(false);
            return reconfigured;
        }

        return await CreateDraftAsync(
            new CreateBatchDraftCommand(
                command.BatchId,
                command.InputRoot,
                command.OutputRoot,
                snapshot,
                command.SelectionMode,
                command.IncludeEditablePsd,
                command.OutputSelection),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesReadyConfiguration(
        BatchDraft existing,
        CreateBatchFromTemplateCommand command,
        string revisionId)
    {
        var existingPages = existing.TemplateSnapshot.Pages
            .OrderBy(page => page.Order)
            .Select(page => page.PageId);
        var requestedPages = command.SelectedPageIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return string.Equals(existing.InputRoot, command.InputRoot, StringComparison.Ordinal)
            && string.Equals(existing.OutputRoot, command.OutputRoot, StringComparison.Ordinal)
            && existing.OutputSelection == command.OutputSelection
            && existing.SelectionMode == command.SelectionMode
            && existing.IncludeEditablePsd == command.IncludeEditablePsd
            && string.Equals(existing.TemplateSnapshot.RevisionId, revisionId, StringComparison.Ordinal)
            && existingPages.Order(StringComparer.Ordinal).SequenceEqual(requestedPages, StringComparer.Ordinal);
    }

    public async Task<string?> ResolveTemplateGroupIdAsync(
        BatchDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (templates is null)
        {
            return null;
        }

        var revision = await templates.GetRevisionAsync(draft.TemplateSnapshot.RevisionId, cancellationToken)
            .ConfigureAwait(false);
        return revision?.GroupId;
    }

    public Task<BatchDraft?> GetDraftAsync(
        string batchId,
        CancellationToken cancellationToken = default) =>
        batches.GetAsync(batchId, cancellationToken);

    public Task<IReadOnlyList<BatchDraft>> ListRecoverableAsync(
        CancellationToken cancellationToken = default) =>
        batches.ListRecoverableAsync(cancellationToken);

    public async Task<BatchDraft> ScanAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var draft = await batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InputRuleViolationException("INPUT_BATCH_NOT_FOUND");
        if (draft.ScanStatus == BatchScanStatus.ScanReady)
        {
            return draft;
        }

        if (draft.ScanStatus == BatchScanStatus.Scanning)
        {
            draft = draft.Block(ScanBlockReason.Interrupted(), timeProvider.GetUtcNow());
            await batches.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        }

        var scanning = draft.BeginScanning(timeProvider.GetUtcNow());
        try
        {
            await batches.SaveAsync(scanning, cancellationToken).ConfigureAwait(false);
            var inventoryResult = await fileInventory.ReadApprovedLevelsAsync(
                scanning.InputRoot,
                cancellationToken).ConfigureAwait(false);
            if (!inventoryResult.IsSuccess)
            {
                return await BlockAsync(
                    scanning,
                    inventoryResult.Failure ?? ScanBlockReason.SystemFailure(),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            var layout = InputLayoutDetector.Detect(inventoryResult.Inventory!);
            if (layout.IsBlocked)
            {
                return await BlockAsync(
                    scanning,
                    layout.BlockReason!,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            var work = layout.Styles
                .SelectMany((style, styleIndex) => style.Files.Select((file, fileIndex) =>
                    new MetadataWorkItem(style, styleIndex, file, fileIndex)))
                .ToArray();
            if (work.Length > 20_000)
            {
                return await BlockAsync(
                    scanning,
                    ScanBlockReason.TooManyAssets(),
                    layout.InputType,
                    cancellationToken).ConfigureAwait(false);
            }

            var metadata = await ReadMetadataAsync(scanning.InputRoot, work, cancellationToken)
                .ConfigureAwait(false);
            var firstFailure = metadata
                .Select((result, index) => (result, index))
                .FirstOrDefault(item => !item.result.IsSuccess);
            if (firstFailure.result is not null)
            {
                return await BlockAsync(
                    scanning,
                    firstFailure.result.Failure ?? ScanBlockReason.SystemFailure(),
                    layout.InputType,
                    cancellationToken).ConfigureAwait(false);
            }

            var (styles, assets) = BuildReadyInventory(scanning.BatchId, layout, work, metadata);
            var inventoryDigest = ComputeInventoryDigest(styles, assets);
            var manifest = AssetManifestV1.Create(
                scanning,
                layout.InputType!.Value,
                styles,
                assets,
                inventoryDigest,
                timeProvider.GetUtcNow());
            var committed = await scanCommitter.CommitAsync(
                new BatchScanCommitRequest(
                    scanning,
                    layout.InputType.Value,
                    styles,
                    assets,
                    manifest),
                cancellationToken).ConfigureAwait(false);
            if (committed.IsSuccess)
            {
                return committed.Draft!;
            }

            if (committed.RequiresRecovery)
            {
                // The commit journal is now the recovery source.  Do not
                // overwrite a possible ScanReady SQLite graph with a
                // ScanBlocked row while the durable evidence is being
                // reconciled.
                return scanning.Block(
                    committed.Failure ?? ScanBlockReason.PersistenceFailure(),
                    timeProvider.GetUtcNow(),
                    layout.InputType);
            }

            return await BlockAsync(
                scanning,
                committed.Failure ?? ScanBlockReason.SystemFailure(),
                layout.InputType,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await BlockAfterCancellationAsync(scanning).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableSystemFailure(exception))
        {
            if (exception.Message is "INPUT_PERSISTENCE_FAILURE"
                or "INPUT_MANIFEST_DATABASE_MISMATCH")
            {
                return scanning.Block(
                    exception.Message == "INPUT_MANIFEST_DATABASE_MISMATCH"
                        ? ScanBlockReason.ManifestDatabaseMismatch()
                        : ScanBlockReason.PersistenceFailure(),
                    timeProvider.GetUtcNow());
            }

            return await BlockBestEffortAsync(
                scanning,
                ScanBlockReason.SystemFailure(),
                null).ConfigureAwait(false);
        }
    }

    private async Task<AssetMetadataReadResult[]> ReadMetadataAsync(
        string inputRoot,
        IReadOnlyList<MetadataWorkItem> work,
        CancellationToken cancellationToken)
    {
        var results = new AssetMetadataReadResult[work.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, work.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _options.MaxConcurrentMetadataReads
            },
            async (index, token) =>
            {
                results[index] = await metadataReader.ReadAsync(
                    inputRoot,
                    work[index].File.RelativePath,
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return results;
    }

    private static (IReadOnlyList<StyleDraft> Styles, IReadOnlyList<SourceAssetMetadata> Assets)
        BuildReadyInventory(
            string batchId,
            InputLayoutDetection layout,
            IReadOnlyList<MetadataWorkItem> work,
            AssetMetadataReadResult[] metadata)
    {
        var assets = new SourceAssetMetadata[work.Count];
        var assetIdsByStyle = new Dictionary<int, List<string>>();
        for (var index = 0; index < work.Count; index++)
        {
            var item = work[index];
            var facts = metadata[index].Metadata!;
            var styleId = InputStableIds.ForStyle(batchId, item.Style.SourceDirectoryRelativePath);
            var assetId = InputStableIds.ForAsset(
                batchId,
                styleId,
                item.File.RelativePath,
                facts.Sha256);
            assets[index] = SourceAssetMetadata.CreateReadable(
                assetId,
                styleId,
                item.File.RelativePath,
                item.File.FileName,
                facts.Length,
                facts.Sha256,
                facts.PixelWidth,
                facts.PixelHeight,
                facts.Format,
                facts.MimeType);
            if (!assetIdsByStyle.TryGetValue(item.StyleIndex, out var styleAssetIds))
            {
                styleAssetIds = [];
                assetIdsByStyle.Add(item.StyleIndex, styleAssetIds);
            }

            styleAssetIds.Add(assetId);
        }

        var styles = layout.Styles.Select((style, index) => StyleDraft.Create(
            InputStableIds.ForStyle(batchId, style.SourceDirectoryRelativePath),
            style.DefaultStyleCode,
            style.SourceDirectoryRelativePath,
            index,
            assetIdsByStyle[index])).ToArray();
        return (styles, assets);
    }

    private static string ComputeInventoryDigest(
        IEnumerable<StyleDraft> styles,
        IEnumerable<SourceAssetMetadata> assets)
    {
        var builder = new StringBuilder("asset-inventory/v1\n");
        foreach (var style in styles.OrderBy(style => style.Order))
        {
            builder.Append(style.StyleId).Append('\n')
                .Append(style.SourceDirectoryRelativePath).Append('\n')
                .Append(style.DefaultStyleCode).Append('\n');
        }

        foreach (var asset in assets.OrderBy(asset => asset.SourceRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(asset.AssetId).Append('\n')
                .Append(asset.StyleId).Append('\n')
                .Append(asset.SourceRelativePath).Append('\n')
                .Append(asset.Length).Append('\n')
                .Append(asset.Sha256).Append('\n')
                .Append(asset.PixelWidth).Append('x').Append(asset.PixelHeight).Append('\n')
                .Append(asset.Format).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private async Task<BatchDraft> BlockAsync(
        BatchDraft scanning,
        ScanBlockReason reason,
        BatchInputType? detectedInputType,
        CancellationToken cancellationToken)
    {
        var blocked = scanning.Block(reason, timeProvider.GetUtcNow(), detectedInputType);
        await batches.SaveAsync(blocked, cancellationToken).ConfigureAwait(false);
        return blocked;
    }

    private Task<BatchDraft> BlockAfterCancellationAsync(BatchDraft scanning) =>
        BlockBestEffortAsync(scanning, ScanBlockReason.Cancelled(), null);

    private async Task<BatchDraft> BlockBestEffortAsync(
        BatchDraft scanning,
        ScanBlockReason reason,
        BatchInputType? detectedInputType)
    {
        var blocked = scanning.Block(reason, timeProvider.GetUtcNow(), detectedInputType);
        try
        {
            await batches.SaveAsync(blocked, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableSystemFailure(exception))
        {
            // The aggregate remains blocked in memory; the repository must roll back partial writes.
        }

        return blocked;
    }

    private static bool IsRecoverableSystemFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DbException;

    private sealed record MetadataWorkItem(
        DetectedStyleSource Style,
        int StyleIndex,
        InputFileReference File,
        int FileIndex);
}
