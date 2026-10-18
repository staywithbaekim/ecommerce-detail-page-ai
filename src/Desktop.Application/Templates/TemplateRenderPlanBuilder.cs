using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed record TemplateRenderSource(
    string ResourceId,
    string Sha256,
    long Length,
    string MediaType = "image/jpeg",
    int PixelWidth = 0,
    int PixelHeight = 0);

public sealed record TemplateRenderSubjectGeometry(
    string AssetResourceId,
    decimal Left,
    decimal Top,
    decimal Right,
    decimal Bottom,
    decimal Confidence)
{
    public bool IsValid => Left >= 0m && Top >= 0m
        && Right > Left && Right <= 1m
        && Bottom > Top && Bottom <= 1m
        && Confidence is >= 0m and <= 1m;
}

public sealed record TemplateRenderSelection(
    string FrameId,
    string AssetResourceId,
    TemplateRenderSubjectGeometry? SubjectGeometry);

public sealed record TemplateRenderInstanceIdentity(
    string PageInstanceId,
    string SnapshotId,
    string RuleDigest,
    string InputFactsDigest,
    string ProjectionDigest,
    string SourcePageId,
    string BranchId,
    string RecipeId,
    int RepetitionIndex,
    int StableOrder,
    IReadOnlyList<TemplateRuleColorAssignment> Slots,
    IReadOnlyList<TemplateInstanceFrameProjection> Frames);

public sealed record TemplateRenderPlanRequest(
    string PlanId,
    string IdempotencyKey,
    string CorrelationId,
    string BatchId,
    string ProductId,
    string ArtifactSetId,
    string JpgFileName,
    bool IncludeEditablePsd,
    string? PsdFileName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TemplateRenderSource> Sources,
    IReadOnlyList<TemplateRenderSelection>? ConfirmedSelections = null,
    bool RequireAbsolutePlacement = false,
    string? OutputVariantId = null,
    TemplateRenderInstanceIdentity? InstanceIdentity = null);

public interface ITemplateRenderPlanBuilder
{
    string Build(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        TemplateRenderPlanRequest request);
}

