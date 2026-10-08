using Desktop.Application.Psd;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// States for the PSD-first reference learning workflow.  A learning result
/// is never committed directly from <see cref="ReferenceLearningState.Draft"/>:
/// the user must make an explicit decision for every candidate that will be
/// projected to a template binding.
/// </summary>
public enum ReferenceLearningState
{
    Draft = 0,
    Inspecting,
    Matching,
    AwaitingReview,
    ReadyToCommit,
    Committed,
    RequiresManualCalibration,
    Blocked,
    Cancelled
}

public enum ReferenceMatchState
{
    Exact = 0,
    ManuallyConfirmed,
    Ambiguous,
    NoMatch,
    Modified,
    ExternalLink,
    Unsupported,
    Missing
}

public enum ReferenceReviewDecision
{
    Pending = 0,
    Accepted,
    Rejected,
    ManualCalibration
}

public enum ReferenceSemanticSource
{
    LegacyV1 = 0,
    AiV2,
    UserOverride,
    AiUnavailable
}

public sealed record ReferenceSemanticMetadata(
    ReferenceSemanticSource Source,
    string RequestSchemaVersion,
    string ResultSchemaVersion,
    string ModelConfigVersion,
    string PromptContractVersion,
    string CacheKey,
    IReadOnlyList<string> PositiveEvidence,
    IReadOnlyList<string> ExclusionEvidence,
    bool UserOverridden = false)
{
    public bool IsCommitEligible => Source is ReferenceSemanticSource.AiV2 or ReferenceSemanticSource.UserOverride
        && !string.IsNullOrWhiteSpace(RequestSchemaVersion)
        && !string.IsNullOrWhiteSpace(ResultSchemaVersion)
        && !string.IsNullOrWhiteSpace(PromptContractVersion);
}

/// <summary>
/// Transient request.  <see cref="ReferencePhotoDirectory"/> is consumed
/// during analysis only and is intentionally absent from persisted results.
/// </summary>
public sealed record ReferenceLearningRequest(
    string LearningId,
    string PageId,
    string InternalPsdRelativePath,
    string ReferencePhotoDirectory,
    string? PreviewRelativePath = null,
    string? PageName = null);

public sealed record ReferencePhotoFingerprint(
    string AssetId,
    string FileName,
    string RelativePath,
    long Length,
    string Sha256,
    string MimeType,
    int Width,
    int Height);

public sealed record ReferencePhotoInventory(
    string FolderDigest,
    IReadOnlyList<ReferencePhotoFingerprint> Photos);

/// <summary>
/// A lower smart-object layer that belongs to the same visual replacement
/// stack as a learned frame.  It is retained as evidence so a renderer can
/// hide exactly the superseded layers without relying on layer names.
/// </summary>
public sealed record ReferenceSupersededLayerEvidence(
    IReadOnlyList<int> LayerPath,
    int LayerId,
    bool Visible,
    bool Clipping);

public sealed record ReferenceLearningError(
    string Code,
    string Message,
    string UserAction,
    bool Retryable = false);

