using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Inputs;

/// <summary>
/// Durable input-commit evidence stored beside the asset manifest.  The
/// journal contains only the metadata needed to rebuild the SQLite batch
/// graph; it never copies source image or PSD bytes.
/// </summary>
public sealed class JsonInputCommitJournalStore(
    IFileStore files,
    IPathPolicy pathPolicy) : IInputCommitJournalStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(
        InputCommitJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var envelope = ToEnvelope(journal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        await files.WriteAtomicallyAsync(
            PathFor(journal.BatchId),
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<InputCommitJournal?> GetAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        ValidateId(batchId);
        var bytes = await files.ReadAsync(PathFor(batchId), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(bytes, Options)
                ?? throw new InvalidDataException("INPUT_COMMIT_JOURNAL_CORRUPT");
            return FromEnvelope(envelope, batchId);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw new InvalidDataException("INPUT_COMMIT_JOURNAL_CORRUPT", exception);
        }
    }

    public Task<IReadOnlyList<string>> ListBatchIdsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batchesRoot = pathPolicy.ResolveOwnedPath("batches");
        if (!Directory.Exists(batchesRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = new List<string>();
        foreach (var batchDirectory in Directory.EnumerateDirectories(batchesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchId = Path.GetFileName(batchDirectory);
            if (!IsValidId(batchId))
            {
                continue;
            }

            var journalPath = Path.Combine(
                batchDirectory,
                "asset-manifest",
                "commit.journal.json");
            if (File.Exists(journalPath))
            {
                ids.Add(batchId);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(
            ids.Order(StringComparer.Ordinal).ToArray());
    }

    private static JournalEnvelope ToEnvelope(InputCommitJournal journal)
    {
        if (!string.Equals(journal.SchemaVersion, InputCommitJournalProtocol.Version, StringComparison.Ordinal)
            || !string.Equals(journal.BatchId, journal.ReadyDraft.BatchId, StringComparison.Ordinal)
            || !string.Equals(journal.BatchId, journal.StagedManifest.BatchId, StringComparison.Ordinal)
            || journal.ReadyDraft.ScanStatus != BatchScanStatus.ScanReady
            || string.IsNullOrWhiteSpace(journal.StagedManifest.ManifestReference)
            || string.IsNullOrWhiteSpace(journal.StagedManifest.ManifestDigest)
            || string.IsNullOrWhiteSpace(journal.StagedManifest.StageToken))
        {
            throw new InvalidDataException("INPUT_COMMIT_JOURNAL_INVALID");
        }

        var draft = journal.ReadyDraft;
        return new JournalEnvelope(
            journal.SchemaVersion,
            journal.BatchId,
            journal.State,
            journal.StagedManifest.ManifestReference,
            journal.StagedManifest.ManifestDigest,
            journal.StagedManifest.StageToken,
            draft.InputRoot,
            draft.OutputRoot,
            draft.InputType!.Value,
            draft.SelectionMode,
            draft.IncludeEditablePsd,
            new SnapshotEnvelope(
                draft.TemplateSnapshot.SnapshotId,
                draft.TemplateSnapshot.BatchId,
                draft.TemplateSnapshot.RevisionId,
                draft.TemplateSnapshot.RulesDigest,
                draft.TemplateSnapshot.CreatedAt,
                draft.TemplateSnapshot.Pages.Select(page => new PageEnvelope(
                    page.PageId,
                    page.Order,
                    page.ContentType,
                    page.GenerationMode,
                    page.VariantCapacity,
                    page.TemplateResourceId,
                    page.FrameIds.ToArray())).ToArray()),
            draft.Styles.Select(style => new StyleEnvelope(
                style.StyleId,
                style.DefaultStyleCode,
                style.SourceDirectoryRelativePath,
                style.Order,
                style.AssetIds.ToArray())).ToArray(),
            draft.Assets.Select(asset => new AssetEnvelope(
                asset.AssetId,
                asset.StyleId,
                asset.SourceRelativePath,
                asset.FileName,
                asset.Length,
                asset.Sha256,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Format,
                asset.MimeType,
                asset.Readability)).ToArray(),
            draft.CreatedAt,
            draft.UpdatedAt,
            journal.UpdatedAt);
    }

    private static InputCommitJournal FromEnvelope(
        JournalEnvelope envelope,
        string requestedBatchId)
    {
        if (!string.Equals(envelope.SchemaVersion, InputCommitJournalProtocol.Version, StringComparison.Ordinal)
            || !string.Equals(envelope.BatchId, requestedBatchId, StringComparison.Ordinal)
            || !Enum.IsDefined(envelope.State)
            || string.IsNullOrWhiteSpace(envelope.ManifestReference)
            || string.IsNullOrWhiteSpace(envelope.ManifestDigest)
            || string.IsNullOrWhiteSpace(envelope.StageToken)
            || envelope.Snapshot is null
            || envelope.Styles is null
            || envelope.Assets is null
            || envelope.Snapshot.Pages is null
            || envelope.CreatedAt == default
            || envelope.UpdatedAt == default)
        {
            throw new InvalidDataException("INPUT_COMMIT_JOURNAL_INVALID");
        }

        var pages = envelope.Snapshot.Pages.Select(page => new BatchTemplatePageSnapshot(
            page.PageId,
            page.Order,
            page.ContentType,
            page.GenerationMode,
            page.VariantCapacity,
            page.TemplateResourceId,
            (page.FrameIds ?? []).ToImmutableArray())).ToArray();
        var snapshot = BatchTemplateSnapshot.Restore(
            envelope.Snapshot.SnapshotId,
            envelope.Snapshot.BatchId,
            envelope.Snapshot.RevisionId,
            pages,
            envelope.Snapshot.RulesDigest,
            envelope.Snapshot.CreatedAt);
        var styles = envelope.Styles.Select(style => StyleDraft.Create(
            style.StyleId,
            style.DefaultStyleCode,
            style.SourceDirectoryRelativePath,
            style.Order,
            style.AssetIds ?? [])).ToArray();
        var assets = envelope.Assets.Select(asset => asset.Readability == AssetReadability.Readable
            ? SourceAssetMetadata.CreateReadable(
                asset.AssetId,
                asset.StyleId,
                asset.SourceRelativePath,
                asset.FileName,
                asset.Length,
                asset.Sha256,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Format,
                asset.MimeType)
            : throw new InvalidDataException("INPUT_COMMIT_JOURNAL_ASSET_UNREADABLE")).ToArray();
        var ready = BatchDraft.Restore(
            envelope.BatchId,
            BatchScanStatus.ScanReady,
            envelope.InputType,
            envelope.InputRoot,
            envelope.OutputRoot,
            snapshot,
            envelope.SelectionMode,
            envelope.IncludeEditablePsd,
            styles,
            assets,
            envelope.ManifestReference,
            envelope.ManifestDigest,
            null,
            envelope.CreatedAt,
            envelope.UpdatedAt);
        var staged = new StagedAssetManifest(
            envelope.BatchId,
            envelope.ManifestReference,
            envelope.ManifestDigest,
            envelope.StageToken);
        return new InputCommitJournal(
            envelope.SchemaVersion,
            envelope.BatchId,
            envelope.State,
            staged,
            ready,
            envelope.JournalUpdatedAt);
    }

    private static string PathFor(string batchId) =>
        $"batches/{batchId}/asset-manifest/commit.journal.json";

    private static void ValidateId(string value)
    {
        if (!IsValidId(value))
        {
            throw new InvalidOperationException("INPUT_BATCH_ID_INVALID");
        }
    }

    private static bool IsValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private sealed record JournalEnvelope(
        string SchemaVersion,
        string BatchId,
        InputCommitJournalState State,
        string ManifestReference,
        string ManifestDigest,
        string StageToken,
        string InputRoot,
        string OutputRoot,
        BatchInputType InputType,
        BatchSelectionMode SelectionMode,
        bool IncludeEditablePsd,
        SnapshotEnvelope Snapshot,
        IReadOnlyList<StyleEnvelope> Styles,
        IReadOnlyList<AssetEnvelope> Assets,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset JournalUpdatedAt);

    private sealed record SnapshotEnvelope(
        string SnapshotId,
        string BatchId,
        string RevisionId,
        string RulesDigest,
        DateTimeOffset CreatedAt,
        IReadOnlyList<PageEnvelope> Pages);

    private sealed record PageEnvelope(
        string PageId,
        int Order,
        TemplatePageContentType ContentType,
        TemplateGenerationMode GenerationMode,
        int? VariantCapacity,
        string TemplateResourceId,
        IReadOnlyList<string>? FrameIds);

    private sealed record StyleEnvelope(
        string StyleId,
        string DefaultStyleCode,
        string SourceDirectoryRelativePath,
        int Order,
        IReadOnlyList<string>? AssetIds);

    private sealed record AssetEnvelope(
        string AssetId,
        string StyleId,
        string SourceRelativePath,
        string FileName,
        long Length,
        string Sha256,
        int PixelWidth,
        int PixelHeight,
        SourceImageFormat Format,
        string MimeType,
        AssetReadability Readability);
}