public sealed class TemplateRenderPlanBuilder(IRenderPlanValidator validator) : ITemplateRenderPlanBuilder
{
    private static readonly string[] BaseCapabilities = ["psd-layer-path/psd-bottom-to-top", "smart-object/replace", "output/jpeg-srgb-icc"];
    private static readonly string[] InstanceIdentityCapabilities = ["template/page-instance-identity-v1"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public string Build(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        TemplateRenderPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(templateResource);
        ArgumentNullException.ThrowIfNull(request);
        if (revision.State != TemplateRevisionState.Committed
            || !revision.Pages.Any(candidate => string.Equals(candidate.PageId, page.PageId, StringComparison.Ordinal))
            || !string.Equals(page.TemplateResourceId, templateResource.ResourceId, StringComparison.Ordinal)
            || page.ContentType != TemplatePageContentType.NormalComposition)
        {
            throw new TemplateRuleViolationException("TPL_RENDER_PLAN_INPUT_INVALID");
        }

        var sources = request.Sources.ToDictionary(source => source.ResourceId, StringComparer.Ordinal);
        var bindings = revision.ActiveFrameBindings.Where(binding => string.Equals(
            binding.PageId,
            page.PageId,
            StringComparison.Ordinal)).ToArray();
        var hasRasterTargets = bindings.Any(binding => binding.ExpectedLayer.Kind == TemplateLayerKind.Pixel);
        if (hasRasterTargets && !request.RequireAbsolutePlacement)
            throw new TemplateRuleViolationException("TPL_RENDER_GEOMETRY_INVALID");
        if (bindings.Length == 0)
        {
            throw new TemplateRuleViolationException("TPL_FRAME_EVIDENCE_INCOMPLETE");
        }

        var explicitSelections = request.ConfirmedSelections?.ToDictionary(
            selection => selection.FrameId,
            StringComparer.Ordinal);
        var requiresExplicitTargetSelections = bindings.Any(binding =>
            binding.SelectionRequirement.Criteria.Any(criterion =>
                string.Equals(criterion.Key, "reference_only", StringComparison.Ordinal)
                && string.Equals(criterion.Value, "true", StringComparison.Ordinal)));
        if ((requiresExplicitTargetSelections && explicitSelections is null)
            || explicitSelections is not null
            && (explicitSelections.Count != bindings.Length
                || bindings.Any(binding => !explicitSelections.ContainsKey(binding.FrameId))))
        {
            throw new TemplateRuleViolationException("TPL_RENDER_SELECTIONS_INCOMPLETE");
        }

        var targets = bindings.Select(binding =>
        {
            var explicitSelection = explicitSelections?.GetValueOrDefault(binding.FrameId);
            var assetId = explicitSelection?.AssetResourceId
                ?? binding.SelectionRequirement.Criteria.SingleOrDefault(criterion =>
                    string.Equals(criterion.Key, "asset_id", StringComparison.Ordinal))?.Value
                ?? throw new TemplateRuleViolationException("TPL_CALIBRATION_INCOMPLETE");
            if (!sources.TryGetValue(assetId, out var source))
            {
                throw new TemplateRuleViolationException("TPL_RENDER_SOURCE_MISSING");
            }

            if (explicitSelection?.SubjectGeometry is { } geometry
                && (!geometry.IsValid
                    || !string.Equals(
                        geometry.AssetResourceId,
                        assetId,
                        StringComparison.Ordinal)))
            {
                throw new TemplateRuleViolationException("TPL_RENDER_GEOMETRY_INVALID");
            }

            var frameBounds = TemplatePlacementResolver.ResolveFrameBounds(binding, templateResource.Inspection);
            var layout = new Dictionary<string, object>
            {
                ["intent"] = binding.LayoutIntent.Intent,
                ["scale"] = binding.LayoutIntent.Scale,
                ["offset"] = new { x = binding.LayoutIntent.OffsetX, y = binding.LayoutIntent.OffsetY },
                ["safe_bounds"] = new
                {
                    left = frameBounds.Left, top = frameBounds.Top,
                    right = frameBounds.Right, bottom = frameBounds.Bottom
                }
            };
            if (request.RequireAbsolutePlacement)
                layout["placement"] = TemplatePlacementResolver.Resolve(binding, source,
                    explicitSelection?.SubjectGeometry, templateResource.Inspection);
            return new
            {
                frame_id = binding.FrameId,
                layer_path = binding.LayerPath.Segments,
                layer_path_order = binding.LayerPath.Order,
                expected_layer = new
                {
                    layer_id = binding.ExpectedLayer.LayerId,
                    kind = binding.ExpectedLayer.Kind == TemplateLayerKind.Pixel ? "pixel" : "smartObject",
                    name_before = binding.ExpectedLayer.NameBefore,
                    visible = binding.ExpectedLayer.Visible,
                    clipping = binding.ExpectedLayer.Clipping,
                    user_mask = binding.ExpectedLayer.UserMask,
                    vector_mask = binding.ExpectedLayer.VectorMask,
                    opacity = binding.ExpectedLayer.Opacity,
                    blend_mode = binding.ExpectedLayer.BlendMode,
                    structure_signature = binding.ExpectedLayer.StructureSignature.Value
                },
                // Keep lower PSD layers intact. Replacement coverage comes from
                // the placement bleed, not visibility changes to the source stack.
                superseded_layers = Array.Empty<object>(),
                asset_resource_id = assetId,
                layout
            };
        }).ToArray();

        object CreateDocument(string digest) => new
        {
            schema_version = "render-plan/v1",
            plan_id = request.PlanId,
            idempotency_key = request.IdempotencyKey,
            correlation_id = request.CorrelationId,
            batch_id = request.BatchId,
            product_id = request.ProductId,
            template_revision_id = revision.RevisionId,
            plan_digest = digest,
            created_at = request.CreatedAt,
            required_capabilities = BaseCapabilities
                .Concat(request.RequireAbsolutePlacement ? new[] { RenderPlanPlacementMigration.Capability } : [])
                .Concat(request.InstanceIdentity is null ? [] : InstanceIdentityCapabilities)
                .Concat(hasRasterTargets ? new[] { RenderTemplateEvidenceValidator.RasterReplacementCapability } : [])
                .Order(StringComparer.Ordinal).ToArray(),
            resources = new object[]
            {
                new
                {
                    resource_id = templateResource.ResourceId,
                    role = "template_copy",
                    media_type = "image/vnd.adobe.photoshop",
                    sha256 = templateResource.Sha256,
                    byte_length = templateResource.Length
                }
            }.Concat(request.Sources.Select(source => (object)new
            {
                resource_id = source.ResourceId,
                role = "high_resolution_render_source",
                media_type = source.MediaType,
                sha256 = source.Sha256,
                byte_length = source.Length
            })).ToArray(),
            document = new
            {
                template_resource_id = templateResource.ResourceId,
                page_id = page.PageId,
                content_type = "ordinary_set",
                instance_identity = request.InstanceIdentity is null ? null : new
                {
                    page_instance_id = request.InstanceIdentity.PageInstanceId,
                    snapshot_id = request.InstanceIdentity.SnapshotId,
                    rule_digest = request.InstanceIdentity.RuleDigest,
                    input_facts_digest = request.InstanceIdentity.InputFactsDigest,
                    projection_digest = request.InstanceIdentity.ProjectionDigest,
                    source_page_id = request.InstanceIdentity.SourcePageId,
                    branch_id = request.InstanceIdentity.BranchId,
                    recipe_id = request.InstanceIdentity.RecipeId,
                    repetition_index = request.InstanceIdentity.RepetitionIndex,
                    stable_order = request.InstanceIdentity.StableOrder,
                    slots = request.InstanceIdentity.Slots.Select(slot => new { slot_id = slot.SlotId, variant_id = slot.VariantId }),
                    frames = request.InstanceIdentity.Frames.Select(frame => new { frame_id = frame.FrameId, slot_id = frame.SlotId, variant_id = frame.VariantId, asset_id = frame.AssetId })
                },
                canvas = new
                {
                    width = templateResource.Inspection.Canvas.Width,
                    height = templateResource.Inspection.Canvas.Height,
                    color_mode = "RGB",
                    bits = 8
                }
            },
            targets,
            size_table = (object?)null,
            output = new
            {
                artifact_set_id = request.ArtifactSetId,
                overwrite_policy = "never",
                jpg = new
                {
                    enabled = true,
                    file_name = request.JpgFileName,
                    color_profile = "sRGB IEC61966-2.1",
                    rendering_intent = "relative_colorimetric",
                    black_point_compensation = true,
                    dither = true,
                    embed_profile = true
                },
                editable_psd = new
                {
                    enabled = request.IncludeEditablePsd,
                    file_name = request.IncludeEditablePsd ? request.PsdFileName : null
                },
                atomic_page_commit = true
            }
        };

        static string SerializeDocument(object document)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(document, JsonOptions))!;
            if (node["document"]!["instance_identity"] is null)
                node["document"]!.AsObject().Remove("instance_identity");
            return node.ToJsonString();
        }

        var digestPreimage = SerializeDocument(
            CreateDocument("sha256:" + new string('0', 64)));
        var digest = RenderPlanDigest.Compute(digestPreimage);
        var json = SerializeDocument(CreateDocument(digest));
        json = RenderPlanVariantIdentity.AddToNewPlan(json, request.OutputVariantId);
        var validation = validator.Validate(json);
        if (!validation.IsValid)
        {
            throw new TemplateRuleViolationException("TPL_RENDER_PLAN_SCHEMA_REJECTED");
        }
        return json;
    }
}
