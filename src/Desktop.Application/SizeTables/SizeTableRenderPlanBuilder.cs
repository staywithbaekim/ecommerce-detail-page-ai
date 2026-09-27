using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.SizeTables;

public sealed record SizeTableRenderPlanRequest(
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
    SizeTable Table,
    SizeTableRasterizedResource RasterizedResource,
    SizeTableRenderTarget? CalibratedTarget = null,
    SizeTableInsertionTarget? CalibratedInsertion = null,
    string? OutputVariantId = null);

/// <summary>An explicit size-table binding, never inferred from a product binding or the only smart object.</summary>
public sealed record SizeTableRenderTarget(
    string TemplateRevisionId,
    string TemplateSha256,
    FrameBinding Binding,
    RenderTargetPlacement Placement);

public sealed record SizeTableLayerReference(IReadOnlyList<int> LayerPath, int LayerId,
    string Kind, string Name, string StructureSignature)
{
    public string LayerPathOrder { get; } = "psd_bottom_to_top";
}

public sealed record SizeTableInsertion(string LayerName, SizeTableLayerReference AboveLayer,
    IReadOnlyList<SizeTableLayerReference> HiddenLayers, TemplateRect SafeBounds, RenderTargetPlacement Placement);

public sealed record SizeTableInsertionTarget(string TemplateRevisionId, string TemplateSha256,
    string PageId, SizeTableInsertion Insertion);

public interface ISizeTableRenderPlanBuilder
{
    string Build(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        SizeTableRenderPlanRequest request);
}

