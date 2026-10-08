using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Psd;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed record TemplateInstanceRenderPlanRequest(
    TemplateInstanceProjectionSnapshot Snapshot,
    string PageInstanceId,
    TemplateRevision Revision,
    ImportedTemplateResource TemplateResource,
    IReadOnlyList<SourceAssetMetadata> Assets,
    bool IncludeEditablePsd = false,
    TemplateInstanceProductionParameters? Production = null,
    TemplateRevision? ResourceOrigin = null);

public sealed record TemplateInstanceProductionParameters(int Version, string OutputBaseName,
    IReadOnlyList<TemplateRenderSubjectGeometry> Geometry, TemplateInstanceSizePreparation? Size = null);

public sealed record TemplateInstanceSizePreparation(string SourcePageId, string? TableJson,
    string? BasePlanJson, string? BusinessReason);

public sealed record TemplateInstanceRenderPlanResult(
    string PageInstanceId,
    string PlanId,
    string PlanDigest,
    string RenderPlanJson);

/// <summary>
/// E2 first slice: turns one validated, frozen instance into one renderer-neutral
/// plan. It does not create an execution record or enter the legacy batch path.
/// </summary>
public sealed partial class TemplateInstanceRenderPlanFactory(IRenderPlanValidator validator)
{
    private const string IdentitySchema = "template-instance-render-plan-identity/v1";
    private static readonly DateTimeOffset DeterministicPlanTimestamp = DateTimeOffset.UnixEpoch;

