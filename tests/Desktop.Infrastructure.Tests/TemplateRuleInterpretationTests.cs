using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateRuleInterpretationTests
{
    private static readonly TemplateDraftSession Session = new("group", "test", "revision", 1, new HashSet<string>());
    [Fact]
    public async Task FirstTextRuleCreatesOnlySelectedRealTargetsWithoutReferencePhotosAndCanUndoAfterReopen()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path, pages: 2);
        var initial = await workspace.GetRulesAsync(Session, default);
        Assert.Empty(initial.Frames); Assert.Equal(2, initial.Candidates.Count);
        var first = initial.Candidates[0]; var second = initial.Candidates[1];
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(first.FrameId, "假模特正面完整图"), new(second.FrameId, "同款同色背面图")], [], "已设置正背面", null), new(10, 10, 20, false), null);
        var result = await service.InterpretAsync(Session, new("first_rules", initial.Version, "@1 用假模正面图，@2 用同色背面图", []), default);
        Assert.False(result.RequiresClarification); Assert.Equal(2, result.Draft.Frames.Length);
        Assert.All(provider.Request!.Frames, frame => Assert.Empty(frame.Description));
        var revision = (await store.GetRevisionAsync(Session.DraftRevisionId, default))!;
        Assert.Null(revision.RuleWorkspace!.Rules); Assert.Equal(2, revision.FrameBindings.Length);
        Assert.All(revision.FrameBindings, binding => Assert.DoesNotContain(binding.SelectionRequirement.Criteria, criterion => criterion.Key == "asset_id"));
        var reopened = new TemplateWorkspaceService(store, store, null!, TimeProvider.System);
        var saved = await reopened.GetRulesAsync(Session, default);
        Assert.Equal(initial.Aliases.ToArray(), saved.Aliases.ToArray());
        var undone = await reopened.UndoRulePatchAsync(Session, result.AppliedCommandId!, "undo_first", saved.Version, default);
        Assert.Empty(undone.Frames); Assert.Equal(initial.Aliases.ToArray(), undone.Aliases.ToArray());
        Assert.Equal(2, (await reopened.GetRulesAsync(Session, default)).Candidates.Count);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ExplicitSecondCandidateRetainsItsNumberAndDoesNotRegisterTheFirst()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path, framesPerPage: 2);
        var initial = await workspace.GetRulesAsync(Session, default);
        var second = initial.Candidates[1];
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(second.FrameId, "袖口特写图")], [], "已更新框2", null), null, null);
        var result = await service.InterpretAsync(Session, new("second", initial.Version, "@1 的框2 用袖口特写图", []), default);
        Assert.Equal(second.FrameId, Assert.Single(result.Draft.Frames).FrameId);
        Assert.Equal(2, result.Draft.Aliases.Single(alias => alias.FrameId == second.FrameId).Number);
        var view = await workspace.GetRulesAsync(Session, default);
        Assert.Equal(initial.Candidates[0].FrameId, Assert.Single(view.Candidates, candidate => candidate.CanInterpret).FrameId);
        Assert.False(view.Candidates.Single(candidate => candidate.FrameId == second.FrameId).CanInterpret);
        Assert.Equal(1, view.Aliases.Single(alias => alias.FrameId == initial.Candidates[0].FrameId).Number);
    }

    [Fact]
    public async Task AmbiguousPageCandidateNeedsAnExplicitFrameAndDoesNotReachProvider()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path, framesPerPage: 2);
        var initial = await workspace.GetRulesAsync(Session, default);
        var result = await service.InterpretAsync(Session, new("ambiguous", initial.Version, "@1 用正面图", []), default);
        Assert.True(result.RequiresClarification); Assert.Contains("具体框", result.Message);
        Assert.Equal(0, provider.Calls); Assert.Empty(result.Draft.Frames);
    }

    [Theory]
    [InlineData(AiProviderOutcome.AiResultUnknown)]
    [InlineData(AiProviderOutcome.RetryableError)]
    [InlineData(AiProviderOutcome.TerminalError)]
    public async Task FirstRuleProviderFailureNeverRegistersCandidatesOrChangesTheirAliases(AiProviderOutcome outcome)
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        provider.Result = new(outcome, null, null, null);
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() => service.InterpretAsync(Session,
            new("failure", initial.Version, "@1 用正面图", []), default));
        var after = await workspace.GetRulesAsync(Session, default);
        Assert.Empty(after.Frames); Assert.Equal(initial.RuleDigest, after.RuleDigest);
        Assert.Equal(initial.Aliases.ToArray(), after.Aliases.ToArray());
        if (outcome == AiProviderOutcome.AiResultUnknown)
        {
            await Code("TPL_RULE_AI_RESULT_UNKNOWN", () => service.InterpretAsync(Session,
                new("another_click", after.Version, "@1 用背面图", []), default));
            Assert.Equal(1, provider.Calls);
        }
    }

    [Fact]
    public async Task ClarificationLeavesNewTargetsUnregistered()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [], [], "需要确认", "需要正面还是背面？"), new(10, 10, 20, false), null);
        var result = await service.InterpretAsync(Session, new("clarify", initial.Version, "@1 用模特图", []), default);
        Assert.True(result.RequiresClarification); Assert.Empty(result.Draft.Frames);
        Assert.Equal(initial.RuleDigest, result.Draft.RuleDigest);
    }

    [Fact]
    public async Task FirstRuleAndPageRecipeRegisterAtomicallyAndUndoTogether()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default); var candidate = Assert.Single(initial.Candidates);
        var branch = new TemplateRuleBranch("first", new(1, 2),
            [new("page_once", candidate.PageId, TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
                [new("main", TemplateRuleColorSource.Primary)], [new(candidate.FrameId, "main")])]);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(candidate.FrameId, "主色正面图")], [new("first", branch)], "每款使用一次", null), null, null);
        var result = await service.InterpretAsync(Session, new("with_recipe", initial.Version, "@1 用主色正面图，每款一次", []), default);
        Assert.Single(result.Draft.Frames); Assert.Empty(result.Draft.Diagnostics); Assert.NotNull(result.Draft.Rules);
        var undone = await workspace.UndoRulePatchAsync(Session, result.AppliedCommandId!, "undo_recipe", result.Draft.Version, default);
        Assert.Empty(undone.Frames); Assert.Null(undone.Rules);
    }

    [Fact]
    public async Task RecipeCannotRegisterACandidateWithoutItsImageRequirement()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default); var candidate = Assert.Single(initial.Candidates);
        var branch = new TemplateRuleBranch("first", new(1, 2),
            [new("page_once", candidate.PageId, TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
                [new("main", TemplateRuleColorSource.Primary)], [new(candidate.FrameId, "main")])]);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [], [new("first", branch)], "未指定图片", null), null, null);
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() => service.InterpretAsync(Session,
            new("missing_requirement", initial.Version, "@1 每款一次", []), default));
        Assert.Empty((await workspace.GetRulesAsync(Session, default)).Frames);
    }

    [Fact]
    public async Task FirstRuleCannotExpandToAnUnmentionedPage()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path, pages: 2);
        var initial = await workspace.GetRulesAsync(Session, default);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(initial.Candidates[1].FrameId, "越界图")], [], "越界", null), null, null);
        await Code("TPL_RULE_AI_SCOPE_VIOLATION", () => service.InterpretAsync(Session,
            new("scope", initial.Version, "@1 用正面图", []), default));
        Assert.Empty((await workspace.GetRulesAsync(Session, default)).Frames);
    }

    [Fact]
    public async Task LateFirstRuleCannotOverwriteAManuallyRegisteredRule()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default); var candidate = Assert.Single(initial.Candidates);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(candidate.FrameId, "迟到的 AI 规则")], [], "迟到", null), new(10, 10, 20, false), null);
        provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.InterpretAsync(Session, new("late_first", initial.Version, "@1 用正面图", []), default);
        await provider.Started.Task;
        var calibration = new TemplateCalibrationService(store, store);
        await calibration.SetSelectionRequirementAsync(Session, candidate.PageId, candidate.Target.FrameId, "人工最新规则", default);
        provider.Release.SetResult();
        await Code("TPL_RULE_VERSION_CONFLICT", () => pending);
        Assert.Equal("人工最新规则", Assert.Single((await workspace.GetRulesAsync(Session, default)).Frames).Description);
    }

    [Fact]
    public async Task CandidateAdditionCannotInjectLayoutOrSyntheticAssets()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, _, _) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default); var candidate = Assert.Single(initial.Candidates);
        var binding = new TemplatePackageFrameBinding(candidate.FrameId, candidate.PageId, [1], 7, "candidate 0",
            true, false, false, false, 255, "norm", "sha256:" + new string('b', 64), "假模正面图",
            [new("asset_id", "invented_asset")], "subject_complete", 1, 0, 0, 0, 0, 99, 100);
        await Code("TPL_RULE_TARGET_REFERENCE_INVALID", () => workspace.ApplyRulePatchAsync(Session,
            new("inject", initial.Version, new([], [], Targets: [new(candidate.FrameId, binding)],
                Source: new("ai", "@1 的框1", "request", "sha256:" + new string('c', 64), TemplateRuleAiProtocol.ModelVersion,
                    TemplateRuleAiProtocol.PromptVersion))), default));
        Assert.Empty((await workspace.GetRulesAsync(Session, default)).Frames);
    }

    [Fact]
    public async Task UndoOfFirstRuleDoesNotEraseALaterManualEdit()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default); var candidate = Assert.Single(initial.Candidates);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion,
            [new(candidate.FrameId, "正面图")], [], "已设置", null), null, null);
        var result = await service.InterpretAsync(Session, new("first_for_undo", initial.Version, "@1 用正面图", []), default);
        var modified = await workspace.ApplyRulePatchAsync(Session, new("manual_later", result.Draft.Version,
            new([new(candidate.FrameId, "人工最新要求")], [])), default);
        await Code("TPL_RULE_UNDO_CONFLICT", () => workspace.UndoRulePatchAsync(Session,
            result.AppliedCommandId!, "undo_modified", modified.Version, default));
        Assert.Equal("人工最新要求", Assert.Single((await workspace.GetRulesAsync(Session, default)).Frames).Description);
    }

    [Fact]
    public async Task CancelledFirstRuleNeverLeavesPlaceholderBindings()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var pending = service.InterpretAsync(Session, new("cancel_first", initial.Version, "@1 用正面图", []), cancellation.Token);
        await provider.Started.Task; cancellation.Cancel();
        await Code("TPL_RULE_AI_RESULT_UNKNOWN", () => pending);
        Assert.Empty((await workspace.GetRulesAsync(Session, default)).Frames);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task MaskedCoveredOrUnprovenSlotsCannotBeRegisteredByFirstText(bool masked, bool covered, bool pixels)
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await SetupCandidates(store, directory.Path, masked: masked, covered: covered, pixels: pixels);
        var initial = await workspace.GetRulesAsync(Session, default);
        Assert.DoesNotContain(initial.Candidates, candidate => candidate.CanInterpret);
        var result = await service.InterpretAsync(Session, new("unsupported", initial.Version, "@1 用正面图", []), default);
        Assert.True(result.RequiresClarification); Assert.Empty(result.Draft.Frames); Assert.Equal(0, provider.Calls);
    }

    private static async Task<(TemplateWorkspaceService Workspace, TemplateRuleInterpretationService Service, Provider Provider)> SetupCandidates(
        SqliteDesktopStore store, string root, int pages = 1, int framesPerPage = 1, bool masked = false, bool covered = false, bool pixels = true)
    {
        var setup = await Setup(store, root);
        var revision = (await store.GetRevisionAsync(Session.DraftRevisionId, default))!.RemovePage("page");
        var signature = "sha256:" + new string('b', 64); var hash = new string('a', 64);
        for (var index = 0; index < pages; index++)
        {
            var layers = Enumerable.Range(0, framesPerPage).Select(number => new PsdLayerInspection([number], 7 + number,
                "candidate " + number, "smartobject", false, true, true, false, 255, 255, "norm", new(0, 0, 100, 100),
                masked, false, null, new("smart", "embedded.png", "data", "png", "png", 4, false, [0, 0, 100, 0, 100, 100, 0, 100]))
                { PixelEvidence = new(pixels, 10000, covered ? 0 : 10000, covered ? null : new(0, 0, 100, 100)) }).ToArray();
            var resource = "candidate_resource_" + index;
            await store.SaveImportedAsync("candidate_import_" + index, new(resource, "group", "revision", "candidate_" + index + ".psd",
                4, hash, signature, new(hash, 4, new(100, 100, 8, "RGB"), layers.Length, layers.Length, 0, 0, 0, signature,
                    new(1, 1, 2), 1024, null, layers), DateTimeOffset.UtcNow), default);
            revision = revision.AddPage(TemplatePage.Create("page_" + index, "revision", index, "页面" + index,
                TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true, resource));
        }
        await store.SaveDraftRevisionAsync(revision, default);
        return setup;
    }
    [Fact]
    public async Task UnsetPageGuidesManualRulesWithoutReferencePhotosOrProviderCost()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var revision = (await store.GetRevisionAsync(Session.DraftRevisionId, default))!;
        await store.SaveDraftRevisionAsync(revision.RemoveFrameBinding("frame"), default);
        var initial = await workspace.GetRulesAsync(Session, default);
        var result = await service.InterpretAsync(Session, new("manual_guidance", initial.Version, "@1 用假模正面图", []), default);
        Assert.True(result.RequiresClarification);
        Assert.Contains("无需导入参考款", result.Message);
        Assert.Contains("本次未发送 AI 请求", result.Message);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(initial.RuleDigest, result.Draft.RuleDigest);
        Assert.Empty(result.Draft.Frames);
    }
    [Theory]
    [InlineData(TemplateRuleRepeat.Once, TemplateRuleColorRange.All, TemplateRuleColorSource.Primary)]
    [InlineData(TemplateRuleRepeat.Once, TemplateRuleColorRange.None, TemplateRuleColorSource.Allocated)]
    [InlineData(TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.None, TemplateRuleColorSource.Allocated)]
    public async Task InexecutableAiRecipeCannotMutateDraftAndStillRecordsUsage(TemplateRuleRepeat repeat,
        TemplateRuleColorRange range, TemplateRuleColorSource source)
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        var branch = new TemplateRuleBranch("branch", new(1, 2),
            [new("recipe", "page", repeat, range, [new("color", source)], [new("frame", "color")])]);
        provider.Result = new(AiProviderOutcome.Completed,
            new(TemplateRuleAiProtocol.ResultVersion, [new("frame", "AI文字")], [new("branch", branch)], "changed", null), new(10, 10, 20, false), null);
        await Code("TPL_RULE_COLOR_ALLOCATION_INVALID", () => service.InterpretAsync(Session, new("invalid", initial.Version, "@1 改规则", []), default));
        Assert.Equal(initial.RuleDigest, (await workspace.GetRulesAsync(Session, default)).RuleDigest);
        Assert.Equal("正面", (await workspace.GetRulesAsync(Session, default)).Frames[0].Description);
        var identity = provider.TemplateRulesIdentity;
        var inputDigest = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(provider.Request! with { RequestId = "" }));
        var fingerprint = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(new
        { Version = "template-rule-request-identity/v2", identity.Provider, identity.Model, identity.ConfigurationVersion,
            TaskType = TemplateRuleAiProtocol.RequestVersion, TemplateRuleAiProtocol.ResultVersion, inputDigest }));
        var entry = (await store.FindRequestByFingerprintAsync(fingerprint, default))!;
        Assert.Equal("TerminalError", entry.Status); Assert.Null(entry.ResultReference);
        Assert.Single(await store.LoadUsageAsync(entry.RequestId, default));
        await Code("TPL_RULE_AI_REQUEST_ALREADY_FINISHED", () => service.InterpretAsync(Session, new("second_click", initial.Version, "@1 改规则", []), default));
        Assert.Equal(1, provider.Calls);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedRecipesRequireNonExclusiveCoverageAndPreserveRealStructure(bool exclusive)
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        var branch = new TemplateRuleBranch("branch", new(2, 2),
            [new("each_color", "page", TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.All,
                [new("allocated", TemplateRuleColorSource.Allocated)], [new("frame", "allocated")]),
             new("primary_again", "page", TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
                [new("primary", TemplateRuleColorSource.Primary)], [new("frame", "primary")])], exclusive);
        provider.Result = new(AiProviderOutcome.Completed,
            new(TemplateRuleAiProtocol.ResultVersion, [], [new("branch", branch)], "repeat", null), new(10, 10, 20, false), null);
        if (exclusive)
        {
            await Code("TPL_RULE_COLOR_COVERAGE_INVALID", () => service.InterpretAsync(Session, new("repeat", initial.Version, "@1 逐色再主色一次", []), default));
            Assert.Equal(initial.RuleDigest, (await workspace.GetRulesAsync(Session, default)).RuleDigest);
        }
        else
        {
            var result = await service.InterpretAsync(Session, new("repeat", initial.Version, "@1 逐色再主色一次", []), default);
            Assert.Empty(result.Draft.Diagnostics);
            var revision = (await store.GetRevisionAsync("revision", default))!;
            var evaluated = TemplateRuleEvaluator.Evaluate(revision.RuleWorkspace!.Rules!, revision, new(["red", "wine"], "red", true));
            Assert.Empty(evaluated.Diagnostics); Assert.Equal(3, evaluated.Instances.Length);
            Assert.Equal("red", Assert.Single(evaluated.Instances[0].Colors).VariantId);
            Assert.Equal("wine", Assert.Single(evaluated.Instances[1].Colors).VariantId);
            Assert.Equal("red", Assert.Single(evaluated.Instances[2].Colors).VariantId);
        }
    }
    [Fact]
    public async Task ConfiguredSecondProviderPatchUsesItsOwnSourceMetadata()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        provider.TemplateRulesIdentity = OpenAiTemplateRuleAdapter.Identity;
        await store.GrantAsync(provider.TemplateRulesIdentity.ConsentScope, AiConsentPolicy.Version, default);
        var initial = await workspace.GetRulesAsync(Session, default);
        var result = await service.InterpretAsync(Session, new("other_provider", initial.Version, "@1 的框1 改反面", []), default);
        Assert.Equal("反面", result.Draft.Frames[0].Description);
        var command = (await store.GetRuleCommandAsync("revision", result.AppliedCommandId!, default))!;
        Assert.Equal(OpenAiTemplateRuleAdapter.Identity.ConfigurationVersion, command.Applied!.Source!.ModelVersion);
        Assert.Equal(provider.Request!.PromptVersion, command.Applied.Source.PromptVersion);
    }
    [Fact]
    public async Task ProviderIdentitySeparatesCacheAndPersistsOnlyBoundedMetadata()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var view = await workspace.GetRulesAsync(Session, default);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion, [], [], "需要补充", "哪个视角？"), new(10, 10, 20, false), null);
        var first = await service.InterpretAsync(Session, new("first_profile", view.Version, "@1 替换", []), default);
        provider.TemplateRulesIdentity = OpenAiTemplateRuleAdapter.Identity;
        // Ark consent must never authorize a different destination.
        await Code("AI_UPLOAD_NOT_AUTHORIZED", () => service.InterpretAsync(Session, new("denied", view.Version, "@1 替换", []), default));
        await store.GrantAsync(OpenAiTemplateRuleAdapter.Identity.ConsentScope, AiConsentPolicy.Version, default);
        var second = await service.InterpretAsync(Session, new("second_profile", view.Version, "@1 替换", []), default);
        Assert.Equal(2, provider.Calls); Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(OpenAiTemplateRuleAdapter.Identity.ConfigurationVersion, Assert.Single(await store.LoadUsageAsync(second.RequestId, default)).ModelConfigVersion);
        var metadata = await File.ReadAllTextAsync(Path.Combine(directory.Path, "ai/template-rule-results", second.RequestId + ".provider.json"));
        var json = JsonNode.Parse(metadata)!;
        Assert.Equal("openai", json["provider"]!["provider"]!.GetValue<string>());
        Assert.Equal(TemplateRuleAiProtocol.ResultVersion, json["resultSchema"]!.GetValue<string>());
        Assert.DoesNotContain("@1", metadata); Assert.DoesNotContain("test-secret", metadata);
    }
    [Fact]
    public async Task UnknownBlocksDifferentProviderAfterSqliteReopen()
    {
        using var directory = new TestDirectory();
        long version;
        await using (var store = new SqliteDesktopStore(new(directory.Path)))
        {
            var (workspace, service, provider) = await Setup(store, directory.Path);
            version = (await workspace.GetRulesAsync(Session, default)).Version;
            provider.Result = new(AiProviderOutcome.AiResultUnknown, null, null, new("unknown", false, "provider", "unknown"));
            await Code("unknown", () => service.InterpretAsync(Session, new("first", version, "@1 反面", []), default));
        }
        await using var reopened = new SqliteDesktopStore(new(directory.Path));
        var workspace2 = new TemplateWorkspaceService(reopened, reopened, null!, TimeProvider.System);
        var provider2 = new Provider { TemplateRulesIdentity = OpenAiTemplateRuleAdapter.Identity };
        await reopened.GrantAsync(provider2.TemplateRulesIdentity.ConsentScope, AiConsentPolicy.Version, default);
        var service2 = new TemplateRuleInterpretationService(workspace2, provider2, reopened, reopened,
            new AtomicFileStore(new LocalAppPathPolicy(directory.Path)), TimeProvider.System, reopened);
        await Code("TPL_RULE_AI_RESULT_UNKNOWN", () => service2.InterpretAsync(Session, new("different", version, "@1 反面", []), default));
        Assert.Equal(0, provider2.Calls);
    }
    [Theory]
    [InlineData(10_000_000, 0)]
    [InlineData(0, 2_000_000)]
    [InlineData(9_000_000, 1_600_000)]
    public async Task RuleInterpretationHonorsPersistedProductionBudgetBeforeProvider(int input, int output)
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var now = DateTimeOffset.UtcNow;
        await store.RecordRequestAsync(new("previous", "sha256:" + new string('f', 64), "tplrules_revision", "template_rules", "Queued", null, now, now), default);
        await store.RecordUsageAsync(new("usage_previous", "previous", TemplateRuleAiProtocol.ModelVersion, input, output, false, 0, null, null, now), default);
        var initial = await workspace.GetRulesAsync(Session, default);
        await Code("AI_BUDGET_HARD_STOP", () => service.InterpretAsync(Session, new("budget", initial.Version, "@1 反面", []), default));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(initial.RuleDigest, (await workspace.GetRulesAsync(Session, default)).RuleDigest);
    }
    [Fact]
    public async Task AuthorizedTextPatchUsesExistingLedgerAndUsageAndCanBeUndone()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var draft = await workspace.GetRulesAsync(Session, default);
        var result = await service.InterpretAsync(Session, new("command", draft.Version, "@1 的框1 换成反面", []), default);
        Assert.False(result.RequiresClarification); Assert.Equal("反面", result.Draft.Frames[0].Description);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("frame", Assert.Single(provider.Request!.AllowedTargets).FrameId);
        Assert.Single(await store.LoadUsageAsync(result.RequestId, default));
        var restored = await workspace.UndoRulePatchAsync(Session, result.AppliedCommandId!, "undo", result.Draft.Version, default);
        Assert.Equal("正面", restored.Frames[0].Description);
    }
    [Fact]
    public async Task RuleBudgetReservationSurvivesReopenAndTerminalOrUnknownOutcomes()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        await using (var store = new SqliteDesktopStore(new(directory.Path)))
        {
            await store.InitializeAsync();
            for (var index = 0; index < AiAnalysisProductionPolicy.MaxRequestsPerBatch; index++)
            {
                var entry = RuleEntry(index, now);
                await store.RecordRequestAsync(entry, default);
                await store.UpdateRequestStatusAsync(entry.RequestId, "Queued", "Uploading", null, now, default);
                await store.UpdateRequestStatusAsync(entry.RequestId, "Uploading", "Analysing", null, now, default);
                await store.UpdateRequestStatusAsync(entry.RequestId, "Analysing", "TerminalError", null, now, default);
            }
        }
        await using var reopened = new SqliteDesktopStore(new(directory.Path));
        var blocked = RuleEntry(AiAnalysisProductionPolicy.MaxRequestsPerBatch, now);
        await reopened.RecordRequestAsync(blocked, default);
        await Code("AI_BUDGET_HARD_STOP", () => reopened.UpdateRequestStatusAsync(blocked.RequestId, "Queued", "Uploading", null, now, default));
        Assert.Equal("Queued", (await reopened.FindRequestByFingerprintAsync(blocked.Fingerprint, default))!.Status);
    }
    [Fact]
    public async Task RuleAdmissionIsAtomicAcrossStoresAndDoesNotChangeLegacyAdmission()
    {
        using var directory = new TestDirectory();
        await using var first = new SqliteDesktopStore(new(directory.Path)); await first.InitializeAsync();
        await using var second = new SqliteDesktopStore(new(directory.Path));
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < AiAnalysisProductionPolicy.MaxConcurrentRequests + 1; index++)
            await first.RecordRequestAsync(RuleEntry(index, now) with { BatchId = "tplrules_revision_" + index }, default);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, AiAnalysisProductionPolicy.MaxConcurrentRequests + 1).Select(async index =>
        {
            var store = index % 2 == 0 ? first : second;
            try { await store.UpdateRequestStatusAsync(RuleEntry(index, now).RequestId, "Queued", "Uploading", null, now, default); return "allowed"; }
            catch (TemplateRuleViolationException error) { return error.Code; }
        }));
        Assert.Equal(AiAnalysisProductionPolicy.MaxConcurrentRequests, outcomes.Count(value => value == "allowed"));
        Assert.Equal("AI_CONCURRENCY_LIMIT", Assert.Single(outcomes, value => value != "allowed"));
        var legacy = RuleEntry(100, now) with { StyleId = "legacy_style" };
        await first.RecordRequestAsync(legacy, default);
        Assert.Equal("Uploading", (await first.UpdateRequestStatusAsync(legacy.RequestId, "Queued", "Uploading", null, now, default)).Status);
        var admitted = Enumerable.Range(0, outcomes.Length).First(index => outcomes[index] == "allowed");
        var entry = RuleEntry(admitted, now);
        await first.UpdateRequestStatusAsync(entry.RequestId, "Uploading", "Analysing", null, now, default);
        await first.UpdateRequestStatusAsync(entry.RequestId, "Analysing", "RetryableError", null, now, default);
        Assert.Equal("TPL_RULE_AI_REQUEST_ALREADY_FINISHED", (await Assert.ThrowsAsync<InvalidOperationException>(() => first.UpdateRequestStatusAsync(entry.RequestId, "RetryableError", "Queued", null, now, default))).Message);
    }
    private static AiRequestLedgerEntry RuleEntry(int index, DateTimeOffset now) => new("rule_budget_" + index,
        TemplateRuleCodec.Hash("budget-fixture-" + index), "tplrules_revision", "template_rules", "Queued", null, now, now);
    [Fact]
    public async Task MissingConsentAndPrivatePathsNeverReachProvider()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path, grant: false);
        var draft = await workspace.GetRulesAsync(Session, default);
        await Code("AI_UPLOAD_NOT_AUTHORIZED", () => service.InterpretAsync(Session, new("first", draft.Version, "@1 反面", []), default));
        await Code("TPL_RULE_MESSAGE_CONTAINS_PRIVATE_DATA", () => service.InterpretAsync(Session, new("private", draft.Version, @"@1 C:\private\photos", []), default));
        await Code("TPL_RULE_REFERENCE_INACTIVE", () => service.InterpretAsync(Session, new("deleted", draft.Version, "@99 反面", []), default));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(draft.RuleDigest, (await workspace.GetRulesAsync(Session, default)).RuleDigest);
    }
    [Fact]
    public async Task UnknownAndDoubleClickCannotBecomeNewPaidRequests()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        provider.Result = new(AiProviderOutcome.AiResultUnknown, null, null, new("unknown", false, "provider", "unknown"));
        var view = await workspace.GetRulesAsync(Session, default);
        await Code("unknown", () => service.InterpretAsync(Session, new("click_one", view.Version, "@1 反面", []), default));
        await Code("TPL_RULE_AI_RESULT_UNKNOWN", () => service.InterpretAsync(Session, new("click_two", view.Version, "@1 反面", []), default));
        Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task LateResponseCannotOverwriteManualEdit()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = await workspace.GetRulesAsync(Session, default);
        var interpreting = service.InterpretAsync(Session, new("late", initial.Version, "@1 反面", []), default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workspace.ApplyRulePatchAsync(Session, new("manual", initial.Version, new([new("frame", "新手工文字")], [])), default);
        provider.Release.SetResult();
        await Code("TPL_RULE_VERSION_CONFLICT", () => interpreting);
        Assert.Equal("新手工文字", (await workspace.GetRulesAsync(Session, default)).Frames[0].Description);
        Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task ClarificationAndIllegalTargetLeaveDraftUnchanged()
    {
        using var directory = new TestDirectory(); await using var store = new SqliteDesktopStore(new(directory.Path));
        var (workspace, service, provider) = await Setup(store, directory.Path);
        var initial = await workspace.GetRulesAsync(Session, default);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion, [], [], "需要补充", "两个副色中的哪个？"), null, null);
        var result = await service.InterpretAsync(Session, new("question", initial.Version, "@1 放副色", []), default);
        Assert.True(result.RequiresClarification); Assert.Equal(initial.Version, result.Draft.Version);
        provider.Result = new(AiProviderOutcome.Completed, new(TemplateRuleAiProtocol.ResultVersion, [new("outside", "反面")], [], "changed", null), null, null);
        await Code("TPL_RULE_AI_SCOPE_VIOLATION", () => service.InterpretAsync(Session, new("bad", initial.Version, "@1 改反面", []), default));
        Assert.Equal(initial.RuleDigest, (await workspace.GetRulesAsync(Session, default)).RuleDigest);
    }
    [Fact]
    public async Task AdapterSendsOnlyTextWithSeparateStrictSchemaAndNoImageCopies()
    {
        string? sent = null;
        using var http = new HttpClient(new Handler(async (message, token) =>
        {
            sent = await message.Content!.ReadAsStringAsync(token);
            return Response(new(TemplateRuleAiProtocol.ResultVersion, [new("frame", "反面")], [], "已更新框1", null));
        }));
        var adapter = new VolcArkAiAdapter(http, new Credential(), new NoCopies(), new JsonSchemaContractValidator());
        var result = await adapter.InterpretTemplateRulesAsync(Request(), default);
        Assert.Equal(AiProviderOutcome.Completed, result.Outcome); Assert.NotNull(result.Usage);
        var json = JsonNode.Parse(sent!)!;
        Assert.Equal("template_rule_result_v1", json["text"]!["format"]!["name"]!.GetValue<string>());
        Assert.Single(json["input"]![0]!["content"]!.AsArray());
        Assert.DoesNotContain("input_image", sent!, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", sent!, StringComparison.Ordinal);
        var prompt = json["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("colorRange 必须 None", prompt, StringComparison.Ordinal);
        Assert.Contains("恰好把每色各用一次", prompt, StringComparison.Ordinal);
    }
    [Fact]
    public async Task AdapterRejectsUnknownFieldsAndDuplicateJsonProperties()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new JsonObject { ["status"] = "completed", ["output_text"] = "{\"schemaVersion\":\"template-rule-ai-result/v1\",\"frames\":[],\"branches\":[],\"summary\":\"q\",\"clarification\":\"q\",\"sql\":\"drop\"}" }.ToJsonString()) })));
        var adapter = new VolcArkAiAdapter(http, new Credential(), new NoCopies(), new JsonSchemaContractValidator());
        var result = await adapter.InterpretTemplateRulesAsync(Request(), default);
        Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome); Assert.Null(result.Response);
    }
    [Fact]
    public async Task AdapterCancellationAfterUploadIsUnknownNotSafeToResubmit()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (message, _) =>
        { await message.Content!.ReadAsByteArrayAsync(CancellationToken.None); entered.SetResult(); return await release.Task; }));
        var adapter = new VolcArkAiAdapter(http, new Credential(), new NoCopies(), new JsonSchemaContractValidator());
        var pending = adapter.InterpretTemplateRulesAsync(Request(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(AiProviderOutcome.AiResultUnknown, result.Outcome);
            Assert.False(result.Error!.Retryable);
        }
        finally { release.TrySetResult(Response(new(TemplateRuleAiProtocol.ResultVersion, [], [], "澄清", "哪个框？"))); }
    }
    [Fact]
    public async Task AdapterHardDeadlineDoesNotWaitForAnUncooperativeUploadedRequest()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (message, token) => { await message.Content!.ReadAsByteArrayAsync(CancellationToken.None); return await release.Task; }));
        var adapter = new VolcArkAiAdapter(http, new Credential(), new NoCopies(), new JsonSchemaContractValidator(),
            new(RequestTimeout: TimeSpan.FromMilliseconds(80)));
        var result = await adapter.InterpretTemplateRulesAsync(Request(), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AiProviderOutcome.AiResultUnknown, result.Outcome);
        release.SetResult(Response(new(TemplateRuleAiProtocol.ResultVersion, [], [], "澄清", "哪个框？")));
    }
    private static TemplateRuleAiRequest Request() => new(TemplateRuleAiProtocol.RequestVersion, "request", TemplateRuleAiProtocol.PromptVersion,
        TemplateRuleAiProtocol.ModelVersion, 1, "sha256:" + new string('a', 64), "@1 的框1 改为反面",
        [new("page", "页面", false)], [new("page", "frame", "正面")], [new("page", null, 1), new("page", "frame", 1)], null, [new("page", "frame")]);
    private static HttpResponseMessage Response(TemplateRuleAiResponse result) => new(HttpStatusCode.OK)
    { Content = new StringContent(new JsonObject { ["status"] = "completed", ["output_text"] = TemplateRuleCodec.Serialize(result),
        ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 10, ["total_tokens"] = 20 } }.ToJsonString(), Encoding.UTF8, "application/json") };
    private static async Task Code(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<TemplateRuleViolationException>(action)).Code);
    private static async Task<(TemplateWorkspaceService Workspace, TemplateRuleInterpretationService Service, Provider Provider)> Setup(SqliteDesktopStore store, string root, bool grant = true)
    {
        await store.InitializeAsync();
        await store.SaveGroupAsync(TemplateGroup.Create("group", "test", DateTimeOffset.UtcNow), default);
        await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UtcNow), default);
        var signature = "sha256:" + new string('b', 64); var hash = new string('a', 64);
        var inspection = new PsdInspectionDocument(hash, 4, new(100, 100, 8, "RGB"), 0, 0, 0, 0, 0, signature, new(0, 0, 0), 0, null, []);
        await store.SaveImportedAsync("import", new("resource", "group", "revision", "template.psd", 4, hash, signature, inspection, DateTimeOffset.UtcNow), default);
        var revision = (await store.GetRevisionAsync("revision", default))!.AddPage(TemplatePage.Create("page", "revision", 0, "page", TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, true, "resource"))
            .AddFrameBinding(FrameBinding.Create("frame", "page", LayerPath.Create([1]), ExpectedLayerEvidence.Create(7, "target", true, false, false, false, 255, "norm", LayerStructureSignature.Create(signature)), [], SelectionRequirement.Create("正面"), LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 100, 100))));
        await store.SaveDraftRevisionAsync(revision, default);
        if (grant) await store.GrantAsync(AiConsentScopes.AnalysisCopy, AiConsentPolicy.Version, default);
        var workspace = new TemplateWorkspaceService(store, store, null!, TimeProvider.System);
        var provider = new Provider(); var files = new AtomicFileStore(new LocalAppPathPolicy(root));
        return (workspace, new(workspace, provider, store, store, files, TimeProvider.System, store), provider);
    }
    private sealed class Provider : IAiServicePort
    {
        public AiTaskProviderIdentity TemplateRulesIdentity { get; set; } = AiTaskProviderIdentity.LegacyTemplateRules;
        public int Calls { get; private set; }
        public TemplateRuleAiRequest? Request { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; set; }
        public TemplateRuleAiExecution Result { get; set; } = new(AiProviderOutcome.Completed,
            new(TemplateRuleAiProtocol.ResultVersion, [new("frame", "反面")], [], "已更新框1", null), new(10, 10, 20, false), null);
        public async Task<TemplateRuleAiExecution> InterpretTemplateRulesAsync(TemplateRuleAiRequest request, CancellationToken cancellationToken)
        { Calls++; Request = request; Started.TrySetResult(); if (Release is not null) await Release.Task.WaitAsync(cancellationToken); return Result; }
        public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Credential : IAiCredentialSource
    { public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>("test-secret"); }
    private sealed class NoCopies : IAiAnalysisCopySource
    {
        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
