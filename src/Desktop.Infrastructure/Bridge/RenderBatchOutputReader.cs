using Desktop.Application.Inputs;
using Desktop.Application.Rendering;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Bridge;

public sealed class RenderBatchOutputReader(IBatchRepository batches, IRenderBatchStore journals,
    IRenderPageCommitStore commits, SqliteDesktopStore persistedBatches) : IRenderBatchOutputReader
{
    public async Task<string?> GetOutputDirectoryAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        // IBatchRepository is recovery-decorated in production. Directory lookup must not reconcile or write it.
        var batch = await persistedBatches.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null) throw new InvalidDataException("RENDER_BATCH_NOT_FOUND");
        if (batch.BatchId != batchId || batch.ScanStatus != BatchScanStatus.ScanReady)
            throw new InvalidDataException("RENDER_BATCH_INPUT_NOT_CONFIRMED");
        // New modes must never expose the private evidence root as the selected public destination.
        // Multiple source directories have no single safe batch-level substitute.
        if (batch.OutputSelection is not null)
            return batch.Styles.Length == 1
                ? await GetStyleOutputDirectoryAsync(batchId, batch.Styles[0].StyleId, cancellationToken).ConfigureAwait(false) : null;
        var journal = await journals.LoadRenderBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (journal is not null && journal.BatchId != batchId)
            throw new InvalidDataException("RENDER_BATCH_ID_MISMATCH");
        // A newer failed/Unknown attempt does not remove earlier committed output.
        if (journal is null || !journal.Pages.Any(page => page.Status == RenderBatchPageStatus.Committed && page.Commit is not null))
            return null;
        var root = batch.OutputRoot;
        if (root.Length < 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\'
            || root[2..].Contains(':') || root.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '?' or '*'))
            throw new InvalidDataException("RENDER_OUTPUT_DIRECTORY_INVALID");
        root = Path.GetFullPath(root);
        BridgeOutputReservation.ValidatePath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE");
        cancellationToken.ThrowIfCancellationRequested();
        return root;
    }

    public async Task<string?> GetStyleOutputDirectoryAsync(string batchId, string styleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        var batch = await persistedBatches.GetAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("RENDER_BATCH_NOT_FOUND");
        if (batch.ScanStatus != BatchScanStatus.ScanReady || !batch.Styles.Any(style => style.StyleId == styleId))
            throw new InvalidDataException("RENDER_BATCH_INPUT_NOT_CONFIRMED");
        return batch.OutputSelection is null
            ? await GetOutputDirectoryAsync(batchId, cancellationToken).ConfigureAwait(false)
            : await new RenderStyleOutputPublisher(persistedBatches, commits).GetVerifiedDirectoryAsync(batchId, styleId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenJpegAsync(string batchId, RenderBatchPage page, CancellationToken cancellationToken)
    {
        var journal = await journals.LoadRenderBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var current = journal?.Pages.SingleOrDefault(item => item.StyleId == page.StyleId && item.PageId == page.PageId
            && item.Instance == page.Instance && item.OutputVariantId == page.OutputVariantId && item.Attempt == page.Attempt);
        if (current?.Status != RenderBatchPageStatus.Committed || current.Commit is null || current != page)
            throw new InvalidDataException("RENDER_COMMIT_RECOVERY_REQUIRED");
        var batch = await batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("RENDER_BATCH_NOT_FOUND");
        if (!Path.IsPathFullyQualified(batch.OutputRoot)) throw new InvalidDataException("RENDER_BATCH_INPUT_INVALID");
        return await RenderPageCommitter.OpenCommittedJpegAsync(new LocalAppPathPolicy(batch.OutputRoot), commits,
            current.Commit, cancellationToken).ConfigureAwait(false);
    }
}
