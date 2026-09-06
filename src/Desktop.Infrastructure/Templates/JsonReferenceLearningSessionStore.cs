using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Templates;
using Desktop.Application.Psd;
using Desktop.Domain.Templates;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Templates;

/// <summary>
/// Durable learning-session snapshots.  The file name is derived solely from
/// the opaque learning id and the payload contains no absolute source paths.
/// AtomicFileStore provides the same replace-on-success semantics as template
/// manifests, so an interrupted write cannot replace a prior snapshot with a
/// partial JSON document.
/// </summary>
public sealed class JsonReferenceLearningSessionStore(
    IFileStore files,
    IJsonContractValidator? validator = null) : IReferenceLearningSessionStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    static JsonReferenceLearningSessionStore()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task SaveAsync(
        ReferenceLearningDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateId(document.LearningId);
        var envelope = ToEnvelope(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (validator is not null
            && !validator.Validate(
                ContractDocumentKind.ReferenceLearning,
                System.Text.Encoding.UTF8.GetString(bytes)).IsValid)
        {
            throw new InvalidOperationException("TPL_LEARN_SESSION_SCHEMA_INVALID");
        }
        await files.WriteAtomicallyAsync(PathFor(document.LearningId), bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReferenceLearningDocument?> GetAsync(
        string learningId,
        CancellationToken cancellationToken)
    {
        ValidateId(learningId);
        var bytes = await files.ReadAsync(PathFor(learningId), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            if (validator is not null
                && !validator.Validate(
                    ContractDocumentKind.ReferenceLearning,
                    System.Text.Encoding.UTF8.GetString(bytes)).IsValid)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<SessionEnvelope>(bytes, Options);
            return envelope is not null
                && string.Equals(envelope.SchemaVersion, "reference-learning/v1", StringComparison.Ordinal)
                ? FromEnvelope(envelope)
                : null;
        }
        catch (Exception error) when (error is JsonException
            or InvalidOperationException
            or ArgumentException
            or OverflowException)
        {
            return null;
        }
    }

    private static string PathFor(string learningId) =>
        $"learning/reference-sessions/{learningId}.json";

    private static void ValidateId(string learningId)
    {
        if (string.IsNullOrWhiteSpace(learningId)
            || learningId.Length > 128
            || learningId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidOperationException("TPL_LEARN_ID_INVALID");
        }
    }

    private static SessionEnvelope ToEnvelope(ReferenceLearningDocument document) => new(
        document.SchemaVersion,
        document.LearningId,
        document.PageId,
        document.InternalPsdRelativePath,
        document.PsdSha256,
        document.PsdLength,
        document.StructureSignature,
        document.Canvas,
        document.ReferenceFolderDigest,
        document.ReferencePhotos,
        document.Candidates.Select(candidate => new CandidateSnapshot(
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
            candidate.MatchState,
            candidate.ReferenceAssetId,
            candidate.ReferencePhotoRelativePath,
            candidate.EmbeddedContentSha256,
            candidate.SemanticLabel,
            candidate.Confidence,
            new LayoutIntentSnapshot(
                candidate.LayoutIntent.Intent,
                candidate.LayoutIntent.Scale,
                candidate.LayoutIntent.OffsetX,
                candidate.LayoutIntent.OffsetY,
                new RectSnapshot(
                    candidate.LayoutIntent.SafeBounds.Left,
                    candidate.LayoutIntent.SafeBounds.Top,
                    candidate.LayoutIntent.SafeBounds.Right,
                    candidate.LayoutIntent.SafeBounds.Bottom)),
            candidate.ReviewDecision,
            candidate.ReviewNote,
            candidate.SupersededLayerEvidence.Select(layer => new SupersededLayerSnapshot(
                layer.LayerPath,
                layer.LayerId,
                layer.Visible,
                layer.Clipping)).ToArray(),
            candidate.StructuralWindowLayerPath,
            candidate.IsProduct,
            candidate.StructuralReason)).ToArray(),
        document.State,
        document.RulesDigest,
        document.CreatedAt,
        document.UpdatedAt,
        document.BlockReason);

    private static ReferenceLearningDocument FromEnvelope(SessionEnvelope envelope)
    {
        var candidates = envelope.Candidates.Select(candidate => new ReferenceFrameCandidate(
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
            candidate.MatchState,
            candidate.ReferenceAssetId,
            candidate.ReferencePhotoRelativePath,
            candidate.EmbeddedContentSha256,
            candidate.SemanticLabel,
            candidate.Confidence,
            LayoutIntent.Create(
                candidate.LayoutIntent.Intent,
                candidate.LayoutIntent.Scale,
                candidate.LayoutIntent.OffsetX,
                candidate.LayoutIntent.OffsetY,
                TemplateRect.Create(
                    candidate.LayoutIntent.SafeBounds.Left,
                    candidate.LayoutIntent.SafeBounds.Top,
                    candidate.LayoutIntent.SafeBounds.Right,
                    candidate.LayoutIntent.SafeBounds.Bottom)),
            candidate.ReviewDecision,
            candidate.ReviewNote,
            candidate.SupersededLayers?.Select(layer => new ReferenceSupersededLayerEvidence(
                layer.LayerPath,
                layer.LayerId,
                layer.Visible,
                layer.Clipping)).ToArray(),
            candidate.WindowLayerPath,
            candidate.IsProduct,
            candidate.StructuralReason)).ToArray();
        var document = new ReferenceLearningDocument(
            envelope.LearningId,
            envelope.PageId,
            envelope.InternalPsdRelativePath,
            envelope.PsdSha256,
            envelope.PsdLength,
            envelope.StructureSignature,
            envelope.Canvas,
            envelope.ReferenceFolderDigest,
            envelope.ReferencePhotos,
            candidates,
            envelope.State,
            envelope.RulesDigest,
            envelope.CreatedAt,
            envelope.UpdatedAt,
            envelope.BlockReason)
        {
            SchemaVersion = envelope.SchemaVersion
        };
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
            throw new InvalidOperationException("TPL_LEARN_SESSION_DIGEST_INVALID");
        }

        return document;
    }

    private sealed record SessionEnvelope(
        string SchemaVersion,
        string LearningId,
        string PageId,
        string InternalPsdRelativePath,
        string PsdSha256,
        long PsdLength,
        string StructureSignature,
        PsdCanvasInspection Canvas,
        string ReferenceFolderDigest,
        IReadOnlyList<ReferencePhotoFingerprint> ReferencePhotos,
        IReadOnlyList<CandidateSnapshot> Candidates,
        ReferenceLearningState State,
        string RulesDigest,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? BlockReason);

    private sealed record CandidateSnapshot(
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
        LayoutIntentSnapshot LayoutIntent,
        ReferenceReviewDecision ReviewDecision,
        string? ReviewNote,
        IReadOnlyList<SupersededLayerSnapshot>? SupersededLayers = null,
        IReadOnlyList<int>? WindowLayerPath = null,
        bool IsProduct = true,
        string? StructuralReason = null);

    private sealed record SupersededLayerSnapshot(
        IReadOnlyList<int> LayerPath,
        int LayerId,
        bool Visible,
        bool Clipping);

    private sealed record LayoutIntentSnapshot(
        string Intent,
        double Scale,
        double OffsetX,
        double OffsetY,
        RectSnapshot SafeBounds);

    private sealed record RectSnapshot(double Left, double Top, double Right, double Bottom);
}
