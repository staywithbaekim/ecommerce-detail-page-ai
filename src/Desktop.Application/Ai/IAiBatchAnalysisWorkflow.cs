namespace Desktop.Application.Ai;

/// <summary>
/// Application-facing orchestration for the B-02/B-03 vertical slice.  The
/// presentation layer consumes these projections and commands; it never
/// carries scheduler state, provider DTOs or product rules.
/// </summary>
public interface IAiBatchAnalysisWorkflow
{
    Task<AiBatchAnalysisProjection> StartAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection?> RefreshAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> PauseAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> ResumeAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> CancelAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> WaitForNetworkAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> LockSelectionAsync(
        string batchId,
        string styleId,
        string requirementId,
        string assetId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> SelectSizeTableSourceAsync(
        string batchId,
        string styleId,
        string assetId,
        CancellationToken cancellationToken);

    Task<AiBatchAnalysisProjection> AnalyzeSelectedAssetAsync(
        string batchId,
        string styleId,
        string assetId,
        bool includeSizeTableSource,
        CancellationToken cancellationToken);

    Task<AiRenderPlanPreparation> PrepareRenderingAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord?> GetUploadConsentAsync(
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> GrantUploadConsentAsync(
        CancellationToken cancellationToken);

    Task<AiUploadConsentRecord> RevokeUploadConsentAsync(
        CancellationToken cancellationToken);
}
