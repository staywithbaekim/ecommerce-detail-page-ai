using Desktop.Application.Ai;
using Desktop.Application.Tasks;

namespace Desktop.Application.Rendering;

public enum RenderBatchStatus { Prepared, Running, Paused, RecoveryRequired, Completed, CompletedWithIssues, Cancelled }
public enum RenderBatchPageStatus { Planned, Blocked, Submitting, Running, Committed, Failed, Cancelled, Unknown }

/// <summary>Logical plans and durable execution facts only; no paths, credentials or transport handles.</summary>
public sealed record RenderBatchPage(string StyleId, string PageId, string? OutputVariantId, int Attempt,
    string? PlanJson, RenderBatchPageStatus Status, string ReasonCode,
    string? ExecutionId = null, RenderPageCommitCheckpoint? Commit = null,
    string? PreparationReasonCode = null, string? OutputVariantName = null,
    RenderSizeTableRepairOrigin? SizeTableRepair = null);

public sealed record RenderBatchJournal(int Version, string BatchId, long Revision, RenderBatchStatus Status,
    bool PauseRequested, bool CancelRequested, IReadOnlyList<RenderBatchPage> Pages, int? RegenerationPageIndex = null);

public interface IRenderBatchStore
{
    Task<RenderBatchJournal?> LoadRenderBatchAsync(string batchId, CancellationToken cancellationToken);
    Task SaveRenderBatchAsync(RenderBatchJournal journal, long expectedRevision, CancellationToken cancellationToken);
}

public sealed record RenderPageObservation(RenderBatchPageStatus Status, string ReasonCode,
    string? ExecutionId = null, RenderPageCommitCheckpoint? Commit = null);

/// <summary>The formal rendering backend owns resource leases and independent commit verification.</summary>
public interface IRenderBatchSession : IAsyncDisposable
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
    Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken cancellationToken);
    Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken cancellationToken);
    Task CancelAsync(RenderBatchPage page, CancellationToken cancellationToken);
}

public interface IRenderBatchSessionFactory
{
    Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken);
}

public interface IRenderBatchWorkflow
{
    Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken cancellationToken);
    Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken cancellationToken);
    Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId,
        string? outputVariantId, int expectedAttempt, CancellationToken cancellationToken);
    Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken cancellationToken);
    Task RequestPauseAsync(string batchId, CancellationToken cancellationToken);
    Task RequestCancelAsync(string batchId, CancellationToken cancellationToken);
}

/// <summary>Returns an independently verified, read-only JPG lease; the consumer must dispose it.</summary>
public interface IRenderBatchOutputReader
{
    Task<Stream> OpenJpegAsync(string batchId, RenderBatchPage page, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the exact batch's persisted, confirmed output root without recovery or rendering.
    /// Returns null when no committed output is registered; never infers a root from page artifacts.
    /// </summary>
    Task<string?> GetOutputDirectoryAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>Returns the exact public directory for one frozen style when it exists.</summary>
    Task<string?> GetStyleOutputDirectoryAsync(string batchId, string styleId, CancellationToken cancellationToken) =>
        GetOutputDirectoryAsync(batchId, cancellationToken);
}

/// <summary>Publishes already-committed internal pages; it never submits or retries Photoshop work.</summary>
public interface IRenderStyleOutputPublisher
{
    Task<bool> UsesPublicOutputAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Admits only a terminal, explicitly failed page whose style has not entered public publication.
    /// This is a recovery retry, not post-publication editing.
    /// </summary>
    Task<bool> CanRecoverFailedPageAsync(string batchId, string styleId, string pageId, string? outputVariantId,
        int expectedAttempt, CancellationToken cancellationToken);

    Task PublishAsync(string batchId, RenderBatchJournal journal, CancellationToken cancellationToken);
}
