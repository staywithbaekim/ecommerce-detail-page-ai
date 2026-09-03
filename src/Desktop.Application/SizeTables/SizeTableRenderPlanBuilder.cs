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
    SizeTableRasterizedResource RasterizedResource);

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
            required_capabilities = new[]
            {
                "psd-layer-path/psd-bottom-to-top",
                "smart-object/replace",
                "output/jpeg-srgb-icc"
            },
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
            targets = Array.Empty<object>(),
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
        var validation = validator.Validate(json);
        if (!validation.IsValid)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RENDER_PLAN_SCHEMA_REJECTED");
        }
        return json;
    }

    private static void Validate(
        TemplateRevision revision,
        TemplatePage page,
        ImportedTemplateResource templateResource,
        SizeTableRenderPlanRequest request)
    {
        var table = request.Table;
        var raster = request.RasterizedResource;
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
