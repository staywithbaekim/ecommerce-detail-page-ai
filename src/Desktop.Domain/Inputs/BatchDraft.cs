using System.Collections.Immutable;
using Desktop.Domain.Templates;

namespace Desktop.Domain.Inputs;

public sealed record BatchDraft
{
    private BatchDraft(
        string batchId,
        BatchScanStatus scanStatus,
        BatchInputType? inputType,
        string inputRoot,
        string outputRoot,
        IBatchTemplateContext templateSnapshot,
        BatchSelectionMode selectionMode,
        bool includeEditablePsd,
        ImmutableArray<StyleDraft> styles,
        ImmutableArray<SourceAssetMetadata> assets,
        string? manifestReference,
        string? manifestDigest,
        ScanBlockReason? blockReason,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        BatchOutputSelection? outputSelection = null)
    {
        BatchId = batchId;
        ScanStatus = scanStatus;
        InputType = inputType;
        InputRoot = inputRoot;
        OutputRoot = outputRoot;
        TemplateSnapshot = templateSnapshot;
        SelectionMode = selectionMode;
        IncludeEditablePsd = includeEditablePsd;
        Styles = styles;
        Assets = assets;
        ManifestReference = manifestReference;
        ManifestDigest = manifestDigest;
        BlockReason = blockReason;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        OutputSelection = outputSelection;
    }

    public string BatchId { get; }

    public BatchScanStatus ScanStatus { get; }

    public BatchInputType? InputType { get; }

    public string InputRoot { get; }

    public string OutputRoot { get; }

    public BatchOutputSelection? OutputSelection { get; }

    public IBatchTemplateContext TemplateSnapshot { get; }

    public BatchSelectionMode SelectionMode { get; }

    public bool IncludeEditablePsd { get; }

    public ImmutableArray<StyleDraft> Styles { get; }

    public ImmutableArray<SourceAssetMetadata> Assets { get; }

    public string? ManifestReference { get; }

    public string? ManifestDigest { get; }

    public ScanBlockReason? BlockReason { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public static BatchDraft Create(
        string batchId,
        string inputRoot,
        string outputRoot,
        IBatchTemplateContext templateSnapshot,
        BatchSelectionMode selectionMode,
        bool includeEditablePsd,
        DateTimeOffset createdAt,
        BatchOutputSelection? outputSelection = null)
    {
        ArgumentNullException.ThrowIfNull(templateSnapshot);
        var validBatchId = InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID");
        if (!string.Equals(templateSnapshot.BatchId, validBatchId, StringComparison.Ordinal))
        {
            throw new InputRuleViolationException("INPUT_TEMPLATE_SNAPSHOT_BATCH_MISMATCH");
        }

        return new BatchDraft(
            validBatchId,
            BatchScanStatus.Draft,
            null,
            InputValueRules.RequiredText(inputRoot, 32_767, "INPUT_ROOT_INVALID"),
            InputValueRules.RequiredText(outputRoot, 32_767, "INPUT_OUTPUT_ROOT_INVALID"),
            templateSnapshot,
            selectionMode,
            includeEditablePsd,
            [],
            [],
            null,
            null,
            null,
            createdAt,
            createdAt,
            outputSelection);
    }

    public static BatchDraft Restore(
        string batchId,
        BatchScanStatus scanStatus,
        BatchInputType? inputType,
        string inputRoot,
        string outputRoot,
        IBatchTemplateContext templateSnapshot,
        BatchSelectionMode selectionMode,
        bool includeEditablePsd,
        IEnumerable<StyleDraft> styles,
        IEnumerable<SourceAssetMetadata> assets,
        string? manifestReference,
        string? manifestDigest,
        ScanBlockReason? blockReason,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        BatchOutputSelection? outputSelection = null)
    {
        var draft = new BatchDraft(
            InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID"),
            scanStatus,
            inputType,
            InputValueRules.RequiredText(inputRoot, 32_767, "INPUT_ROOT_INVALID"),
            InputValueRules.RequiredText(outputRoot, 32_767, "INPUT_OUTPUT_ROOT_INVALID"),
            templateSnapshot,
            selectionMode,
            includeEditablePsd,
            styles.ToImmutableArray(),
            assets.ToImmutableArray(),
            manifestReference,
            manifestDigest,
            blockReason,
            createdAt,
            updatedAt,
            outputSelection);
        draft.ValidateState();
        return draft;
    }

    public BatchDraft BeginScanning(DateTimeOffset updatedAt)
    {
        if (ScanStatus is not (BatchScanStatus.Draft or BatchScanStatus.ScanBlocked))
        {
            throw new InputRuleViolationException("INPUT_SCAN_TRANSITION_INVALID");
        }

        return new BatchDraft(
            BatchId,
            BatchScanStatus.Scanning,
            null,
            InputRoot,
            OutputRoot,
            TemplateSnapshot,
            SelectionMode,
            IncludeEditablePsd,
            [],
            [],
            null,
            null,
            null,
            CreatedAt,
            EnsureTimestamp(updatedAt),
            OutputSelection);
    }

    public BatchDraft Reconfigure(
        string inputRoot,
        string outputRoot,
        IBatchTemplateContext templateSnapshot,
        BatchSelectionMode selectionMode,
        bool includeEditablePsd,
        DateTimeOffset updatedAt,
        BatchOutputSelection? outputSelection = null)
    {
        ArgumentNullException.ThrowIfNull(templateSnapshot);
        if (ScanStatus is not (BatchScanStatus.Draft or BatchScanStatus.ScanBlocked))
        {
            throw new InputRuleViolationException("INPUT_DRAFT_CONFIGURATION_FROZEN");
        }

        if (!string.Equals(templateSnapshot.BatchId, BatchId, StringComparison.Ordinal))
        {
            throw new InputRuleViolationException("INPUT_TEMPLATE_SNAPSHOT_BATCH_MISMATCH");
        }

        return new BatchDraft(
            BatchId,
            BatchScanStatus.Draft,
            null,
            InputValueRules.RequiredText(inputRoot, 32_767, "INPUT_ROOT_INVALID"),
            InputValueRules.RequiredText(outputRoot, 32_767, "INPUT_OUTPUT_ROOT_INVALID"),
            templateSnapshot,
            selectionMode,
            includeEditablePsd,
            [],
            [],
            null,
            null,
            null,
            CreatedAt,
            EnsureTimestamp(updatedAt),
            outputSelection);
    }

    public BatchDraft Block(
        ScanBlockReason reason,
        DateTimeOffset updatedAt,
        BatchInputType? detectedInputType = null)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (ScanStatus != BatchScanStatus.Scanning)
        {
            throw new InputRuleViolationException("INPUT_SCAN_TRANSITION_INVALID");
        }

        return new BatchDraft(
            BatchId,
            BatchScanStatus.ScanBlocked,
            detectedInputType,
            InputRoot,
            OutputRoot,
            TemplateSnapshot,
            SelectionMode,
            IncludeEditablePsd,
            [],
            [],
            null,
            null,
            reason,
            CreatedAt,
            EnsureTimestamp(updatedAt),
            OutputSelection);
    }

