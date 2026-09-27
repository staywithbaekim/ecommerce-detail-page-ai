using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.Rendering;

/// <summary>Prepares one calibrated insertion candidate. Only the coordinator may authorize its execution.</summary>
public sealed class SizeTablePageRepairPreparer(
    IBatchRepository batches,
    ITemplateRepository templates,
    ITemplateResourceRepository templateResources,
    ISizeTableRepository tables,
    IAssetMetadataReader sources,
    ISizeTableRasterizer rasterizer,
    ISizeTableRenderPlanBuilder plans,
    IRenderPlanValidator validator,
    IFileStore files) : ISizeTablePageRepairPreparer
{
    public async Task<SizeTable> LoadBaseAsync(RenderBatchJournal journal, RenderBatchPage page,
        CancellationToken cancellationToken) =>
        (await LoadContextAsync(journal, page, cancellationToken).ConfigureAwait(false)).Table;

    public async Task<PreparedSizeTableRepair> PrepareAsync(SizeTablePageRepairCommand command,
        RenderBatchJournal journal, RenderBatchPage page, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestDigest = SizeTableRepairData.RequestDigest(command);
        var target = command.Target;
        RequirePage(journal, page);
        using var original = JsonDocument.Parse(page.PlanJson!);
        var before = original.RootElement;
        if (target.BatchId != journal.BatchId || target.StyleId != page.StyleId || target.PageId != page.PageId
            || target.OutputVariantId != page.OutputVariantId || target.ExpectedJournalRevision != journal.Revision
            || target.ExpectedAttempt != page.Attempt || target.BasePlanDigest != Text(before, "plan_digest"))
            throw Invalid("SIZE_REPAIR_REVISION_CONFLICT");
        if (createdAt < before.GetProperty("created_at").GetDateTimeOffset()) throw Invalid("SIZE_REPAIR_TIMESTAMP_INVALID");
        var context = await LoadContextAsync(journal, page, cancellationToken).ConfigureAwait(false);
        var baseline = context.Table;
        if (target.ExpectedTableRevision != baseline.Revision || target.ExpectedTableDataDigest != baseline.DataDigest)
            throw Invalid("SIZE_REPAIR_REVISION_CONFLICT");
        var edited = SizeTableRepairData.ApplyDraft(baseline, command.Draft, createdAt);
        if (!edited.CanSave || edited.DataDigest == baseline.DataDigest) throw Invalid("SIZE_REPAIR_CONTENT_CHANGE_REQUIRED");
        var frozenDraft = SizeTableRepairData.ToEditorDocument(edited);
        if (SizeTableRepairData.RequestDigest(command with { Draft = new(frozenDraft.Columns, frozenDraft.Rows) }) != requestDigest)
            throw Invalid("SIZE_REPAIR_REQUEST_CONFLICT");
        var origin = new RenderSizeTableRepairOrigin(command.RequestId, requestDigest, journal.Revision, page.Attempt,
            target.BasePlanDigest, SizeTableRepairData.Serialize(baseline), SizeTableRepairData.Serialize(edited));

        var style = SizeTableCalibrationResolver.RasterStyle(context.Page.SizeTableCalibration!);
        var raster = await rasterizer.RasterizeAsync(new(edited, style), cancellationToken).ConfigureAwait(false);
        if (raster.OwnedRelativePath != ResourcePath(edited, raster.ResourceId)
            || raster.TableDataDigest != edited.DataDigest || raster.StyleVersion != style.StyleVersion
            || raster.LayoutVersion != style.LayoutVersion)
            throw Invalid("SIZE_REPAIR_RESOURCE_MISMATCH");
        await VerifyRasterAsync(raster, cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N");
        var request = Request(before, edited, raster, context.Page) with
        {
            PlanId = "plan_" + identity,
            IdempotencyKey = "size_repair_" + identity,
            ArtifactSetId = "artifact_" + identity,
            CreatedAt = createdAt
        };
        var candidate = plans.Build(context.Revision, context.Page, context.Resource, request);
        if (!validator.Validate(candidate).IsValid || !RenderTemplateEvidenceValidator.Matches(candidate, context.Resource.Inspection))
            throw Invalid("SIZE_REPAIR_PLAN_INVALID");
        RenderPlanSizeTableRepair.Validate(page.PlanJson!, candidate, origin);
        // Source evidence is checked again after asynchronous rasterization; never accept a changed source silently.
        await VerifySourceAsync(context.Batch, edited, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(candidate, origin);
    }

    private async Task<RepairContext> LoadContextAsync(RenderBatchJournal journal, RenderBatchPage page,
        CancellationToken cancellationToken)
    {
        RequirePage(journal, page);
        if (!validator.Validate(page.PlanJson!).IsValid) throw Invalid("SIZE_REPAIR_PLAN_INVALID");
        using var document = JsonDocument.Parse(page.PlanJson!);
        var plan = document.RootElement;
        var planDocument = plan.GetProperty("document");
        if (Text(plan, "batch_id") != journal.BatchId || Text(plan, "product_id") != page.StyleId
            || Text(planDocument, "page_id") != page.PageId || RenderPlanVariantIdentity.Read(plan) != page.OutputVariantId
            || Text(planDocument, "content_type") != "dynamic_size_table" || plan.GetProperty("targets").GetArrayLength() != 0
            || !RenderPlanPlacementMigration.HasSizeInsertion(plan)) throw Invalid("SIZE_REPAIR_UNSUPPORTED_PAGE");

        var batch = await batches.GetAsync(journal.BatchId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("SIZE_REPAIR_BATCH_NOT_FOUND");
        var snapshot = batch.TemplateSnapshot;
        var revision = await templates.GetRevisionAsync(snapshot.RevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");
        var frozen = snapshot.Pages.SingleOrDefault(item => item.PageId == page.PageId);
        var templatePage = revision.Pages.SingleOrDefault(item => item.PageId == page.PageId);
        if (batch.BatchId != journal.BatchId || batch.ScanStatus != BatchScanStatus.ScanReady
            || snapshot.BatchId != batch.BatchId || revision.State != TemplateRevisionState.Committed
            || revision.RevisionId != snapshot.RevisionId || snapshot.RulesDigest != revision.RuleDigest
            || Text(plan, "template_revision_id") != revision.RevisionId || frozen is null || templatePage is null
            || !templatePage.Enabled || templatePage.SizeTableCalibration is null
            || templatePage.ContentType != TemplatePageContentType.DynamicSizeTable
            || templatePage.RevisionId != revision.RevisionId || templatePage.Order != frozen.Order
            || templatePage.ContentType != frozen.ContentType || templatePage.GenerationMode != frozen.GenerationMode
            || templatePage.VariantCapacity != frozen.VariantCapacity || templatePage.TemplateResourceId != frozen.TemplateResourceId
            || !frozen.FrameIds.SequenceEqual(revision.FrameBindings.Where(binding => binding.PageId == page.PageId)
                .Select(binding => binding.FrameId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");
        var resource = await templateResources.GetByResourceIdAsync(frozen.TemplateResourceId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");
        if (resource.ResourceId != frozen.TemplateResourceId || resource.GroupId != revision.GroupId
            || Text(planDocument, "template_resource_id") != resource.ResourceId
            || resource.Inspection is null || resource.Length <= 0
            || !SizeTableCalibrationResolver.Matches(templatePage.SizeTableCalibration, resource.Inspection)
            || !RenderTemplateEvidenceValidator.Matches(page.PlanJson!, resource.Inspection)
            || resource.Length != resource.Inspection.SizeBytes || !SameHash(resource.Sha256, resource.Inspection.FileSha256)
            || !SameHash(resource.StructureSignature, resource.Inspection.StructureSignature))
            throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");
        if (resource.DraftId != revision.RevisionId)
        {
            var owner = await templates.GetRevisionAsync(resource.DraftId, cancellationToken).ConfigureAwait(false);
            if (owner is not { State: TemplateRevisionState.Committed } || owner.RevisionId != resource.DraftId
                || owner.GroupId != revision.GroupId || owner.RevisionNo >= revision.RevisionNo
                || !owner.Pages.Any(item => item.TemplateResourceId == resource.ResourceId))
                throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");
        }
        if (!SameHash(await files.ComputeSha256Async(resource.InternalRelativePath, cancellationToken).ConfigureAwait(false), resource.Sha256))
            throw Invalid("SIZE_REPAIR_TEMPLATE_MISMATCH");

        // Regeneration has no new repair origin: walk this exact logical page's ancestors, never another variant.
        var ancestor = journal.Pages.Where(item => RenderBatchJournalValidator.SamePage(item, page)
            && item.Attempt <= page.Attempt && item.SizeTableRepair is not null).MaxBy(item => item.Attempt);
        SizeTable table;
        if (ancestor?.SizeTableRepair is { } repair)
        {
            table = SizeTableRepairData.Deserialize(repair.TableJson);
            if (ancestor.PlanJson is null || !SizeTableRepairData.MatchesPlan(table, ancestor.PlanJson))
                throw Invalid("SIZE_REPAIR_BASELINE_MISMATCH");
        }
        else
        {
            var loaded = await tables.LoadAsync(batch.BatchId, page.StyleId, page.PageId, cancellationToken).ConfigureAwait(false);
            if (loaded.Status != SizeTableLoadStatus.Loaded || loaded.Table is null) throw Invalid("SIZE_REPAIR_BASELINE_MISMATCH");
            table = loaded.Table;
        }
        if (!table.CanSave || table.BatchId != batch.BatchId || table.StyleId != page.StyleId || table.PageId != page.PageId
            || !SizeTableRepairData.MatchesPlan(table, page.PlanJson!)) throw Invalid("SIZE_REPAIR_BASELINE_MISMATCH");
        await VerifySourceAsync(batch, table, cancellationToken).ConfigureAwait(false);

        var size = plan.GetProperty("size_table");
        var rasterId = Text(size, "render_resource_id");
        var rasterResource = plan.GetProperty("resources").EnumerateArray().Single(item => Text(item, "resource_id") == rasterId);
        var placement = size.GetProperty("insertion").GetProperty("placement");
        var raster = new SizeTableRasterizedResource(rasterId, Text(rasterResource, "role"), Text(rasterResource, "media_type"),
            Text(rasterResource, "sha256"), rasterResource.GetProperty("byte_length").GetInt64(),
            placement.GetProperty("source_width").GetInt32(), placement.GetProperty("source_height").GetInt32(),
            table.DataDigest, Text(size, "style_version"), Text(size, "layout_version"), ResourcePath(table, rasterId));
        var style = SizeTableCalibrationResolver.RasterStyle(templatePage.SizeTableCalibration);
        if (raster.StyleVersion != style.StyleVersion || raster.LayoutVersion != style.LayoutVersion)
            throw Invalid("SIZE_REPAIR_BASELINE_MISMATCH");
        await VerifyRasterAsync(raster, cancellationToken).ConfigureAwait(false);
        var rebuilt = plans.Build(revision, templatePage, resource, Request(plan, table, raster, templatePage));
        using var closed = JsonDocument.Parse(rebuilt);
        if (!JsonElement.DeepEquals(plan, closed.RootElement)) throw Invalid("SIZE_REPAIR_BASELINE_MISMATCH");
        return new(batch, revision, templatePage, resource, table);
    }

    private async Task VerifySourceAsync(BatchDraft batch, SizeTable table, CancellationToken cancellationToken)
    {
        var style = batch.Styles.SingleOrDefault(item => item.StyleId == table.StyleId);
        var asset = batch.Assets.SingleOrDefault(item => item.AssetId == table.Source.AssetId);
        if (style is null || asset is null || asset.StyleId != style.StyleId || !style.AssetIds.Contains(asset.AssetId)
            || asset.Readability != AssetReadability.Readable || !SameHash(table.Source.ContentDigest, asset.Sha256))
            throw Invalid("SIZE_REPAIR_SOURCE_MISMATCH");
        // The existing input port resolves and protects the source; no source path is opened in Application.
        var read = await sources.ReadAsync(batch.InputRoot, asset.SourceRelativePath, cancellationToken).ConfigureAwait(false);
        if (read.Metadata is not { } actual || actual.Length != asset.Length || !SameHash(actual.Sha256, asset.Sha256)
            || actual.PixelWidth != asset.PixelWidth || actual.PixelHeight != asset.PixelHeight
            || actual.Format != asset.Format || actual.MimeType != asset.MimeType)
            throw Invalid("SIZE_REPAIR_SOURCE_MISMATCH");
    }

    private async Task VerifyRasterAsync(SizeTableRasterizedResource raster, CancellationToken cancellationToken)
    {
        var content = await files.ReadAsync(raster.OwnedRelativePath, cancellationToken).ConfigureAwait(false);
        if (content is null || content.LongLength != raster.ByteLength || content.Length < 24
            || !SameHash(Convert.ToHexString(SHA256.HashData(content)), raster.Sha256)
            || content[0] != 137 || !content.AsSpan(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8)
            || !content.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(16, 4)) != raster.PixelWidth
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4)) != raster.PixelHeight)
            throw Invalid("SIZE_REPAIR_RESOURCE_MISMATCH");
    }

    private static SizeTableRenderPlanRequest Request(JsonElement plan, SizeTable table,
        SizeTableRasterizedResource raster, TemplatePage page)
    {
        var output = plan.GetProperty("output");
        var psd = output.GetProperty("editable_psd");
        return new(Text(plan, "plan_id"), Text(plan, "idempotency_key"), Text(plan, "correlation_id"), table.BatchId, table.StyleId,
            Text(output, "artifact_set_id"), Text(output.GetProperty("jpg"), "file_name"), psd.GetProperty("enabled").GetBoolean(),
            psd.GetProperty("file_name").GetString(), plan.GetProperty("created_at").GetDateTimeOffset(), table, raster,
            CalibratedInsertion: SizeTableCalibrationResolver.Resolve(page, raster), OutputVariantId: RenderPlanVariantIdentity.Read(plan));
    }

    private static void RequirePage(RenderBatchJournal journal, RenderBatchPage page)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(page);
        if (journal.Pages.Count(item => RenderBatchJournalValidator.SamePage(item, page) && item.Attempt == page.Attempt) != 1
            || !journal.Pages.Contains(page) || !RenderBatchJournalValidator.CanRegenerate(journal, page))
            throw Invalid("SIZE_REPAIR_NOT_ALLOWED");
    }

    private static string ResourcePath(SizeTable table, string resourceId)
    {
        if (resourceId.Length != 36 || !resourceId.StartsWith("size_raster_", StringComparison.Ordinal)
            || resourceId.Skip(12).Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw Invalid("SIZE_REPAIR_RESOURCE_MISMATCH");
        return $"batches/{table.BatchId}/size-tables/{table.StyleId}/{table.PageId}/resources/{resourceId}.png";
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
    private static bool SameHash(string? left, string right) => left is not null
        && string.Equals(left.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? left[7..] : left,
            right.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? right[7..] : right, StringComparison.OrdinalIgnoreCase);
    private static InvalidOperationException Invalid(string code) => new(code);
    private sealed record RepairContext(BatchDraft Batch, TemplateRevision Revision, TemplatePage Page,
        ImportedTemplateResource Resource, SizeTable Table);
}