    public TemplateInstanceRenderPlanResult Build(TemplateInstanceRenderPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        ArgumentNullException.ThrowIfNull(request.Revision);
        ArgumentNullException.ThrowIfNull(request.TemplateResource);
        ArgumentNullException.ThrowIfNull(request.Assets);

        TemplateInstanceProjectionSnapshotCodec.Validate(request.Snapshot);
        TemplateValueRules.Identifier(request.PageInstanceId, "TPL_INSTANCE_RENDER_PLAN_INPUT_INVALID");
        if (request.Revision.State != TemplateRevisionState.Committed
            || request.Revision.RevisionId != request.Snapshot.RevisionId
            || !HasValidResourceOrigin(request))
            throw Invalid("TPL_INSTANCE_RENDER_PLAN_INPUT_INVALID");

        var instance = request.Snapshot.Instances.SingleOrDefault(item =>
            item.PageInstanceId == request.PageInstanceId);
        if (instance is null)
            throw Invalid("TPL_INSTANCE_RENDER_INSTANCE_NOT_FOUND");
        if (request.Revision.Pages.SingleOrDefault(page => page.PageId == instance.SourcePageId)?.ContentType
            == TemplatePageContentType.DynamicSizeTable && request.Production?.Size is not null)
            return BuildSize(request, instance);
        if (instance.Status != TemplateInstancePreflightStatus.AssetBindingsSatisfied
            || !instance.BlockingReasons.IsEmpty)
            throw Invalid("TPL_INSTANCE_RENDER_INSTANCE_BLOCKED");
        if (instance.TemplateResourceId != request.TemplateResource.ResourceId
            || instance.SourcePageId != request.Revision.Pages.SingleOrDefault(page => page.PageId == instance.SourcePageId)?.PageId)
            throw Invalid("TPL_INSTANCE_RENDER_TEMPLATE_MISMATCH");

        var page = request.Revision.Pages.SingleOrDefault(item => item.PageId == instance.SourcePageId)
            ?? throw Invalid("TPL_INSTANCE_RENDER_PAGE_NOT_FOUND");
        if (page.ContentType != TemplatePageContentType.NormalComposition)
            throw Invalid("TPL_INSTANCE_RENDER_PAGE_UNSUPPORTED");
        if (page.TemplateResourceId != request.TemplateResource.ResourceId)
            throw Invalid("TPL_INSTANCE_RENDER_TEMPLATE_MISMATCH");

        var assets = request.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        if (assets.Count != request.Assets.Count || request.Assets.Any(asset => asset.StyleId != instance.StyleId
            || asset.Readability != AssetReadability.Readable || !IsSha(asset.Sha256) || asset.Length < 1))
            throw Invalid("TPL_INSTANCE_RENDER_ASSET_INVALID");

        var bindings = request.Revision.FrameBindings
            .Where(binding => binding.PageId == page.PageId)
            .OrderBy(binding => binding.FrameId, StringComparer.Ordinal)
            .ToArray();
        var frames = instance.Frames.OrderBy(frame => frame.FrameId, StringComparer.Ordinal).ToArray();
        if (bindings.Length == 0 || frames.Length != bindings.Length
            || frames.Select(frame => frame.FrameId).Distinct(StringComparer.Ordinal).Count() != frames.Length)
            throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");

        var frameById = frames.ToDictionary(frame => frame.FrameId, StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (!frameById.TryGetValue(binding.FrameId, out var frame)
                || frame.AssetId is null
                || !assets.ContainsKey(frame.AssetId)
                || frame.VariantId != instance.Slots.SingleOrDefault(slot => slot.SlotId == frame.SlotId)?.VariantId)
                throw Invalid("TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
        }

        if (request.Production is { } production && (production.Version != 1
            || string.IsNullOrWhiteSpace(production.OutputBaseName)
            || production.Geometry.Any(geometry => !geometry.IsValid)
            || production.Geometry.Select(geometry => geometry.AssetResourceId).Distinct(StringComparer.Ordinal).Count() != production.Geometry.Count))
            throw Invalid("TPL_INSTANCE_PRODUCTION_PARAMETERS_INVALID");
        var selected = frames.Select(frame => new TemplateRenderSelection(
            frame.FrameId, frame.AssetId!, request.Production?.Geometry.SingleOrDefault(geometry => geometry.AssetResourceId == frame.AssetId))).ToArray();
        var sourceAssets = selected.Select(selection => assets[selection.AssetResourceId])
            .DistinctBy(asset => asset.AssetId)
            .OrderBy(asset => asset.AssetId, StringComparer.Ordinal)
            .Select(asset => new TemplateRenderSource(asset.AssetId, asset.Sha256, asset.Length,
                asset.MimeType, asset.PixelWidth, asset.PixelHeight))
            .ToArray();

        var identityDigest = Hash(new
        {
            schema = IdentitySchema,
            instance.PageInstanceId,
            instance.BatchId,
            instance.StyleId,
            instance.SnapshotId,
            instance.RevisionId,
            instance.RuleDigest,
            instance.InputFactsDigest,
            request.Snapshot.ProjectionDigest,
            instance.SourcePageId,
            instance.TemplateResourceId,
            instance.BranchId,
            instance.RecipeId,
            instance.RepetitionIndex,
            instance.Order,
            template_sha256 = request.TemplateResource.Sha256,
            include_editable_psd = request.IncludeEditablePsd,
            assets = sourceAssets.Select(asset => new { asset.ResourceId, asset.Sha256, asset.Length, asset.MediaType, asset.PixelWidth, asset.PixelHeight }),
            frames = frames.Select(frame => new { frame.FrameId, frame.SlotId, frame.VariantId, frame.AssetId })
        });

        if (request.Production is not null)
            identityDigest = Hash(new { schema = "template-instance-render-plan-identity/v2", identityDigest, request.Production });
        var outputBaseName = request.Production?.OutputBaseName ?? "instance-" + identityDigest[7..31];
        var requestModel = new TemplateRenderPlanRequest(
            PlanId: "plan_" + identityDigest[7..31],
            IdempotencyKey: "render/instance/" + identityDigest[7..55],
            CorrelationId: "corr_" + identityDigest[31..55],
            BatchId: instance.BatchId,
            ProductId: instance.StyleId,
            ArtifactSetId: "artifact_" + identityDigest[40..64],
            JpgFileName: outputBaseName + ".jpg",
            IncludeEditablePsd: request.IncludeEditablePsd,
            PsdFileName: request.IncludeEditablePsd ? outputBaseName + ".psd" : null,
            CreatedAt: DeterministicPlanTimestamp,
            Sources: sourceAssets,
            ConfirmedSelections: selected,
            RequireAbsolutePlacement: request.Production is not null,
            InstanceIdentity: new TemplateRenderInstanceIdentity(
                instance.PageInstanceId, instance.SnapshotId, instance.RuleDigest, instance.InputFactsDigest,
                request.Snapshot.ProjectionDigest, instance.SourcePageId, instance.BranchId, instance.RecipeId,
                instance.RepetitionIndex, instance.Order, instance.Slots, frames));

        var json = new TemplateRenderPlanBuilder(validator).Build(
            request.Revision, page, request.TemplateResource, requestModel);
        using var document = JsonDocument.Parse(json);
        var planDigest = document.RootElement.GetProperty("plan_digest").GetString()
            ?? throw Invalid("TPL_INSTANCE_RENDER_PLAN_INVALID");
        return new TemplateInstanceRenderPlanResult(instance.PageInstanceId,
            document.RootElement.GetProperty("plan_id").GetString()!, planDigest, json);
    }

    private static bool IsSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool HasValidResourceOrigin(TemplateInstanceRenderPlanRequest request) =>
        request.TemplateResource.DraftId == request.Revision.RevisionId
        || request.ResourceOrigin is { State: TemplateRevisionState.Committed } origin
            && origin.RevisionId == request.TemplateResource.DraftId && origin.GroupId == request.Revision.GroupId
            && origin.RevisionNo < request.Revision.RevisionNo
            && origin.Pages.Any(page => page.TemplateResourceId == request.TemplateResource.ResourceId);

    private static string Hash<T>(T value) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static TemplateRuleViolationException Invalid(string code) => new(code);
}