    public BatchDraft MarkScanReady(
        BatchInputType inputType,
        IEnumerable<StyleDraft> styles,
        IEnumerable<SourceAssetMetadata> assets,
        string manifestReference,
        string manifestDigest,
        DateTimeOffset updatedAt)
    {
        if (ScanStatus != BatchScanStatus.Scanning)
        {
            throw new InputRuleViolationException("INPUT_SCAN_TRANSITION_INVALID");
        }

        var ready = new BatchDraft(
            BatchId,
            BatchScanStatus.ScanReady,
            inputType,
            InputRoot,
            OutputRoot,
            TemplateSnapshot,
            SelectionMode,
            IncludeEditablePsd,
            styles.ToImmutableArray(),
            assets.ToImmutableArray(),
            InputValueRules.OwnedRelativePath(manifestReference),
            InputValueRules.ManifestDigest(manifestDigest),
            null,
            CreatedAt,
            EnsureTimestamp(updatedAt),
            OutputSelection);
        ready.ValidateState();
        return ready;
    }

    private void ValidateState()
    {
        if (!string.Equals(TemplateSnapshot.BatchId, BatchId, StringComparison.Ordinal)
            || UpdatedAt < CreatedAt)
        {
            throw new InputRuleViolationException("INPUT_BATCH_DRAFT_INVALID");
        }

        if (ScanStatus == BatchScanStatus.ScanReady)
        {
            ValidateReadyState();
            return;
        }

        if (!Styles.IsDefaultOrEmpty
            || !Assets.IsDefaultOrEmpty
            || ManifestReference is not null
            || ManifestDigest is not null
            || (ScanStatus == BatchScanStatus.ScanBlocked) != (BlockReason is not null))
        {
            throw new InputRuleViolationException("INPUT_PARTIAL_SCAN_STATE_FORBIDDEN");
        }

        if (ScanStatus is BatchScanStatus.Draft or BatchScanStatus.Scanning && InputType is not null)
        {
            throw new InputRuleViolationException("INPUT_BATCH_DRAFT_INVALID");
        }
    }

    private void ValidateReadyState()
    {
        if (InputType is null
            || Styles.IsDefaultOrEmpty
            || Assets.IsDefaultOrEmpty
            || ManifestReference is null
            || ManifestDigest is null
            || BlockReason is not null
            || Styles.Select(style => style.StyleId).Distinct(StringComparer.Ordinal).Count() != Styles.Length
            || Styles.Select(style => style.Order).Distinct().Count() != Styles.Length
            || Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count() != Assets.Length)
        {
            throw new InputRuleViolationException("INPUT_READY_EVIDENCE_INCOMPLETE");
        }

        var assetsById = Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var referencedAssets = Styles.SelectMany(style => style.AssetIds).ToArray();
        if (referencedAssets.Distinct(StringComparer.Ordinal).Count() != Assets.Length
            || referencedAssets.Any(assetId => !assetsById.TryGetValue(assetId, out var asset)
                || Styles.All(style => !string.Equals(style.StyleId, asset.StyleId, StringComparison.Ordinal)
                    || !style.AssetIds.Contains(assetId, StringComparer.Ordinal))))
        {
            throw new InputRuleViolationException("INPUT_STYLE_ASSET_RELATION_INVALID");
        }

        if (InputType == BatchInputType.SingleStyle
            && (Styles.Length != 1
                || !string.IsNullOrEmpty(Styles[0].SourceDirectoryRelativePath)))
        {
            throw new InputRuleViolationException("INPUT_SINGLE_STYLE_LAYOUT_INVALID");
        }

        if (InputType == BatchInputType.StyleDirectoryBatch
            && Styles.Any(style => string.IsNullOrEmpty(style.SourceDirectoryRelativePath)))
        {
            throw new InputRuleViolationException("INPUT_STYLE_DIRECTORY_LAYOUT_INVALID");
        }
    }

    private DateTimeOffset EnsureTimestamp(DateTimeOffset updatedAt)
    {
        if (updatedAt < CreatedAt)
        {
            throw new InputRuleViolationException("INPUT_BATCH_TIMESTAMP_INVALID");
        }

        return updatedAt;
    }
}
