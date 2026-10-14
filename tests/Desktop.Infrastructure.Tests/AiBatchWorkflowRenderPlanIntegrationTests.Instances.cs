using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Ai;
using Desktop.Application.Inputs;
using Desktop.Application.Files;
using Desktop.Application.SizeTables;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Tasks;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Infrastructure.Tests;

public sealed partial class AiBatchWorkflowRenderPlanIntegrationTests
{
    [Fact]
    public async Task FrozenInstanceSelectionReviewReusesProductionEvidenceWithoutReadingAiResults()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(new ScenarioProvider(request => InstanceResult(request, "healthy")));
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>();
        await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path);
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default);
        await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId,
            projection => projection.Status == AiBatchUiStatus.Ready);
        await workflow.PrepareRenderingAsync(batch.BatchId, default);

        var editing = Assert.IsAssignableFrom<ITemplateInstanceSelectionWorkflow>(workflow);
        var review = await editing.ReviewInstanceSelectionAsync(batch.BatchId, batch.Styles[0].StyleId,
            "frame_render_integration", batch.Assets[0].AssetId, default);

        Assert.NotNull(review);
    }

    [Fact]
    public async Task FrozenInstanceSelectionReviewDoesNotCallProviderAgain()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var provider = new ScenarioProvider(request => InstanceResult(request, "healthy"));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(provider);
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>();
        await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path);
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default);
        await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId,
            projection => projection.Status == AiBatchUiStatus.Ready);
        var callsBefore = provider.Calls;
        await workflow.PrepareRenderingAsync(batch.BatchId, default);
        var callsAfterPreparation = provider.Calls;

        var editing = Assert.IsAssignableFrom<ITemplateInstanceSelectionWorkflow>(workflow);
        var review = await editing.ReviewInstanceSelectionAsync(batch.BatchId, batch.Styles[0].StyleId,
            "frame_render_integration", batch.Assets[0].AssetId, default);

        Assert.NotNull(review);
        Assert.Equal(callsBefore, callsAfterPreparation);
        Assert.Equal(callsAfterPreparation, provider.Calls);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("low_confidence")]
    [InlineData("business_red")]
    [InlineData("all_red")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("failed_retry")]
    [InlineData("publication_failure")]
    [InlineData("database_confirmation_failure")]
    [InlineData("pause")]
    [InlineData("cancel")]
    [InlineData("all_failed")]
    [InlineData("freeze_transaction_failure")]
    public async Task E3FormalDiInputWorkflowCoordinatorCommitPublicationAndRecovery(string scenario)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var provider = new ScenarioProvider(request => InstanceResult(request, scenario));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(provider);
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>();
        await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path, zero: scenario == "empty");
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default);
        await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId,
            projection => projection.Status is AiBatchUiStatus.Ready or AiBatchUiStatus.NotGeneratable or AiBatchUiStatus.ReviewRequired);
        if (scenario == "freeze_transaction_failure")
        {
            await SqlE3(options, "CREATE TRIGGER e3_fail BEFORE INSERT ON template_instance_production_facts BEGIN SELECT RAISE(ABORT,'OFFLINE_FREEZE'); END;");
            await Assert.ThrowsAsync<SqliteException>(() => workflow.PrepareRenderingAsync(batch.BatchId, default));
            Assert.Null(await store.ReadProductionEvidenceAsync(batch.BatchId, batch.Styles[0].StyleId, default));
            Assert.Null(await store.ReadAsync(batch.BatchId, batch.Styles[0].StyleId, default));
            Assert.Null(await store.LoadRenderBatchAsync(batch.BatchId, default));
            Assert.Equal(0, container.GetRequiredService<OfflineInstanceRenderer>().Opens);
            await SqlE3(options, "DROP TRIGGER e3_fail;");
        }
        var preparation = await workflow.PrepareRenderingAsync(batch.BatchId, default);
        Assert.True(preparation.UsesInstancePipeline);
        Assert.Equal(scenario == "empty" ? 0 : 4, preparation.Pages.Count);
        Assert.Equal(scenario is "all_red" or "empty" ? 0 : scenario == "business_red" ? 2 : 4, preparation.ReadyPageCount);
        var coordinator = container.GetRequiredService<IRenderBatchWorkflow>();
        var journal = (await coordinator.LoadAsync(batch.BatchId, default))!;
        Assert.Equal(4, journal.Version);
        Assert.Equal(preparation.InstanceSets, journal.InstanceSets);
        Assert.All(journal.Pages, page => { Assert.NotNull(page.Instance); Assert.Null(page.OutputVariantId); });
        Assert.Equal(scenario == "empty" ? 0 : 4, journal.Pages.Select(page => page.Instance!.PageInstanceId).Distinct().Count());
        if (scenario != "empty") Assert.Single(journal.Pages.Select(page => page.PageId).Distinct());
        Assert.NotNull(await store.ReadAsync(batch.BatchId, batch.Styles[0].StyleId, default));
        Assert.Equal("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.LockSelectionAsync(batch.BatchId, batch.Styles[0].StyleId, "frame_render_integration", batch.Assets[0].AssetId, default))).Message);
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() => store.GetForBatchAsync(batch.BatchId, default));
        var backend = container.GetRequiredService<OfflineInstanceRenderer>();
        if (scenario is "all_red" or "empty")
        {
            Assert.Equal(scenario == "empty" ? RenderBatchStatus.Empty : RenderBatchStatus.UserActionRequired, journal.Status);
            Assert.Equal(0, backend.Opens);
            Assert.Equal(0, (await container.GetRequiredService<TaskCoordinator>().LoadAsync(batch.BatchId)).OutputCount);
            return;
        }
        if (scenario == "healthy")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareAsync(preparation with
            { InstanceSets = [preparation.InstanceSets![0] with { BatchPlanDigest = "sha256:" + new string('f', 64) }] }, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(journal with
            { Revision = 2, InstanceSets = [journal.InstanceSets![0] with { InputFactsDigest = "sha256:" + new string('f', 64) }] }, 1, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareAsync(preparation with { Pages = preparation.Pages.Skip(1).ToArray() }, default));
            var evidence = (await store.ReadProductionEvidenceAsync(batch.BatchId, batch.Styles[0].StyleId, default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FreezeProductionEvidenceAsync(evidence with
                { SourceEvidenceDigest = "sha256:" + new string('f', 64) }, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(journal with { BatchId = "invented" }, 0, default));
        }
        if (scenario == "unknown") backend.LoseAcceptance = true;
        if (scenario == "failed_retry") backend.FailInstance = journal.Pages[0].Instance!.PageInstanceId;
        if (scenario == "all_failed") backend.FailAll = true;
        if (scenario is "pause" or "cancel") backend.BeforeReady = () => scenario == "pause"
            ? coordinator.RequestPauseAsync(batch.BatchId, default) : coordinator.RequestCancelAsync(batch.BatchId, default);
        if (scenario == "publication_failure") await SqlE3(options,
            "CREATE TRIGGER e3_fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Committed' BEGIN SELECT RAISE(ABORT,'OFFLINE_PUBLICATION'); END;");
        if (scenario == "database_confirmation_failure") await SqlE3(options,
            "CREATE TRIGGER e3_fail BEFORE UPDATE ON render_page_checkpoints WHEN NEW.output_state='Committed' BEGIN SELECT RAISE(ABORT,'OFFLINE_CONFIRMATION'); END;");
        var completed = await coordinator.RunAsync(batch.BatchId, default);
        if (scenario is "pause" or "cancel" or "all_failed")
        {
            Assert.Equal(scenario == "pause" ? RenderBatchStatus.Paused : scenario == "cancel" ? RenderBatchStatus.Cancelled : RenderBatchStatus.Failed, completed.Status);
            Assert.Equal(scenario == "all_failed" ? 4 : 0, backend.Submissions);
            Assert.Equal(0, (await container.GetRequiredService<TaskCoordinator>().LoadAsync(batch.BatchId)).OutputCount);
            if (scenario != "pause") return;
            completed = await coordinator.RunAsync(batch.BatchId, default);
        }
        if (scenario == "unknown")
        {
            Assert.Equal(RenderBatchStatus.RecoveryRequired, completed.Status);
            var execution = backend.Records.Single().ExecutionId;
            var again = await coordinator.RunAsync(batch.BatchId, default);
            Assert.Equal(RenderBatchStatus.RecoveryRequired, again.Status);
            Assert.Equal(1, backend.Submissions);
            Assert.Equal(execution, backend.Records.Single().ExecutionId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareInstanceRetryAsync(batch.BatchId,
                batch.Styles[0].StyleId, journal.Pages[0].Instance!.PageInstanceId, 1, default));
            return;
        }
        if (scenario is "publication_failure" or "database_confirmation_failure")
        {
            Assert.Equal(RenderBatchStatus.RecoveryRequired, completed.Status);
            var submissions = backend.Submissions;
            var opens = backend.Opens;
            await SqlE3(options, "DROP TRIGGER e3_fail;");
            completed = await coordinator.RunAsync(batch.BatchId, default);
            if (scenario == "publication_failure") { Assert.Equal(submissions, backend.Submissions); Assert.Equal(opens, backend.Opens); }
            else Assert.Equal(4, backend.Submissions);
        }
        if (scenario == "failed_retry")
        {
            var failed = completed.Pages.Single(page => page.Status == RenderBatchPageStatus.Failed);
            var succeeded = completed.Pages.Where(page => page.Status == RenderBatchPageStatus.Committed).ToArray();
            var retry = await coordinator.PrepareInstanceRetryAsync(batch.BatchId, failed.StyleId, failed.Instance!.PageInstanceId, 1, default);
            Assert.Equal(2, retry.Pages[^1].Attempt);
            Assert.Equal(failed.Instance, retry.Pages[^1].Instance);
            backend.FailInstance = null;
            completed = await coordinator.RunAsync(batch.BatchId, default);
            Assert.Equal(succeeded, completed.Pages.Where(page => succeeded.Contains(page)));
            Assert.Equal(5, backend.Submissions);
        }
        Assert.Equal(scenario is "business_red" or "failed_retry" ? RenderBatchStatus.CompletedWithIssues : RenderBatchStatus.Completed, completed.Status);
        Assert.Equal(preparation.ReadyPageCount, (await container.GetRequiredService<TaskCoordinator>().LoadAsync(batch.BatchId)).OutputCount);
        var reader = container.GetRequiredService<IRenderBatchOutputReader>();
        foreach (var page in completed.Pages.Where(page => page.Status == RenderBatchPageStatus.Committed))
        {
            await using var jpeg = await reader.OpenJpegAsync(batch.BatchId, page, default);
            Assert.True(jpeg.CanRead); Assert.False(jpeg.CanWrite);
            var other = completed.Pages.First(item => item.Instance != page.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.OpenJpegAsync(batch.BatchId, page with { Instance = other.Instance }, default));
        }
        var publicDirectory = await reader.GetStyleOutputDirectoryAsync(batch.BatchId, batch.Styles[0].StyleId, default);
        Assert.NotNull(publicDirectory);
        var names = Directory.GetFiles(publicDirectory!, "*.jpg").Select(Path.GetFileName).ToArray();
        Assert.Equal(preparation.ReadyPageCount, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.Contains("商品主图-实例", name, StringComparison.Ordinal));
        var countBefore = backend.Submissions;
        await coordinator.RunAsync(batch.BatchId, default);
        Assert.Equal(countBefore, backend.Submissions);
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        Assert.Equal(JsonSerializer.Serialize(completed.Pages), JsonSerializer.Serialize((await reopened.LoadRenderBatchAsync(batch.BatchId, default))!.Pages));
        Assert.Equal(preparation.ReadyPageCount, (await new TaskCoordinator(reopened).LoadAsync(batch.BatchId)).OutputCount);
        Assert.Equal(JsonSerializer.Serialize(preparation), JsonSerializer.Serialize(await workflow.PrepareRenderingAsync(batch.BatchId, default)));
    }

    [Fact]
    public async Task E3ManualSelectionIsPersistedForOneInstanceWithCasAndFreezeAdmission()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(new ScenarioProvider(request => InstanceResult(request, "healthy")));
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>(); await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path);
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default); await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId, projection => projection.Status == AiBatchUiStatus.Ready);
        var editing = Assert.IsAssignableFrom<ITemplateInstanceSelectionWorkflow>(workflow);
        var review = (await editing.ReviewInstanceSelectionAsync(batch.BatchId, batch.Styles[0].StyleId,
            "frame_render_integration", batch.Assets[0].AssetId, default))!;
        Assert.Equal(2, review.Targets.Count); // Same colour, two recipes of the same PSD.
        Assert.Null(await store.ReadAsync(batch.BatchId, batch.Styles[0].StyleId, default));
        Assert.Null(await store.LoadRenderBatchAsync(batch.BatchId, default));
        var command = new TemplateInstanceSelectionCommand(batch.BatchId, review.StyleId, review.FrameId, review.AssetId,
            review.Targets[1].PageInstanceId, review.Revision, review.BaseFactsDigest);
        await editing.LockInstanceSelectionAsync(command, default);
        Assert.Equal("TPL_INSTANCE_SELECTION_REVISION_CONFLICT", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            editing.LockInstanceSelectionAsync(command, default))).Message);
        await using (var reopened = new SqliteDesktopStore(options))
        {
            await reopened.InitializeAsync();
            var selection = (await reopened.ReadManualSelectionsAsync(batch.BatchId, review.StyleId, default))!;
            Assert.Equal(1, selection.Revision); Assert.Single(selection.Overrides);
        }
        var refreshed = (await editing.ReviewInstanceSelectionAsync(batch.BatchId, review.StyleId, review.FrameId, review.AssetId, default))!;
        Assert.Equal(0, refreshed.Targets[0].SelectionRevision); Assert.Equal(1, refreshed.Targets[1].SelectionRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editing.LockInstanceSelectionAsync(command with
            { ExpectedRevision = 1, PageInstanceId = "instance_" + new string('f', 64) }, default));
        var prepared = await workflow.PrepareRenderingAsync(batch.BatchId, default);
        Assert.Equal(4, prepared.ReadyPageCount);
        var frozen = (await store.ReadAsync(batch.BatchId, review.StyleId, default))!;
        Assert.Equal(1, frozen.Instances.Single(instance => instance.PageInstanceId == command.PageInstanceId).Frames[0].SelectionRevision);
        Assert.All(frozen.Instances.Where(instance => instance.PageInstanceId != command.PageInstanceId), instance => Assert.Equal(0, instance.Frames[0].SelectionRevision));
        var saved = (await store.ReadManualSelectionsAsync(batch.BatchId, review.StyleId, default))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveManualSelectionsAsync(batch.BatchId, review.StyleId,
            saved with { Revision = 2, Overrides = [saved.Overrides[0] with { SelectionRevision = 2 }] }, 1, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editing.LockInstanceSelectionAsync(command with { ExpectedRevision = 1 }, default));
    }

    [Fact]
    public async Task RepeatedPreparationReusesFrozenInstanceEvidenceAndProjection()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(new ScenarioProvider(request => InstanceResult(request, "healthy")));
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>();
        await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path);
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default);
        await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId, projection => projection.Status == AiBatchUiStatus.Ready);

        var first = await workflow.PrepareRenderingAsync(batch.BatchId, default);
        var evidence = (await store.ReadProductionEvidenceAsync(batch.BatchId, batch.Styles[0].StyleId, default))!;
        var snapshot = (await store.ReadAsync(batch.BatchId, batch.Styles[0].StyleId, default))!;
        var second = await workflow.PrepareRenderingAsync(batch.BatchId, default);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(await store.ReadAsync(batch.BatchId, batch.Styles[0].StyleId, default)));
        Assert.Equal(JsonSerializer.Serialize(evidence), JsonSerializer.Serialize(await store.ReadProductionEvidenceAsync(batch.BatchId, batch.Styles[0].StyleId, default)));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("conflict")]
    [InlineData("calibration")]
    [InlineData("resource_path")]
    public async Task E3SizeInstancesUseFrozenCurrentStyleDataAndIsolatedResources(string scenario)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var services = new ServiceCollection().AddDesktopFoundation(options);
        services.AddSingleton<IAiServicePort>(new ScenarioProvider(request =>
        {
            var result = InstanceResult(request, "healthy");
            return result with { SizeTableCandidate = scenario == "missing" ? null : new(request.Assets[0].AssetId, 0.99m,
                ["尺码", "胸围"], [["L", "102"], ["XL", "107"]]),
                Warnings = scenario == "conflict" ? [new("size_source_conflict", "size_table", "两个来源冲突")] : [] };
        }));
        services.AddSingleton<OfflineInstanceRenderer>();
        services.AddSingleton<IRenderBatchSessionFactory>(container => container.GetRequiredService<OfflineInstanceRenderer>());
        await using var container = services.BuildServiceProvider();
        var store = container.GetRequiredService<SqliteDesktopStore>(); await store.InitializeAsync();
        var batch = await SeedRuleEntryAsync(container, directory.Path, scenario);
        var workflow = container.GetRequiredService<IAiBatchAnalysisWorkflow>();
        await workflow.GrantUploadConsentAsync(default); await workflow.StartAsync(batch.BatchId, default);
        await WaitForProjectionAsync((AiBatchAnalysisWorkflow)workflow, batch.BatchId,
            projection => projection.Status is AiBatchUiStatus.Ready or AiBatchUiStatus.ReviewRequired or AiBatchUiStatus.NotGeneratable);
        if (scenario == "calibration")
        {
            Assert.Equal("SIZE_TABLE_CALIBRATION_INVALID", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                workflow.PrepareRenderingAsync(batch.BatchId, default))).Message);
            Assert.Null(await store.LoadRenderBatchAsync(batch.BatchId, default));
            await container.DisposeAsync();
            return;
        }
        var prepared = await workflow.PrepareRenderingAsync(batch.BatchId, default);
        var sizePages = prepared.Pages.Where(page => page.PageId == "page_size_e3").ToArray();
        Assert.Equal(2, sizePages.Length);
        Assert.Equal(2, sizePages.Select(page => page.Instance!.PageInstanceId).Distinct().Count());
        Assert.All(prepared.Pages.Where(page => page.PageId != "page_size_e3"), page =>
            Assert.DoesNotContain("deterministic_size_table", page.RenderPlanJson!, StringComparison.Ordinal));
        if (scenario is "missing" or "conflict")
        {
            Assert.All(sizePages, page => { Assert.False(page.HasPlan); Assert.Equal("SIZE_TABLE_SOURCE_" + scenario.ToUpperInvariant(), page.ReasonCode); });
            Assert.Equal(4, prepared.ReadyPageCount); return;
        }
        Assert.All(sizePages, page => Assert.True(page.HasPlan));
        Assert.Equal(2, sizePages.Select(page => page.PlanDigest).Distinct().Count());
        var evidence = (await store.ReadProductionEvidenceAsync(batch.BatchId, batch.Styles[0].StyleId, default))!;
        var frozen = Assert.Single(evidence.SizePages);
        Assert.Equal(batch.Styles[0].StyleId, SizeTableRepairData.Deserialize(frozen.TableJson!).StyleId);
        var paths = container.GetRequiredService<IPathPolicy>();
        using var plan = JsonDocument.Parse(sizePages[0].RenderPlanJson!);
        var rasterId = plan.RootElement.GetProperty("size_table").GetProperty("render_resource_id").GetString()!;
        var resource = paths.ResolveOwnedPath($"batches/{batch.BatchId}/size-tables/{batch.Styles[0].StyleId}/page_size_e3/resources/{rasterId}.png");
        Assert.True(File.Exists(resource));
        if (scenario == "resource_path")
        {
            File.Move(resource, resource + ".original"); Directory.CreateDirectory(resource);
            var journal = (await store.LoadRenderBatchAsync(batch.BatchId, default))!;
            var page = journal.Pages.First(page => page.PageId == "page_size_e3");
            await Assert.ThrowsAnyAsync<Exception>(() => container.GetRequiredService<RenderBatchResourcePreparer>()
                .PrepareAsync(batch, page, new LocalAppPathPolicy(Path.Combine(directory.Path, "copies")), default));
            Assert.Equal(0, container.GetRequiredService<OfflineInstanceRenderer>().Submissions);
        }
        else
        {
            var edit = await container.GetRequiredService<ISizeTableEditingWorkflow>().SaveAsync(new(batch.BatchId,
                batch.Styles[0].StyleId, "page_size_e3", 1, new([], [])), default);
            Assert.Equal("TPL_INSTANCE_PRODUCTION_FACTS_FROZEN", edit.ReasonCode);
            Assert.Equal(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(await workflow.PrepareRenderingAsync(batch.BatchId, default)));
            var completed = await container.GetRequiredService<IRenderBatchWorkflow>().RunAsync(batch.BatchId, default);
            Assert.Equal(RenderBatchStatus.Completed, completed.Status);
            Assert.Equal(6, (await container.GetRequiredService<TaskCoordinator>().LoadAsync(batch.BatchId)).OutputCount);
            var reader = container.GetRequiredService<IRenderBatchOutputReader>();
            foreach (var sizePage in completed.Pages.Where(page => page.PageId == "page_size_e3"))
            {
                await using var jpeg = await reader.OpenJpegAsync(batch.BatchId, sizePage, default);
                Assert.True(jpeg.CanRead);
                Assert.Equal(sizePage.Instance, sizePage.Commit!.Page.Instance);
            }
        }
    }

    private static async Task<BatchDraft> SeedRuleEntryAsync(ServiceProvider container, string root, string? sizeCase = null, bool zero = false)
    {
        var fixture = Fixture(root, DateTimeOffset.UnixEpoch);
        var templateBytes = System.Text.Encoding.UTF8.GetBytes("Offline template bytes, not a real Photoshop acceptance sample.");
        var templateHash = Convert.ToHexStringLower(SHA256.HashData(templateBytes));
        var resource = fixture.Resource with { Sha256 = templateHash, Length = templateBytes.Length,
            Inspection = fixture.Resource.Inspection with { FileSha256 = templateHash, SizeBytes = templateBytes.Length } };
        var templatePath = container.GetRequiredService<IPathPolicy>().ResolveOwnedPath(resource.InternalRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!); await File.WriteAllBytesAsync(templatePath, templateBytes);
        var page = fixture.Revision.Pages[0];
        var recipe = new TemplatePageRecipe("recipe_a", page.PageId, TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.All,
            [new("slot", TemplateRuleColorSource.Allocated)], [new("frame_render_integration", "slot")]);
        if (zero) recipe = recipe with { ColorRange = TemplateRuleColorRange.Secondary };
        var recipes = new List<TemplatePageRecipe> { recipe, recipe with { RecipeId = "recipe_b" } };
        var pages = fixture.Revision.Pages.ToList();
        if (sizeCase is not null)
        {
            var anchor = resource.Inspection.Layers[0] with { Path = [0], LayerId = 99, Name = "旧尺码占位", Kind = "pixel", SmartObject = null };
            resource = resource with { Inspection = resource.Inspection with { Layers = [anchor, .. resource.Inspection.Layers], LayerCount = 2 } };
            var reference = new SizeTableTemplateLayer(1, 99, "pixel", anchor.Name, resource.StructureSignature);
            var calibration = new SizeTableTemplateCalibration("size-table-template/v1", sizeCase == "calibration" ? new string('0', 64) : resource.Sha256,
                "生产尺码表", reference, [reference], TemplateRect.Create(10, 20, 780, 1200),
                new("Microsoft YaHei UI", 24, 1000, 56, 24, 12, "#FFFFFFFF", "#FFF2F4F7", "#FF101828", "#FF98A2B3"));
            pages.Add(TemplatePage.Create("page_size_e3", page.RevisionId, 1, "尺码页", TemplatePageContentType.DynamicSizeTable,
                TemplateGenerationMode.OncePerProduct, null, true, resource.ResourceId, sizeTableCalibration: calibration));
            var sizeRecipe = new TemplatePageRecipe("size_a", "page_size_e3", TemplateRuleRepeat.Once, TemplateRuleColorRange.None, [], []);
            recipes.Add(sizeRecipe); recipes.Add(sizeRecipe with { RecipeId = "size_b" });
        }
        var rules = new TemplateRuleSet(1, [new("branch", new(1), [.. recipes])]);
        var draft = TemplateRevision.CreateDraft(fixture.Revision.RevisionId, fixture.Group.GroupId, 1, DateTimeOffset.UnixEpoch,
            pages, fixture.Revision.FrameBindings, new(0, rules, []));
        var store = container.GetRequiredService<SqliteDesktopStore>();
        await store.SaveGroupAsync(fixture.Group, default);
        await store.SaveDraftRevisionAsync(draft, default);
        draft = (await store.GetRevisionAsync(draft.RevisionId, default))!;
        var revision = TemplateRevision.Restore(draft.RevisionId, draft.GroupId, 1, TemplateRevisionState.Committed,
            fixture.Revision.ManifestRef, TemplateRuleCodec.BusinessDigest(draft), draft.CreatedAt, draft.Pages, draft.FrameBindings, draft.RuleWorkspace);
        await store.CommitRevisionAsync(revision, null, default);
        await store.SaveImportedAsync("e3_import", resource, default);
        var input = Directory.CreateDirectory(Path.Combine(root, "sources")).FullName;
        WriteInputJpeg(Path.Combine(input, "red.jpg")); if (!zero) WriteInputJpeg(Path.Combine(input, "blue.jpg"));
        var publicRoot = Directory.CreateDirectory(Path.Combine(root, "public")).FullName;
        var service = container.GetRequiredService<BatchInputService>();
        var batch = await service.CreateDraftFromTemplateAsync(new("e3_batch", fixture.Group.GroupId, input,
            Path.Combine(root, "evidence"), pages.Select(page => page.PageId).ToArray(), BatchSelectionMode.Automatic, false,
            BatchOutputSelection.Create(BatchOutputMode.UnifiedDirectory, publicRoot)));
        Assert.IsType<BatchRuleTemplateContext>(batch.TemplateSnapshot);
        batch = await service.ScanAsync(batch.BatchId);
        Assert.Equal(BatchScanStatus.ScanReady, batch.ScanStatus);
        return batch;
    }

    private static AiAnalysisResultV1 InstanceResult(AiAnalysisRequestV1 request, string scenario)
    {
        var assets = request.Assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray();
        var result = Result(request, matches: request.Requirements.SelectMany(requirement => assets.Select((asset, index) =>
            new AiRequirementMatchV1(requirement.RequirementId, asset.AssetId, index + 1,
                scenario == "low_confidence" ? 0.55m : 0.96m,
                scenario == "all_red" || scenario == "business_red" && index == 1
                    ? ["unsafe"]
                    : scenario == "low_confidence" ? [AiTargetMatchEvidence.SafeSimilar] : ["front_view", "clear_subject"]))).ToArray(),
            geometry: assets.Select(asset => new AiSubjectGeometryV1(asset.AssetId, 0.1m, 0.1m, 0.9m, 0.9m, 0.96m)).ToArray());
        return result with { VariantGroups = assets.Select((asset, index) => new AiVariantGroupV1("color_" + index, "color",
            index == 0 ? "红" : "蓝", [asset.AssetId], index == 0)).ToArray() };
    }

    private static void WriteInputJpeg(string path)
    {
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1000, 1600, 96, 96, PixelFormats.Bgr24, null, new byte[1000 * 1600 * 3], 3000)));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static async Task SqlE3(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    // Renderer boundary only. Real DI, input scan, persisted AI, workflow, coordinator, file commit,
    // SQLite checkpoints, publication and result reader remain production implementations.
    // This emits offline JPEG evidence; it is explicitly not Photoshop/visual acceptance.
    private sealed class OfflineInstanceRenderer(SqliteDesktopStore store) : IRenderBatchSessionFactory, IRenderBatchSession
    {
        internal int Opens, Submissions;
        internal bool LoseAcceptance;
        internal string? FailInstance;
        internal bool FailAll;
        internal Func<Task>? BeforeReady;
        internal List<BridgeExecutionRecord> Records { get; } = [];
        private BatchDraft _batch = null!;
        public async Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken token)
        {
            Opens++; _batch = (await store.GetAsync(journal.BatchId, token))!;
            try { using (await new RenderStyleOutputRegistry(store).OpenForRenderAsync(journal.BatchId, token)) { } }
            catch (Exception error) { Assert.Fail(error.ToString()); }
            return this;
        }
        public async Task<bool> IsReadyAsync(CancellationToken token)
        {
            if (BeforeReady is { } action) { BeforeReady = null; await action(); }
            return true;
        }
        public Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken token)
        {
            Submissions++;
            using var json = JsonDocument.Parse(page.PlanJson!); var plan = json.RootElement;
            var record = new BridgeExecutionRecord(_batch.BatchId, "offline_" + Guid.NewGuid().ToString("N"),
                plan.GetProperty("idempotency_key").GetString()!, plan.GetProperty("plan_digest").GetString()!, page.PageId,
                LoseAcceptance ? "unknown" : "accepted", false, 0, [], page.Instance);
            Assert.DoesNotContain(Records, item => item.IdempotencyKey == record.IdempotencyKey);
            Records.Add(record);
            return Task.FromResult(LoseAcceptance ? new RenderPageObservation(RenderBatchPageStatus.Unknown, "OFFLINE_LOST_ACK")
                : new RenderPageObservation(RenderBatchPageStatus.Running, "OFFLINE_RENDERING", record.ExecutionId));
        }
        public async Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken token)
        {
            using var json = JsonDocument.Parse(page.PlanJson!); var plan = json.RootElement;
            var paths = new LocalAppPathPolicy(_batch.OutputRoot);
            if (page.Commit is { } existing)
            {
                using var verified = await RenderPageCommitter.OpenCommittedJpegAsync(paths, store, existing, token);
                return new(page.Status, page.ReasonCode, page.ExecutionId, existing);
            }
            if (page.Status is RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled) return new(page.Status, page.ReasonCode, page.ExecutionId);
            var record = Records.Single(item => item.IdempotencyKey == plan.GetProperty("idempotency_key").GetString());
            if (record.State == "unknown") return new(RenderBatchPageStatus.Unknown, "OFFLINE_QUERY_UNKNOWN", record.ExecutionId);
            if (FailAll || page.Instance!.PageInstanceId == FailInstance) return new(RenderBatchPageStatus.Failed, "BRIDGE_PLAN_INVALID", record.ExecutionId);
            var artifact = plan.GetProperty("output").GetProperty("artifact_set_id").GetString()!;
            var relative = "outputs/" + artifact + "/manifest.json";
            if (File.Exists(paths.ResolveOwnedPath(relative)))
            {
                var recovered = await RenderPageCommitter.RecoverAsync(paths, store, relative, token, record);
                return new(RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED", record.ExecutionId, recovered.Checkpoint);
            }
            var checkpoint = new TaskCheckpoint(_batch.BatchId, page.StyleId, page.PageId, "render_output", page.Attempt,
                record.PlanDigest, record.ExecutionId, OutputCommitState.Verified, DateTimeOffset.UnixEpoch, Instance: page.Instance);
            var jpg = RenderJpegValidatorTests.Jpeg(); var hash = Convert.ToHexStringLower(SHA256.HashData(jpg));
            var name = plan.GetProperty("output").GetProperty("jpg").GetProperty("file_name").GetString()!;
            var media = new RenderMediaEvidence(record.PlanDigest, new(3, 2, RenderJpegValidator.SrgbProfileSha256, 3144), 0, 0,
                [new("jpg", jpg.Length, hash)]);
            var temp = paths.ResolveOwnedPath("offline/" + record.ExecutionId + ".jpg"); Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await File.WriteAllBytesAsync(temp, jpg, token); using var source = File.OpenRead(temp);
            using var prepared = await RenderPageCommitter.PrepareAsync(paths, "outputs",
                RenderPageCommitter.StagingDirectory("outputs", _batch.BatchId, page.StyleId, page.PageId, record.PlanDigest), artifact,
                checkpoint, media, [new("jpg", name, jpg.Length, hash)], new Dictionary<string, FileStream> { ["jpg"] = source }, token);
            var committed = await prepared.CommitAsync(store, false, token);
            return new(RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED", record.ExecutionId, committed.Checkpoint);
        }
        public Task CancelAsync(RenderBatchPage page, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
