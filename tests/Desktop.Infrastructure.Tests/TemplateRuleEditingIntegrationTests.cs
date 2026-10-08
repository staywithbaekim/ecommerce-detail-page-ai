using System.Collections.Immutable;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateRuleEditingIntegrationTests
{
    [Fact]
    public async Task RemoveAndUndoPagePreservesBindingsAliasesAndUnrelatedFrameEdit()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var initial = await service.GetRulesAsync(Session, default);
        var removed = await service.ApplyRulePatchAsync(Session, new("remove", initial.Version,
            new([], [], Pages: [new("page_a", null, [])])), default);
        Assert.DoesNotContain(removed.Frames, frame => frame.PageId == "page_a");
        var changed = await service.ApplyRulePatchAsync(Session, Patch("unrelated", removed.Version, "frame_b", "keep"), default);
        var restored = await service.UndoRulePatchAsync(Session, "remove", "undo", changed.Version, default);
        Assert.Equal(initial.Aliases.ToArray(), restored.Aliases.ToArray());
        Assert.Equal("keep", restored.Frames.Single(frame => frame.FrameId == "frame_b").Description);
        Assert.Equal("original", restored.Frames.Single(frame => frame.FrameId == "frame_a").Description);
        Assert.Equal(["page_a", "page_b"], (await store.GetRevisionAsync("revision", default))!.Pages.OrderBy(page => page.Order).Select(page => page.PageId));
    }

    [Fact]
    public async Task StopReplacingAndUndoRestoresTheSameTargetIdentityAndDiagnosesDanglingRecipe()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, default);
        view = await service.ApplyRulePatchAsync(Session, new("rules", view.Version, new([], [new("default", Branch())])), default);
        var original = view;
        view = await service.ApplyRulePatchAsync(Session, new("stop", view.Version, new([], [], Targets: [new("frame_a", null)])), default);
        Assert.NotEmpty(view.Diagnostics);
        view = await service.UndoRulePatchAsync(Session, "stop", "undo", view.Version, default);
        Assert.Empty(view.Diagnostics);
        Assert.Equal(original.RuleDigest, view.RuleDigest);
        Assert.Equal(original.Aliases.ToArray(), view.Aliases.ToArray());
    }

    private static readonly TemplateDraftSession Session = new("group", "rules", "revision", 1, new HashSet<string>());

    [Fact]
    public async Task RulePatchAliasesAndReceiptsSurviveReopenWithoutChangingLegacySemantics()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        TemplateRuleDraftView saved;
        TemplateRulePatch patch;
        await using (var store = new SqliteDesktopStore(options))
        {
            await SeedAsync(store);
            var service = Service(store);
            var initial = await service.GetRulesAsync(Session, CancellationToken.None);
            Assert.Null(initial.Rules);
            Assert.Equal(1, initial.Version);
            Assert.Equal(4, initial.Aliases.Length);
            patch = new("edit_1", initial.Version, new([new("frame_a", "正面全身")], [new("default", Branch())]));
            saved = await service.ApplyRulePatchAsync(Session, patch, CancellationToken.None);
            Assert.Equal(2, saved.Version);
            Assert.Empty(saved.Diagnostics);
            Assert.Equal(2, (await service.GetRuleImpactAsync(Session, "page_a", CancellationToken.None)).Count);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var loaded = await Service(reopened).ApplyRulePatchAsync(Session, patch, CancellationToken.None);
        Assert.Equal(saved.Version, loaded.Version);
        Assert.Equal(saved.RuleDigest, loaded.RuleDigest);
        Assert.Equal(saved.Aliases.ToArray(), loaded.Aliases.ToArray());
        Assert.Equal("正面全身", loaded.Frames.Single(frame => frame.FrameId == "frame_a").Description);
    }

    [Fact]
    public async Task StalePatchAndReusedCommandCannotOverwriteManualEdits()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var initial = await service.GetRulesAsync(Session, CancellationToken.None);
        var first = Patch("first", initial.Version, "frame_a", "first");
        var saved = await service.ApplyRulePatchAsync(Session, first, CancellationToken.None);
        await AssertCode("TPL_RULE_VERSION_CONFLICT", () => service.ApplyRulePatchAsync(Session,
            Patch("late", initial.Version, "frame_a", "late"), CancellationToken.None));
        await AssertCode("TPL_RULE_COMMAND_REUSED", () => service.ApplyRulePatchAsync(Session,
            first with { Changes = new([new("frame_a", "different")], []) }, CancellationToken.None));
        Assert.Equal(saved.RuleDigest, (await service.GetRulesAsync(Session, CancellationToken.None)).RuleDigest);
    }

    [Fact]
    public async Task PreWorkspaceWriterCannotSilentlyEraseNewRules()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var stale = (await store.GetRevisionAsync("revision", CancellationToken.None))!;
        var view = await Service(store).GetRulesAsync(Session, CancellationToken.None);
        await AssertCode("TPL_RULE_VERSION_CONFLICT", () => store.SaveDraftRevisionAsync(
            stale.UpdateFrameBinding("frame_a", SelectionRequirement.Create("stale")), CancellationToken.None));
        Assert.Equal(view.RuleDigest, (await Service(store).GetRulesAsync(Session, CancellationToken.None)).RuleDigest);
    }

    [Fact]
    public async Task AliasesAreTombstonedNotReassignedByDeletionReorderOrNewImport()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var first = await Service(store).GetRulesAsync(Session, CancellationToken.None);
        var revision = (await store.GetRevisionAsync("revision", CancellationToken.None))!;
        await store.SaveDraftRevisionAsync(revision.RemovePage("page_a").AddPage(Page("page_c", 1))
            .ReorderPages(["page_c", "page_b"]), CancellationToken.None);
        var next = await Service(store).GetRulesAsync(Session, CancellationToken.None);
        Assert.All(first.Aliases, alias => Assert.Contains(alias, next.Aliases));
        Assert.Equal(3, next.Aliases.Single(alias => alias.PageId == "page_c" && alias.FrameId is null).Number);
        Assert.Equal(2, next.Aliases.Single(alias => alias.PageId == "page_b" && alias.FrameId is null).Number);
    }

    [Fact]
    public async Task UndoTouchesOnlyItsFieldsAndSurvivesUnrelatedChanges()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, CancellationToken.None);
        view = await service.ApplyRulePatchAsync(Session, Patch("a", view.Version, "frame_a", "changed_a"), CancellationToken.None);
        view = await service.ApplyRulePatchAsync(Session, Patch("b", view.Version, "frame_b", "changed_b"), CancellationToken.None);
        var undoVersion = view.Version;
        view = await service.UndoRulePatchAsync(Session, "a", "undo_a", undoVersion, CancellationToken.None);
        Assert.Equal("original", view.Frames.Single(frame => frame.FrameId == "frame_a").Description);
        Assert.Equal("changed_b", view.Frames.Single(frame => frame.FrameId == "frame_b").Description);
        var replay = await service.UndoRulePatchAsync(Session, "a", "undo_a", undoVersion, CancellationToken.None);
        Assert.Equal(view.Version, replay.Version);
        await AssertCode("TPL_RULE_UNDO_UNAVAILABLE", () => service.UndoRulePatchAsync(Session, "a", "again", view.Version, CancellationToken.None));
    }

    [Fact]
    public async Task UndoRefusesToClobberANewerValueOfTheSameField()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, CancellationToken.None);
        view = await service.ApplyRulePatchAsync(Session, Patch("a", view.Version, "frame_a", "first"), CancellationToken.None);
        view = await service.ApplyRulePatchAsync(Session, Patch("b", view.Version, "frame_a", "second"), CancellationToken.None);
        await AssertCode("TPL_RULE_UNDO_CONFLICT", () => service.UndoRulePatchAsync(Session, "a", "undo", view.Version, CancellationToken.None));
    }

    [Fact]
    public async Task UndoFirstBranchRestoresExplicitLegacyStateWithoutLosingFrameRules()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, CancellationToken.None);
        view = await service.ApplyRulePatchAsync(Session, new("branch", view.Version, new([], [new("default", Branch())])), CancellationToken.None);
        view = await service.UndoRulePatchAsync(Session, "branch", "undo", view.Version, CancellationToken.None);
        Assert.Null(view.Rules);
        Assert.Equal(2, view.Frames.Length);
    }

    [Fact]
    public async Task CommandWriteFailureRollsBackFrameRulesReferencesAndVersionTogether()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await SeedAsync(store);
        var service = Service(store);
        var before = await service.GetRulesAsync(Session, CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_rule_command BEFORE INSERT ON template_rule_commands BEGIN SELECT RAISE(ABORT,'test crash'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => service.ApplyRulePatchAsync(Session,
            new("atomic", before.Version, new([new("frame_a", "changed")], [new("default", Branch())])), CancellationToken.None));
        var after = await service.GetRulesAsync(Session, CancellationToken.None);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.RuleDigest, after.RuleDigest);
        Assert.Empty(await service.GetRuleImpactAsync(Session, "page_a", CancellationToken.None));
    }

    [Fact]
    public async Task InvalidReferencesFailAtomicallyButSemanticConflictsCanRemainInADraft()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, CancellationToken.None);
        await AssertCode("TPL_RULE_FRAME_REFERENCE_INVALID", () => service.ApplyRulePatchAsync(Session,
            new("bad", view.Version, new([new("frame_a", "changed"), new("invented", "fake")], [])), CancellationToken.None));
        var conflict = Branch() with { Condition = new(3, 1) };
        view = await service.ApplyRulePatchAsync(Session, new("conflict", view.Version, new([], [new("default", conflict)])), CancellationToken.None);
        Assert.Contains(view.Diagnostics, item => item.Code == "TPL_RULE_CONDITION_INVALID");
        Assert.Equal("original", view.Frames[0].Description);
        await AssertCode("TPL_RULE_COMMIT_INVALID", () => service.ConfirmRulesAsync(Session,
            new(view.Version, view.RuleDigest), CancellationToken.None));
    }

    [Fact]
    public async Task OverviewCannotCommitAVersionChangedSinceItWasShown()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        var service = Service(store);
        var view = await service.GetRulesAsync(Session, CancellationToken.None);
        await service.ApplyRulePatchAsync(Session, Patch("changed", view.Version, "frame_a", "changed"), CancellationToken.None);
        await AssertCode("TPL_RULE_OVERVIEW_STALE", () => service.ConfirmRulesAsync(Session,
            new(view.Version, view.RuleDigest), CancellationToken.None));
        await AssertCode("TPL_RULE_OVERVIEW_STALE", () => service.CommitDraftAsync(Session, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"version\":0,\"rules\":null,\"aliases\":[],\"unknown\":true}")]
    [InlineData("{\"version\":0,\"version\":1,\"rules\":null,\"aliases\":[]}")]
    [InlineData("{\"version\":0,\"rules\":null}")]
    [InlineData("null")]
    public void StrictWorkspacePayloadRejectsUnknownMissingAndDuplicateFields(string json)
    {
        Assert.Throws<TemplateRuleViolationException>(() => TemplateRuleCodec.Deserialize<TemplateRuleWorkspace>(json));
    }

    [Fact]
    public async Task BusinessDigestIgnoresCanvasAndAliasesButIncludesFrameSemanticsAndOrder()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await SeedAsync(store);
        await Service(store).GetRulesAsync(Session, CancellationToken.None);
        var revision = (await store.GetRevisionAsync("revision", CancellationToken.None))!;
        var original = TemplateRuleCodec.BusinessDigest(revision);
        var page = revision.Pages[0];
        var moved = revision.ConfigurePage(page.PageId, page.Name, page.ContentType, page.GenerationMode,
            page.VariantCapacity, page.Enabled, canvasX: 123, canvasY: 456);
        Assert.Equal(original, TemplateRuleCodec.BusinessDigest(moved));
        Assert.NotEqual(original, TemplateRuleCodec.BusinessDigest(revision.ReorderPages(["page_b", "page_a"])));
        Assert.NotEqual(original, TemplateRuleCodec.BusinessDigest(revision.UpdateFrameBinding("frame_a", SelectionRequirement.Create("new"))));
    }

    private static Task AssertCode(string code, Func<Task> action) => AssertCodeCore(code, action);
    private static async Task AssertCodeCore(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TemplateRuleViolationException>(action);
        Assert.Equal(code, error.Code);
    }

    private static TemplateRulePatch Patch(string id, long version, string frame, string text) =>
        new(id, version, new([new(frame, text)], []));
    private static TemplateWorkspaceService Service(SqliteDesktopStore store) => new(store, store, null!, TimeProvider.System);
    private static TemplatePage Page(string id, int order) => TemplatePage.Create(id, "revision", order, id,
        TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true, "resource_" + id);
    private static TemplateRuleBranch Branch() => new("default", new(1),
        [new("once", "page_a", TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
            [new("primary", TemplateRuleColorSource.Primary)], [new("frame_a", "primary")])]);

    private static async Task SeedAsync(SqliteDesktopStore store)
    {
        await store.InitializeAsync();
        await store.SaveGroupAsync(TemplateGroup.Create("group", "rules", DateTimeOffset.UnixEpoch), CancellationToken.None);
        FrameBinding Frame(string id, string page) => FrameBinding.Create(id, page, LayerPath.Create([1]),
            ExpectedLayerEvidence.Create(1, "target", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))), [], SelectionRequirement.Create("original"),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 600, 800)));
        await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
            [Page("page_a", 0), Page("page_b", 1)], [Frame("frame_a", "page_a"), Frame("frame_b", "page_b")]), CancellationToken.None);
    }
}