public sealed class SizeTableRenderPlanBuilder(IRenderPlanValidator validator) : ISizeTableRenderPlanBuilder
{
    private static readonly string[] BaseCapabilities = ["psd-layer-path/psd-bottom-to-top", "smart-object/replace", "output/jpeg-srgb-icc"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public string Build(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        SizeTableRenderPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(templateResource);
        ArgumentNullException.ThrowIfNull(request);
        Validate(revision, page, templateResource, request);

        var capabilities = BaseCapabilities.AsEnumerable();
        if (request.CalibratedTarget is not null) capabilities = capabilities
            .Append(RenderPlanPlacementMigration.Capability).Append(RenderPlanPlacementMigration.SizeTableCapability);
        var targets = request.CalibratedTarget is { } calibrated
            ? new[] { CreateTarget(calibrated, request.RasterizedResource.ResourceId) } : Array.Empty<object>();

        object CreateDocument(string planDigest) => new
        {
            schema_version = "render-plan/v1",
            plan_id = request.PlanId,
            idempotency_key = request.IdempotencyKey,
            correlation_id = request.CorrelationId,
            batch_id = request.BatchId,
            product_id = request.ProductId,
            template_revision_id = revision.RevisionId,
            plan_digest = planDigest,
            created_at = request.CreatedAt,
            required_capabilities = capabilities.ToArray(),
            resources = new object[]
            {
                new
                {
                    resource_id = templateResource.ResourceId,
                    role = "template_copy",
                    media_type = "image/vnd.adobe.photoshop",
                    sha256 = templateResource.Sha256,
                    byte_length = templateResource.Length
                },
                new
                {
                    resource_id = request.RasterizedResource.ResourceId,
                    role = "deterministic_size_table",
                    media_type = "image/png",
                    sha256 = request.RasterizedResource.Sha256,
                    byte_length = request.RasterizedResource.ByteLength
                }
            },
            document = new
            {
                template_resource_id = templateResource.ResourceId,
                page_id = page.PageId,
                content_type = "dynamic_size_table",
                canvas = new
                {
                    width = templateResource.Inspection.Canvas.Width,
                    height = templateResource.Inspection.Canvas.Height,
                    color_mode = "RGB",
                    bits = 8
                }
            },
            targets,
            size_table = new
            {
                size_table_id = request.Table.TableId,
                grid_digest = "sha256:" + request.RasterizedResource.Sha256,
                style_version = request.RasterizedResource.StyleVersion,
                layout_version = request.RasterizedResource.LayoutVersion,
                columns = request.Table.Columns.Select((column, index) => new
                {
                    column_id = column.ColumnId,
                    label = column.SourceText,
                    index
                }).ToArray(),
                rows = request.Table.Rows.Select((row, index) => new
                {
                    row_id = row.RowId,
                    index,
                    cells = row.Cells.ToDictionary(
                        cell => cell.ColumnId,
                        cell => cell.SourceText,
                        StringComparer.Ordinal)
                }).ToArray(),
                render_resource_id = request.RasterizedResource.ResourceId,
                structured_data_digest = request.Table.DataDigest,
                embed_as = "smart_object"
            },
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

        var preimage = JsonSerializer.Serialize(
            CreateDocument("sha256:" + new string('0', 64)),
            JsonOptions);
        var digest = RenderPlanDigest.Compute(preimage);
        var json = JsonSerializer.Serialize(CreateDocument(digest), JsonOptions);
        if (request.CalibratedInsertion is { } insertion)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            node["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.Capability);
            node["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.SizeInsertionCapability);
            node["size_table"]!["insertion"] = JsonSerializer.SerializeToNode(insertion.Insertion, JsonOptions);
            node["plan_digest"] = RenderPlanDigest.Compute(node.ToJsonString());
            json = node.ToJsonString();
        }
        json = RenderPlanVariantIdentity.AddToNewPlan(json, request.OutputVariantId);
        var validation = validator.Validate(json);
        if (!validation.IsValid)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_PLAN_SCHEMA_REJECTED");
        }
        if ((request.CalibratedTarget is not null || request.CalibratedInsertion is not null)
            && !RenderTemplateEvidenceValidator.Matches(json, templateResource.Inspection))
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_TARGET_INVALID");
        return json;
    }

    internal static object CreateTarget(SizeTableRenderTarget calibrated, string resourceId)
    {
        var binding = calibrated.Binding;
        return new
        {
            frame_id = binding.FrameId,
            layer_path = binding.LayerPath.Segments,
            layer_path_order = binding.LayerPath.Order,
            expected_layer = new
            {
                layer_id = binding.ExpectedLayer.LayerId, kind = "smartObject",
                name_before = binding.ExpectedLayer.NameBefore, visible = binding.ExpectedLayer.Visible,
                clipping = binding.ExpectedLayer.Clipping, user_mask = binding.ExpectedLayer.UserMask,
                vector_mask = binding.ExpectedLayer.VectorMask, opacity = binding.ExpectedLayer.Opacity,
                blend_mode = binding.ExpectedLayer.BlendMode,
                structure_signature = binding.ExpectedLayer.StructureSignature.Value
            },
            superseded_layers = binding.SupersededLayers.Select(layer => new
            {
                layer_path = layer.LayerPath.Segments, layer_path_order = layer.LayerPath.Order,
                expected_kind = "smartObject", expected_visible = layer.ExpectedVisible,
                expected_clipping = layer.ExpectedClipping, expected_layer_id = layer.ExpectedLayerId
            }).ToArray(),
            asset_resource_id = resourceId,
            layout = new
            {
                intent = "size_table_complete", scale = 1, offset = new { x = 0, y = 0 },
                safe_bounds = binding.LayoutIntent.SafeBounds,
                placement = calibrated.Placement
            }
        };
    }

    private static void Validate(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        SizeTableRenderPlanRequest request)
    {
        var table = request.Table;
        var raster = request.RasterizedResource;
        if (request.CalibratedInsertion is { } insertion && (request.CalibratedTarget is not null
            || insertion.TemplateRevisionId != revision.RevisionId || insertion.PageId != page.PageId
            || !string.Equals(insertion.TemplateSha256, templateResource.Sha256, StringComparison.OrdinalIgnoreCase)
            || insertion.Insertion.Placement.SourceWidth != raster.PixelWidth
            || insertion.Insertion.Placement.SourceHeight != raster.PixelHeight))
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_TARGET_INVALID");
        if (request.CalibratedTarget is { } calibrated
            && (calibrated.TemplateRevisionId != revision.RevisionId
                || !string.Equals(calibrated.TemplateSha256, templateResource.Sha256, StringComparison.OrdinalIgnoreCase)
                || calibrated.Binding.PageId != page.PageId
                || calibrated.Placement.SourceWidth != raster.PixelWidth
                || calibrated.Placement.SourceHeight != raster.PixelHeight))
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_TARGET_INVALID");
        if (revision.State != TemplateRevisionState.Committed
            || page.ContentType != TemplatePageContentType.DynamicSizeTable
            || !revision.Pages.Any(candidate => string.Equals(candidate.PageId, page.PageId, StringComparison.Ordinal))
            || !string.Equals(page.TemplateResourceId, templateResource.ResourceId, StringComparison.Ordinal)
            || !table.CanSave
            || !string.Equals(table.BatchId, request.BatchId, StringComparison.Ordinal)
            || !string.Equals(table.StyleId, request.ProductId, StringComparison.Ordinal)
            || !string.Equals(table.PageId, page.PageId, StringComparison.Ordinal)
            || !string.Equals(raster.Role, "deterministic_size_table", StringComparison.Ordinal)
            || !string.Equals(raster.MediaType, "image/png", StringComparison.Ordinal)
            || !string.Equals(raster.TableDataDigest, table.DataDigest, StringComparison.Ordinal)
            || raster.ByteLength <= 0
            || raster.PixelWidth <= 0
            || raster.PixelHeight <= 0
            || raster.Dpi != 96
            || !string.Equals(raster.RasterizerVersion, "wpf-size-table-rasterizer/v1", StringComparison.Ordinal)
            || raster.Sha256.Length != 64
            || raster.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_PLAN_INPUT_INVALID");
        }
    }
}