public sealed record ReferenceLearningResult(
    ReferenceLearningDocument? Document,
    ReferenceLearningError? Error)
{
    public bool IsSuccess => Document is not null && Error is null;

    public static ReferenceLearningResult Success(ReferenceLearningDocument document) =>
        new(document ?? throw new ArgumentNullException(nameof(document)), null);

    public static ReferenceLearningResult Failure(ReferenceLearningError error) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>
/// A candidate derived from one visible smart object.  It contains only
/// relative/photo identities and structural evidence; no source path or media
/// bytes are carried across the application boundary.
/// </summary>
public sealed record ReferenceFrameCandidate(
    string FrameId,
    string PageId,
    IReadOnlyList<int> LayerPath,
    int LayerId,
    string LayerName,
    string StructureSignature,
    bool Visible,
    bool EffectiveVisible,
    bool Clipping,
    bool UserMask,
    bool VectorMask,
    byte Opacity,
    string BlendMode,
    IReadOnlyList<double> TransformBox,
    ReferenceMatchState MatchState,
    string? ReferenceAssetId,
    string? ReferencePhotoRelativePath,
    string? EmbeddedContentSha256,
    string SemanticLabel,
    double Confidence,
    LayoutIntent LayoutIntent,
    ReferenceReviewDecision ReviewDecision = ReferenceReviewDecision.Pending,
    string? ReviewNote = null,
    IReadOnlyList<ReferenceSupersededLayerEvidence>? SupersededLayers = null,
    IReadOnlyList<int>? WindowLayerPath = null,
    bool IsProduct = true,
    string? StructuralReason = null,
    PsdLayerPixelEvidence? PixelEvidence = null,
    ReferenceSemanticMetadata? SemanticMetadata = null)
{
    public IReadOnlyList<ReferenceSupersededLayerEvidence> SupersededLayerEvidence =>
        SupersededLayers ?? [];

    public IReadOnlyList<int> StructuralWindowLayerPath => WindowLayerPath ?? [];

    public string StructuralReasonText => string.IsNullOrWhiteSpace(StructuralReason)
        ? "可见智能对象候选"
        : StructuralReason;

    public PsdLayerPixelEvidence EffectivePixelEvidence => PixelEvidence
        ?? new PsdLayerPixelEvidence(false, 0, 0, null);

    /// <summary>
    /// True only when the local evidence closes over the current candidate:
    /// the embedded payload matches exactly (or a user has explicitly selected
    /// a source photo), the structural identity is present, and the derived
    /// layout is valid.  This is deliberately independent of review state so
    /// the UI can offer a confirmation shortcut without committing anything.
    /// </summary>
    public bool IsEvidenceClosed => IsProduct
        && Visible
        && EffectiveVisible
        && LayerPath.Count > 0
        && LayerPath.All(segment => segment >= 0)
        && !string.IsNullOrWhiteSpace(StructureSignature)
        && TransformBox.Count == 8
        && EffectivePixelEvidence.Available
        && EffectivePixelEvidence.RenderedPixelCount > 0
        && EffectivePixelEvidence.CompositeContributionPixelCount > 0
        && EffectivePixelEvidence.CompositeContributionBounds is not null
        && LayoutIntent.Scale > 0
        && LayoutIntent.SafeBounds.Right > LayoutIntent.SafeBounds.Left
        && LayoutIntent.SafeBounds.Bottom > LayoutIntent.SafeBounds.Top
        && !string.IsNullOrWhiteSpace(ReferenceAssetId)
        && !string.IsNullOrWhiteSpace(ReferencePhotoRelativePath)
        && MatchState is ReferenceMatchState.Exact or ReferenceMatchState.ManuallyConfirmed;

    /// <summary>
    /// A candidate may be included in “confirm qualified” only when local
    /// matching is exact, the usage label was inferred with an unambiguous
    /// local cue, and no review decision has been made yet.  It is still sent
    /// through <see cref="IReferenceLearningService.ApplyReviewAsync"/> so the
    /// explicit review gate remains intact.
    /// </summary>
    public bool IsAutoConfirmable => IsEvidenceClosed
        && MatchState == ReferenceMatchState.Exact
        && Confidence >= 0.99
        && !string.IsNullOrWhiteSpace(SemanticLabel)
        && (SemanticMetadata is null || SemanticMetadata.IsCommitEligible)
        && ReviewDecision == ReferenceReviewDecision.Pending;

    public bool IsHighConfidence => (MatchState is ReferenceMatchState.Exact
            or ReferenceMatchState.ManuallyConfirmed)
        && IsEvidenceClosed
        && Confidence >= 0.99
        && ReviewDecision == ReferenceReviewDecision.Accepted
        && IsProduct
        && !string.IsNullOrWhiteSpace(ReferenceAssetId)
        && !string.IsNullOrWhiteSpace(SemanticLabel)
        && (SemanticMetadata is null || SemanticMetadata.IsCommitEligible);

    // Primitive projections keep the presentation layer on the Application
    // contract instead of forcing it to reference the Domain assembly merely
    // to display layout values.
    public double LayoutScale => LayoutIntent.Scale;

    public double LayoutOffsetX => LayoutIntent.OffsetX;

    public double LayoutOffsetY => LayoutIntent.OffsetY;

    public double LayoutSafeLeft => LayoutIntent.SafeBounds.Left;

    public double LayoutSafeTop => LayoutIntent.SafeBounds.Top;

    public double LayoutSafeRight => LayoutIntent.SafeBounds.Right;

    public double LayoutSafeBottom => LayoutIntent.SafeBounds.Bottom;

    public ReferenceFrameCandidate Review(
        ReferenceReviewDecision decision,
        string? semanticLabel = null,
        LayoutIntent? layoutIntent = null,
        string? note = null) => this with
        {
            ReviewDecision = decision,
            SemanticLabel = string.IsNullOrWhiteSpace(semanticLabel)
                ? SemanticLabel
                : semanticLabel.Trim(),
            LayoutIntent = layoutIntent ?? LayoutIntent,
            ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
}

/// <summary>
/// Serializable, path-safe learning result.  The absolute input folder is
/// deliberately not represented; its digest is enough to detect a changed
/// reference set when a session is resumed.
/// </summary>
public sealed record ReferenceLearningDocument(
    string LearningId,
    string PageId,
    string InternalPsdRelativePath,
    string PsdSha256,
    long PsdLength,
    string StructureSignature,
    PsdCanvasInspection Canvas,
    string ReferenceFolderDigest,
    IReadOnlyList<ReferencePhotoFingerprint> ReferencePhotos,
    IReadOnlyList<ReferenceFrameCandidate> Candidates,
    ReferenceLearningState State,
    string RulesDigest,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? BlockReason = null)
{
    /// <summary>
    /// Version of the persisted learning document itself.  The session
    /// envelope also carries this value, but keeping it in the document makes
    /// standalone exports and schema validation self-describing.
    /// </summary>
    public string SchemaVersion { get; init; } = "reference-learning/v1";

    public bool CanCommit => State == ReferenceLearningState.ReadyToCommit
        && Candidates.Count > 0
        && Candidates.All(candidate => candidate.ReviewDecision is not (
            ReferenceReviewDecision.Pending or ReferenceReviewDecision.ManualCalibration))
        && Candidates
            .Where(candidate => candidate.ReviewDecision != ReferenceReviewDecision.Rejected)
            .All(candidate => candidate.IsHighConfidence);

    public ReferenceLearningDocument WithCandidates(
        IReadOnlyList<ReferenceFrameCandidate> candidates,
        ReferenceLearningState state,
        string? blockReason,
        DateTimeOffset updatedAt) => this with
        {
            Candidates = candidates,
            State = state,
            RulesDigest = ReferenceLearningDigest.Compute(
                PsdSha256,
                StructureSignature,
                ReferenceFolderDigest,
                ReferencePhotos,
                candidates),
            UpdatedAt = updatedAt,
            BlockReason = blockReason
        };
}

public sealed record ReferenceFrameReview(
    string FrameId,
    ReferenceReviewDecision Decision,
    string? ReferenceAssetId = null,
    string? SemanticLabel = null,
    double? Scale = null,
    double? OffsetX = null,
    double? OffsetY = null,
    string? Note = null);

public interface IReferencePhotoFingerprintPort
{
    Task<ReferencePhotoInventory> ScanAsync(
        string referencePhotoDirectory,
        CancellationToken cancellationToken);
}

public interface IReferenceLearningSessionStore
{
    Task SaveAsync(ReferenceLearningDocument document, CancellationToken cancellationToken);

    Task<ReferenceLearningDocument?> GetAsync(
        string learningId,
        CancellationToken cancellationToken);
}

public interface IReferenceLearningService
{
    Task<ReferenceLearningResult> AnalyzeAsync(
        ReferenceLearningRequest request,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningResult> ApplyReviewAsync(
        ReferenceLearningDocument document,
        IReadOnlyList<ReferenceFrameReview> reviews,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningResult> ResumeAsync(
        string learningId,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningResult> ResumeAsync(
        ReferenceLearningRequest request,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningResult> CancelAsync(
        ReferenceLearningDocument document,
        CancellationToken cancellationToken = default);
}

public sealed record ReferenceLearningCommitResult(
    ReferenceLearningDocument? Document,
    TemplateGroupDetails? Template,
    ReferenceLearningError? Error)
{
    public bool IsSuccess => Document is not null && Template is not null && Error is null;

    public static ReferenceLearningCommitResult Success(
        ReferenceLearningDocument document,
        TemplateGroupDetails template) => new(document, template, null);

    public static ReferenceLearningCommitResult Failure(ReferenceLearningError error) =>
        new(null, null, error ?? throw new ArgumentNullException(nameof(error)));
}

public interface IReferenceLearningCommitService
{
    // Stage validated evidence into the existing draft; never publish a revision.
    Task<ReferenceLearningCommitResult> StageDraftAsync(
        IReadOnlyList<ReferenceLearningDocument> documents, TemplateDraftSession draftSession,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TPL_LEARN_DRAFT_STAGING_UNAVAILABLE");

    Task<ReferenceLearningCommitResult> CommitAsync(
        ReferenceLearningDocument document,
        TemplateDraftSession draftSession,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningCommitResult> CommitAsync(
        IReadOnlyList<ReferenceLearningDocument> documents,
        TemplateDraftSession draftSession,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects only explicitly accepted exact or manually confirmed matches into the existing
/// renderer-neutral template contract.  Explicitly retained template content is omitted from
/// the binding set.  Reference asset IDs are retained as provenance criteria; a target batch
/// must provide explicit selections and therefore cannot silently reuse reference photos.
/// </summary>
public static class ReferenceLearningRuleProjector
{
    public const string ReferenceAuditVersion = "reference-audit/v1";
    public const string ReferenceHashChunkPrefix = "reference_hashes_";
    private const int HashesPerCriterion = 3;

    public static IReadOnlyList<FrameBinding> ProjectBindings(
        ReferenceLearningDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanCommit)
        {
            throw new TemplateRuleViolationException("TPL_LEARN_REVIEW_INCOMPLETE");
        }
        if (!string.Equals(
            document.RulesDigest,
            ReferenceLearningDigest.Compute(
                document.PsdSha256,
                document.StructureSignature,
                document.ReferenceFolderDigest,
                document.ReferencePhotos,
                document.Candidates),
            StringComparison.Ordinal))
        {
            throw new TemplateRuleViolationException("TPL_LEARN_RULES_DIGEST_INVALID");
        }

        return document.Candidates
            .Where(candidate => candidate.IsHighConfidence)
            .Select(candidate => CreateBinding(
                candidate,
                candidate.ReferenceAssetId!,
                document.ReferencePhotos.SingleOrDefault(photo => string.Equals(
                    photo.AssetId,
                    candidate.ReferenceAssetId,
                    StringComparison.Ordinal)),
                document.ReferenceFolderDigest,
                document.ReferencePhotos))
            .ToArray();
    }

    private static FrameBinding CreateBinding(
        ReferenceFrameCandidate candidate,
        string assetId,
        ReferencePhotoFingerprint? referencePhoto,
        string referenceSetDigest,
        IReadOnlyList<ReferencePhotoFingerprint> referencePhotos) => FrameBinding.Create(
            candidate.FrameId,
            candidate.PageId,
            LayerPath.Create(NormalizeLayerPath(candidate.LayerPath)),
            ExpectedLayerEvidence.Create(
                candidate.LayerId,
                candidate.LayerName,
                candidate.Visible,
                candidate.Clipping,
                candidate.UserMask,
                candidate.VectorMask,
                candidate.Opacity,
                candidate.BlendMode,
                LayerStructureSignature.Create(candidate.StructureSignature)),
            candidate.SupersededLayerEvidence.Select(layer =>
                SupersededLayerEvidence.Create(
                    LayerPath.Create(NormalizeLayerPath(layer.LayerPath)),
                    layer.Clipping,
                    layer.LayerId)),
            SelectionRequirement.Create(
                string.IsNullOrWhiteSpace(candidate.SemanticLabel)
                    ? "请选择目标款中与该图片框语义匹配的商品图。"
                    : candidate.SemanticLabel,
                CreateSelectionCriteria(
                    candidate,
                    assetId,
                    referencePhoto,
                    referenceSetDigest,
                    referencePhotos)),
            candidate.LayoutIntent);

    private static List<SelectionCriterion> CreateSelectionCriteria(
        ReferenceFrameCandidate candidate,
        string assetId,
        ReferencePhotoFingerprint? referencePhoto,
        string referenceSetDigest,
        IReadOnlyList<ReferencePhotoFingerprint> referencePhotos)
    {
        var hashes = referencePhotos
            .Select(photo => photo.Sha256.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (hashes.Length == 0
            || hashes.Length > Desktop.Application.Ai.AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest
            || hashes.Any(hash => hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new TemplateRuleViolationException("TPL_LEARN_REFERENCE_AUDIT_INVALID");
        }

        var criteria = new List<SelectionCriterion>
        {
            SelectionCriterion.Create("asset_id", assetId),
            SelectionCriterion.Create("reference_only", "true"),
            SelectionCriterion.Create("reference_audit_version", ReferenceAuditVersion),
            SelectionCriterion.Create("reference_set_digest", referenceSetDigest.Trim().ToLowerInvariant()),
            SelectionCriterion.Create(
                "semantic_source",
                candidate.SemanticMetadata?.Source.ToString() ?? "LegacyV1"),
            SelectionCriterion.Create(
                "semantic_request_version",
                candidate.SemanticMetadata?.RequestSchemaVersion ?? "reference-learning/v1"),
            SelectionCriterion.Create(
                "semantic_result_version",
                candidate.SemanticMetadata?.ResultSchemaVersion ?? "reference-learning/v1"),
            SelectionCriterion.Create(
                "semantic_model_version",
                candidate.SemanticMetadata?.ModelConfigVersion ?? "legacy-local-model/v1"),
            SelectionCriterion.Create(
                "semantic_prompt_version",
                candidate.SemanticMetadata?.PromptContractVersion ?? "legacy-local-hint/v1"),
            SelectionCriterion.Create(
                "semantic_cache_key",
                candidate.SemanticMetadata?.CacheKey ?? "legacy-none"),
            SelectionCriterion.Create(
                "reference_file_name",
                referencePhoto?.FileName ?? assetId),
            SelectionCriterion.Create(
                "reference_relative_path",
                referencePhoto?.RelativePath ?? referencePhoto?.FileName ?? assetId)
        };
        for (var index = 0; index < hashes.Length; index += HashesPerCriterion)
        {
            criteria.Add(SelectionCriterion.Create(
                $"{ReferenceHashChunkPrefix}{index / HashesPerCriterion:D2}",
                string.Join('.', hashes.Skip(index).Take(HashesPerCriterion))));
        }

        return criteria;
    }

    private static int[] NormalizeLayerPath(IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0 || path.Any(value => value < 0))
        {
            throw new TemplateRuleViolationException("TPL_FRAME_LAYER_PATH_INVALID");
        }

        // psd-tools reports zero-based collection indexes while the persisted
        // template contract uses positive path segments.  Normalize once at
        // the boundary instead of leaking parser-specific indexing downstream.
        return path.Select(value => value + 1).ToArray();
    }
}

public static class ReferenceLearningDigest
{
    public static string Compute(
        string psdSha256,
        string structureSignature,
        string referenceFolderDigest,
        IEnumerable<ReferencePhotoFingerprint> referencePhotos,
        IEnumerable<ReferenceFrameCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(referencePhotos);
        ArgumentNullException.ThrowIfNull(candidates);
        var photos = referencePhotos
            .OrderBy(photo => photo.AssetId, StringComparer.Ordinal)
            .ThenBy(photo => photo.RelativePath, StringComparer.Ordinal)
            .Select(photo => new
            {
                photo.AssetId,
                photo.FileName,
                photo.RelativePath,
                photo.Length,
                photo.Sha256,
                photo.MimeType,
                photo.Width,
                photo.Height
            })
            .ToArray();
        var candidateValues = candidates.ToArray();
        var frames = candidateValues
            .OrderBy(candidate => candidate.FrameId, StringComparer.Ordinal)
            .Select(candidate => new
            {
                candidate.FrameId,
                candidate.PageId,
                candidate.LayerPath,
                candidate.LayerId,
                candidate.LayerName,
                candidate.StructureSignature,
                candidate.Visible,
                candidate.EffectiveVisible,
                candidate.Clipping,
                candidate.UserMask,
                candidate.VectorMask,
                candidate.Opacity,
                candidate.BlendMode,
                candidate.TransformBox,
                MatchState = candidate.MatchState.ToString(),
                candidate.ReferenceAssetId,
                candidate.ReferencePhotoRelativePath,
                candidate.EmbeddedContentSha256,
                SupersededLayers = candidate.SupersededLayerEvidence.Select(layer => new
                {
                    layer.LayerPath,
                    layer.LayerId,
                    layer.Visible,
                    layer.Clipping
                }),
                candidate.StructuralWindowLayerPath,
                candidate.IsProduct,
                candidate.StructuralReasonText,
                PixelEvidence = new
                {
                    candidate.EffectivePixelEvidence.Available,
                    candidate.EffectivePixelEvidence.RenderedPixelCount,
                    candidate.EffectivePixelEvidence.CompositeContributionPixelCount,
                    candidate.EffectivePixelEvidence.CompositeContributionBounds
                },
                candidate.SemanticLabel,
                candidate.Confidence,
                LayoutIntent = new
                {
                    candidate.LayoutIntent.Intent,
                    candidate.LayoutIntent.Scale,
                    candidate.LayoutIntent.OffsetX,
                    candidate.LayoutIntent.OffsetY,
                    candidate.LayoutIntent.SafeBounds
                },
                ReviewDecision = candidate.ReviewDecision.ToString(),
                candidate.ReviewNote
            })
            .ToArray();
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            PsdSha256 = psdSha256,
            StructureSignature = structureSignature,
            ReferenceFolderDigest = referenceFolderDigest,
            ReferencePhotos = photos,
            Candidates = frames
        });
        if (candidateValues.Any(candidate => candidate.SemanticMetadata is not null))
        {
            payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                BaseDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
                Semantics = candidateValues
                    .OrderBy(candidate => candidate.FrameId, StringComparer.Ordinal)
                    .Select(candidate => candidate.SemanticMetadata is null
                        ? null
                        : new
                        {
                            Source = candidate.SemanticMetadata.Source.ToString(),
                            candidate.SemanticMetadata.RequestSchemaVersion,
                            candidate.SemanticMetadata.ResultSchemaVersion,
                            candidate.SemanticMetadata.ModelConfigVersion,
                            candidate.SemanticMetadata.PromptContractVersion,
                            candidate.SemanticMetadata.CacheKey,
                            candidate.SemanticMetadata.PositiveEvidence,
                            candidate.SemanticMetadata.ExclusionEvidence,
                            candidate.SemanticMetadata.UserOverridden
                        })
                    .ToArray()
            });
        }
        var hash = System.Security.Cryptography.SHA256.HashData(payload);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// Reproduces the original v1 digest for read-only recovery of sessions
    /// written before final-composite pixel evidence was introduced. Such a
    /// recovered candidate does not satisfy <see cref="ReferenceFrameCandidate.IsEvidenceClosed"/>
    /// and therefore cannot be newly committed without reanalysis.
    /// </summary>
    public static string ComputeLegacyWithoutPixelEvidence(
        string psdSha256,
        string structureSignature,
        string referenceFolderDigest,
        IEnumerable<ReferencePhotoFingerprint> referencePhotos,
        IEnumerable<ReferenceFrameCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(referencePhotos);
        ArgumentNullException.ThrowIfNull(candidates);
        var photos = referencePhotos
            .OrderBy(photo => photo.AssetId, StringComparer.Ordinal)
            .ThenBy(photo => photo.RelativePath, StringComparer.Ordinal)
            .Select(photo => new
            {
                photo.AssetId,
                photo.FileName,
                photo.RelativePath,
                photo.Length,
                photo.Sha256,
                photo.MimeType,
                photo.Width,
                photo.Height
            })
            .ToArray();
        var frames = candidates
            .OrderBy(candidate => candidate.FrameId, StringComparer.Ordinal)
            .Select(candidate => new
            {
                candidate.FrameId,
                candidate.PageId,
                candidate.LayerPath,
                candidate.LayerId,
                candidate.LayerName,
                candidate.StructureSignature,
                candidate.Visible,
                candidate.EffectiveVisible,
                candidate.Clipping,
                candidate.UserMask,
                candidate.VectorMask,
                candidate.Opacity,
                candidate.BlendMode,
                candidate.TransformBox,
                MatchState = candidate.MatchState.ToString(),
                candidate.ReferenceAssetId,
                candidate.ReferencePhotoRelativePath,
                candidate.EmbeddedContentSha256,
                SupersededLayers = candidate.SupersededLayerEvidence.Select(layer => new
                {
                    layer.LayerPath,
                    layer.LayerId,
                    layer.Visible,
                    layer.Clipping
                }),
                candidate.StructuralWindowLayerPath,
                candidate.IsProduct,
                candidate.StructuralReasonText,
                candidate.SemanticLabel,
                candidate.Confidence,
                LayoutIntent = new
                {
                    candidate.LayoutIntent.Intent,
                    candidate.LayoutIntent.Scale,
                    candidate.LayoutIntent.OffsetX,
                    candidate.LayoutIntent.OffsetY,
                    candidate.LayoutIntent.SafeBounds
                },
                ReviewDecision = candidate.ReviewDecision.ToString(),
                candidate.ReviewNote
            })
            .ToArray();
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            PsdSha256 = psdSha256,
            StructureSignature = structureSignature,
            ReferenceFolderDigest = referenceFolderDigest,
            ReferencePhotos = photos,
            Candidates = frames
        });
        var hash = System.Security.Cryptography.SHA256.HashData(payload);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
