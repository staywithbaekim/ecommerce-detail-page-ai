using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateInstanceRenderBatchIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedE1BodyAndOppositeSqlMemberOrdersProduceIdenticalPlansWithoutExecutionWrites(bool missingBlue)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var (projectionRequest, resource) = Inputs(missingBlue);
        var snapshot = TemplateInstanceProjectionSnapshotCodec.Freeze(projectionRequest,
            TemplateInstanceProjector.Project(projectionRequest));
        var planner = new TemplateInstanceRenderBatchPlanner(new RenderPlanValidator(new JsonSchemaContractValidator()));
        var before = Build(snapshot, snapshot.Instances.Select(item => item.PageInstanceId));
        Assert.True(before.CanContinue);
        Assert.True(before.HasCompleteMembership);
        Assert.Equal(4, before.Members.Length);
        Assert.Equal(missingBlue ? TemplateInstanceRenderBatchStatus.PartiallyReady : TemplateInstanceRenderBatchStatus.Ready, before.Status);
        Assert.Equal(missingBlue ? 2 : 4, before.Plans.Length);
        Assert.Equal(missingBlue ? 2 : 0, before.Members.Count(member => member.Status == TemplateInstanceBatchMemberStatus.BusinessBlocked));
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.FreezeAsync(new("freeze_render_batch", 0, snapshot), CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restored = (await reopened.ReadAsync("batch", "style", CancellationToken.None))!;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        var ascending = await Members(false);
        var descending = await Members(true);
        Assert.Equal(ascending.AsEnumerable().Reverse(), descending);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Build(restored, ascending)));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Build(restored, descending)));

        await using var counts = connection.CreateCommand();
        counts.CommandText = """
            SELECT (SELECT COUNT(*) FROM render_batch_journals)
                 + (SELECT COUNT(*) FROM render_page_checkpoints)
                 + (SELECT COUNT(*) FROM checkpoints)
                 + (SELECT COUNT(*) FROM render_output_registrations)
                 + (SELECT COUNT(*) FROM render_output_publications);
            """;
        Assert.Equal(0L, await counts.ExecuteScalarAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories), path =>
            path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));

        TemplateInstanceRenderBatchResult Build(TemplateInstanceProjectionSnapshot body, IEnumerable<string> ids) =>
            planner.Build(new("batch", "style", projectionRequest.Revision.RevisionId, ids.Select(id =>
                new TemplateInstanceRenderBatchInput(new(body, id, projectionRequest.Revision, resource,
                    projectionRequest.Facts.Assets), projectionRequest.Facts)).ToArray()));

        async Task<List<string>> Members(bool reverse)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = reverse
                ? "SELECT page_instance_id FROM template_instance_projection_members WHERE batch_id='batch' AND style_id='style' ORDER BY stable_order DESC;"
                : "SELECT page_instance_id FROM template_instance_projection_members WHERE batch_id='batch' AND style_id='style' ORDER BY stable_order ASC;";
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<string>();
            while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            return ids;
        }
    }

    private static (TemplateInstanceProjectionRequest Request, ImportedTemplateResource Resource) Inputs(bool missingBlue)
    {
        var page = TemplatePage.Create("page", "revision", 0, "page", TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "template");
        var binding = FrameBinding.Create("frame", "page", LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(257, "frame", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))), [],
            SelectionRequirement.Create("product", [SelectionCriterion.Create("reference_only", "true")]),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var recipe = new TemplatePageRecipe("recipe_z", "page", TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.All,
            [new("slot", TemplateRuleColorSource.Allocated)], [new("frame", "slot")]);
        var rules = new TemplateRuleSet(1, [new("branch", new(1), [recipe, recipe with { RecipeId = "recipe_a" }])]);
        var draft = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
            [page], [binding], new(1, rules, []));
        var revision = TemplateRevision.Restore("revision", "group", 1, TemplateRevisionState.Committed,
            "templates/revision.json", TemplateRuleCodec.BusinessDigest(draft), DateTimeOffset.UnixEpoch,
            draft.Pages, draft.FrameBindings, draft.RuleWorkspace);
        var red = SourceAssetMetadata.CreateReadable("red_asset", "style", "source/red.jpg", "red.jpg", 100,
            new string('b', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg");
        var blue = SourceAssetMetadata.CreateReadable("blue_asset", "style", "source/blue.jpg", "blue.jpg", 100,
            new string('e', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg");
        var facts = new TemplateInstanceInputFacts(1, new(["red", "blue"], "red", true), [red, blue],
            [new("red", [red.AssetId]), new("blue", [blue.AssetId])],
            missingBlue ? [new("frame", "red", red.AssetId)] : [new("frame", "red", red.AssetId), new("frame", "blue", blue.AssetId)], []);
        var resource = new ImportedTemplateResource("template", "group", "revision", "nonexistent/template.psd", 64,
            new string('c', 64), "sha256:" + new string('d', 64), new PsdInspectionDocument(new string('c', 64), 64,
                new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0, "sha256:" + new string('d', 64), new(1, 1, 1), 1024, null, []), DateTimeOffset.UnixEpoch);
        return (new(new("batch", "style", "snapshot", "revision", revision.RuleDigest!), revision, facts), resource);
    }
}
