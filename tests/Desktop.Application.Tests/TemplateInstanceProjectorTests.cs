using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateInstanceProjectorTests
{
    private static readonly string[] PageIds = ["p", "other", "size"];
    private static readonly string[] CompositionPageIds = ["p", "other"];
    private static readonly string[] Views = ["front", "back"];
    private static readonly string[] AssetViews = ["front", "back", "alternate"];
    private static readonly string[] ColorIds = ["primary", "color_z", "color_a"];

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("zh-CN")]
    public void SameFrozenFactsProduceIdenticalProjectionRegardlessOfMapOrderOrCulture(string culture)
    {
        var request = Request();
        var before = JsonSerializer.Serialize(request);
        var expected = TemplateInstanceProjector.Project(request);
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var reordered = request with { Facts = request.Facts with
            {
                Assets = [.. request.Facts.Assets.Reverse()], Selections = [.. request.Facts.Selections.Reverse()],
                Variants = [.. request.Facts.Variants.Reverse().Select(variant => variant with { AssetIds = [.. variant.AssetIds.Reverse()] })]
            } };
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(TemplateInstanceProjector.Project(reordered)));
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(TemplateInstanceProjector.Project(Request())));
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        finally { CultureInfo.CurrentCulture = prior; }
        Assert.Equal(TemplateInstanceProjectionStatus.Projected, expected.Status);
        Assert.Equal([0, 1], expected.Instances.Select(instance => instance.Order));
        Assert.Equal(["color_z", "color_a"], expected.Instances.Select(instance => Assert.Single(instance.Slots).VariantId));
        Assert.All(expected.Instances, instance =>
        {
            Assert.Equal("batch", instance.BatchId);
            Assert.Equal("style", instance.StyleId);
            Assert.Equal("snapshot", instance.SnapshotId);
            Assert.Equal("revision", instance.RevisionId);
            Assert.Equal(request.Scope.RuleDigest, instance.RuleDigest);
            Assert.Equal(expected.InputFactsDigest, instance.InputFactsDigest);
            Assert.Equal("p", instance.SourcePageId);
            Assert.Equal("resource_p", instance.TemplateResourceId);
            Assert.Equal("branch", instance.BranchId);
            Assert.Equal("secondary", instance.RecipeId);
            Assert.Equal(instance.Order, instance.RepetitionIndex);
            Assert.Matches("^instance_[0-9a-f]{64}$", instance.PageInstanceId);
            Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, instance.Status);
            Assert.Empty(instance.BlockingReasons);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecipesRepetitionsAndPagesNeverCollapseEvenWhenSharingOnePsd(bool samePsd)
    {
        var request = Request(Rules(Secondary(), Secondary() with { RecipeId = "again" }, Secondary("other") with { RecipeId = "other_recipe" }));
        if (samePsd)
        {
            var pages = request.Revision.Pages.Select(page => TemplatePage.Create(page.PageId, "revision", page.Order,
                page.Name, page.ContentType, page.GenerationMode, page.VariantCapacity, page.Enabled, "one_psd"));
            request = WithDraft(request, TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
                pages, request.Revision.FrameBindings, request.Revision.RuleWorkspace));
        }
        var result = TemplateInstanceProjector.Project(request);
        Assert.Equal(6, result.Instances.Length);
        Assert.Equal(6, result.Instances.Select(instance => instance.PageInstanceId).Distinct().Count());
        Assert.Equal(["p", "p", "p", "p", "other", "other"], result.Instances.Select(instance => instance.SourcePageId));
        Assert.Equal([0, 1, 2, 3, 4, 5], result.Instances.Select(instance => instance.Order));
        Assert.Equal([0, 1, 0, 1, 0, 1], result.Instances.Select(instance => instance.RepetitionIndex));
        Assert.Equal(samePsd ? 1 : 2, result.Instances.Select(instance => instance.TemplateResourceId).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    public void OddPrimaryThenPairsAndEvenPairsKeepCoverageThroughAssetPreflight(int colors, int count)
    {
        var primary = new TemplatePageRecipe("primary_once", "p", TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
            [new("main", TemplateRuleColorSource.Primary)], [new("p_front", "main"), new("p_back", "main")]);
        var pairs = new TemplatePageRecipe("pairs", "other", TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.All,
            [new("left", TemplateRuleColorSource.Allocated), new("right", TemplateRuleColorSource.Allocated)],
            [new("other_front", "left"), new("other_back", "right")]);
        var request = Request(new(1,
        [
            new("odd", new(1, null, TemplateRuleColorParity.Odd), [primary, pairs with { ColorRange = TemplateRuleColorRange.Secondary }], true),
            new("even", new(2, null, TemplateRuleColorParity.Even), [pairs], true)
        ]), colors);
        var result = TemplateInstanceProjector.Project(request);
        Assert.Equal(count, result.Instances.Length);
        Assert.Equal(request.Facts.Colors.OrderedVariantIds,
            result.Instances.SelectMany(instance => instance.Slots).Select(slot => slot.VariantId));
        Assert.All(result.Instances, instance =>
        {
            Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, instance.Status);
            Assert.All(instance.Frames, frame => Assert.StartsWith(frame.VariantId + "_", frame.AssetId));
        });
    }

    [Fact]
    public void StableInstanceOrderFollowsRecipesNotSourcePageOrder()
    {
        var request = Request(Rules(Secondary("other"), Secondary() with { RecipeId = "last" }));
        var result = TemplateInstanceProjector.Project(request);
        Assert.Equal(["other", "other", "p", "p"], result.Instances.Select(instance => instance.SourcePageId));
        Assert.Equal([0, 1, 2, 3], result.Instances.Select(instance => instance.Order));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("snapshot")]
    [InlineData("revision")]
    [InlineData("branch")]
    [InlineData("recipe")]
    [InlineData("page")]
    [InlineData("color_order")]
    public void LogicalIdentityBindsItsScopeAndFrozenColorAllocation(string change)
    {
        var original = Request();
        var changed = change switch
        {
            "batch" => original with { Scope = original.Scope with { BatchId = "other_batch" } },
            "style" => Request(style: "other_style"),
            "snapshot" => original with { Scope = original.Scope with { SnapshotId = "other_snapshot" } },
            "revision" => Request(revisionId: "other_revision"),
            "branch" => Request(new(1, [new("other_branch", new(0), [Secondary()])])),
            "recipe" => Request(Rules(Secondary() with { RecipeId = "other_recipe" })),
            "page" => Request(Rules(Secondary("other"))),
            "color_order" => original with { Facts = original.Facts with { Colors = new(["primary", "color_a", "color_z"], "primary", true) } },
            _ => throw new InvalidOperationException()
        };
        var before = TemplateInstanceProjector.Project(original);
        var after = TemplateInstanceProjector.Project(changed);
        Assert.Equal(TemplateInstanceProjectionStatus.Projected, after.Status);
        Assert.Empty(before.Instances.Select(instance => instance.PageInstanceId).Intersect(after.Instances.Select(instance => instance.PageInstanceId)));
    }

    [Fact]
    public void AliasesWorkspaceVersionAndSourcePathsAreNotExecutionIdentityOrInputFacts()
    {
        var original = Request();
        var draft = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
            original.Revision.Pages, original.Revision.FrameBindings,
            original.Revision.RuleWorkspace! with { Version = 42, Aliases = [new("p", null, 90), new("p", "p_front", 88)] });
        var changed = WithDraft(original, draft);
        changed = changed with { Facts = changed.Facts with
        {
            Assets = [.. changed.Facts.Assets.Select(asset => SourceAssetMetadata.CreateReadable(asset.AssetId,
                asset.StyleId, "moved/" + asset.FileName, asset.FileName, asset.Length, asset.Sha256,
                asset.PixelWidth, asset.PixelHeight, asset.Format, asset.MimeType))]
        } };
        Assert.Equal(JsonSerializer.Serialize(TemplateInstanceProjector.Project(original)),
            JsonSerializer.Serialize(TemplateInstanceProjector.Project(changed)));
    }

    [Fact]
    public void MissingSecondaryBackBlocksOnlyThatInstanceWithoutSubstitutingPrimaryOrAnotherColor()
    {
        var request = Request();
        request = request with { Facts = request.Facts with
        { Selections = [.. request.Facts.Selections.Where(selection => selection.FrameId != "p_back" || selection.VariantId != "color_z")] } };
        var result = TemplateInstanceProjector.Project(request);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Instances.Length);
        var blocked = result.Instances[0];
        Assert.Equal(TemplateInstancePreflightStatus.BusinessBlocked, blocked.Status);
        Assert.Equal(new("TPL_INSTANCE_SELECTION_MISSING", "p_back", "color_z"), Assert.Single(blocked.BlockingReasons));
        Assert.Null(blocked.Frames.Single(frame => frame.FrameId == "p_back").AssetId);
        Assert.Equal("color_z_front", blocked.Frames.Single(frame => frame.FrameId == "p_front").AssetId);
        Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, result.Instances[1].Status);
        Assert.All(result.Instances, instance =>
        {
            var color = Assert.Single(instance.Slots).VariantId;
            Assert.All(instance.Frames, frame => Assert.Equal(color, frame.VariantId));
        });
    }

    [Fact]
    public void OverrideChangesOnlyTheExactRecipeRepetitionAndFrameAndKeepsLogicalIdsStable()
    {
        var request = Request(Rules(Secondary(), Secondary() with { RecipeId = "again" }));
        var original = TemplateInstanceProjector.Project(request);
        var manual = new TemplateInstanceSelectionOverride("p", new("branch", "secondary", 0), "p_front", "color_z_alternate", 2);
        var changed = TemplateInstanceProjector.Project(request with { Facts = request.Facts with { Overrides = [manual] } });
        Assert.Equal(original.Instances.Select(instance => instance.PageInstanceId), changed.Instances.Select(instance => instance.PageInstanceId));
        Assert.NotEqual(original.InputFactsDigest, changed.InputFactsDigest);
        var front = changed.Instances[0].Frames.Single(frame => frame.FrameId == "p_front");
        Assert.Equal("color_z_alternate", front.AssetId);
        Assert.Equal(2, front.SelectionRevision);
        Assert.Equal("color_z_back", changed.Instances[0].Frames.Single(frame => frame.FrameId == "p_back").AssetId);
        Assert.Equal("color_a_front", changed.Instances[1].Frames.Single(frame => frame.FrameId == "p_front").AssetId);
        Assert.Equal("color_z_front", changed.Instances[2].Frames.Single(frame => frame.FrameId == "p_front").AssetId);
        var newer = TemplateInstanceProjector.Project(request with { Facts = request.Facts with { Overrides = [manual with { SelectionRevision = 3 }] } });
        Assert.NotEqual(changed.InputFactsDigest, newer.InputFactsDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossColorSelectionOrOverrideBlocksOnlyItsConsumerAndNeverFallsBack(bool manual)
    {
        var request = Request();
        request = request with { Facts = manual ? request.Facts with
        { Overrides = [new("p", new("branch", "secondary", 0), "p_back", "primary_back", 1)] }
        : request.Facts with { Selections = [.. request.Facts.Selections.Select(selection =>
            selection.FrameId == "p_back" && selection.VariantId == "color_z" ? selection with { AssetId = "primary_back" } : selection)] } };
        var result = TemplateInstanceProjector.Project(request);
        var reason = Assert.Single(result.Instances[0].BlockingReasons);
        Assert.Equal("TPL_INSTANCE_ASSET_VARIANT_MISMATCH", reason.Code);
        Assert.Null(result.Instances[0].Frames.Single(frame => frame.FrameId == "p_back").AssetId);
        Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, result.Instances[1].Status);
    }

    [Fact]
    public void MixedPrimarySecondaryPageKeepsDifferentSlotsWithoutForcingOneOutputColor()
    {
        var recipe = new TemplatePageRecipe("mixed", "p", TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.Secondary,
            [new("main", TemplateRuleColorSource.Primary), new("other", TemplateRuleColorSource.Allocated)],
            [new("p_front", "main"), new("p_back", "other")]);
        var result = TemplateInstanceProjector.Project(Request(Rules(recipe)));
        Assert.All(result.Instances, instance =>
        {
            Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, instance.Status);
            Assert.Equal("primary_front", instance.Frames.Single(frame => frame.FrameId == "p_front").AssetId);
        });
        Assert.Equal("color_z_back", result.Instances[0].Frames.Single(frame => frame.FrameId == "p_back").AssetId);
        Assert.Equal("color_a_back", result.Instances[1].Frames.Single(frame => frame.FrameId == "p_back").AssetId);
    }

    [Fact]
    public void EmptyExpansionAllMissingAndRuleFailureHaveDifferentOutcomes()
    {
        var empty = TemplateInstanceProjector.Project(Request(colors: 1));
        Assert.Equal(TemplateInstanceProjectionStatus.Empty, empty.Status);
        Assert.Empty(empty.Instances);
        Assert.Empty(empty.Diagnostics);
        Assert.NotNull(empty.InputFactsDigest);
        var request = Request();
        var missing = TemplateInstanceProjector.Project(request with { Facts = request.Facts with { Selections = [] } });
        Assert.Equal(TemplateInstanceProjectionStatus.Projected, missing.Status);
        Assert.Equal(2, missing.Instances.Length);
        Assert.All(missing.Instances, instance => Assert.Equal(TemplateInstancePreflightStatus.BusinessBlocked, instance.Status));
        AssertRejected(TemplateInstanceProjector.Project(Request(colors: 0)), "TPL_RULE_COLOR_DATA_MISSING");
    }

    [Fact]
    public void EmptyLoopDoesNotIgnoreStaleOverridesAndMissingSelectionCanBeSuppliedPerInstance()
    {
        var request = Request(colors: 1);
        var stale = new TemplateInstanceSelectionOverride("p", new("branch", "secondary", 0), "p_front", "primary_front", 1);
        AssertRejected(TemplateInstanceProjector.Project(request with { Facts = request.Facts with { Overrides = [stale] } }),
            "TPL_INSTANCE_OVERRIDE_INVALID");

        request = Request();
        request = request with { Facts = request.Facts with
        {
            Selections = [.. request.Facts.Selections.Where(selection => selection.FrameId != "p_front" || selection.VariantId != "color_z")]
        } };
        var missing = TemplateInstanceProjector.Project(request);
        var fixedSelection = TemplateInstanceProjector.Project(request with { Facts = request.Facts with
        { Overrides = [stale with { AssetId = "color_z_front" }] } });
        Assert.Equal(missing.Instances[0].PageInstanceId, fixedSelection.Instances[0].PageInstanceId);
        Assert.Equal(TemplateInstancePreflightStatus.BusinessBlocked, missing.Instances[0].Status);
        Assert.Equal(TemplateInstancePreflightStatus.AssetBindingsSatisfied, fixedSelection.Instances[0].Status);
        Assert.NotEqual(missing.InputFactsDigest, fixedSelection.InputFactsDigest);
    }

    [Fact]
    public void SizeOnlyExpansionRemainsPendingProductionValidationWithoutInventedImagesOrNumbers()
    {
        var request = Request(Rules(new TemplatePageRecipe("size_once", "size", TemplateRuleRepeat.Once, TemplateRuleColorRange.None, [], [])), colors: 0);
        var result = TemplateInstanceProjector.Project(request);
        var instance = Assert.Single(result.Instances);
        Assert.Equal(TemplateInstancePreflightStatus.RequiresProductionValidation, instance.Status);
        Assert.Equal("TPL_INSTANCE_SIZE_VALIDATION_REQUIRED", Assert.Single(instance.BlockingReasons).Code);
        Assert.Empty(instance.Slots);
        Assert.Empty(instance.Frames);
        Assert.NotNull(result.InputFactsDigest);
    }

    [Theory]
    [InlineData("overlap", "TPL_RULE_ROUTE_AMBIGUOUS")]
    [InlineData("count", "TPL_RULE_INSTANCE_COUNT_CONFLICT")]
    [InlineData("coverage", "TPL_RULE_COLOR_COVERAGE_INVALID")]
    [InlineData("frame", "TPL_RULE_FRAME_REFERENCE_INVALID")]
    [InlineData("uncovered", "TPL_RULE_ROUTE_UNCOVERED")]
    [InlineData("grouping", "TPL_RULE_COLOR_GROUPING_UNRESOLVED")]
    [InlineData("primary", "TPL_RULE_PRIMARY_INVALID")]
    [InlineData("duplicates", "TPL_RULE_COLOR_IDS_INVALID")]
    public void ConflictsNeverLeakAnEarlierValidInstance(string change, string code)
    {
        var rules = Rules(Secondary());
        rules = change switch
        {
            "overlap" => rules with { Branches = rules.Branches.Add(rules.Branches[0] with { BranchId = "overlap" }) },
            "count" => Rules(Secondary(), Secondary() with { RecipeId = "bad_count", ExpectedInstanceCount = 4 }),
            "coverage" => rules with { Branches = [rules.Branches[0] with { RequireCompleteColorCoverage = true }] },
            "frame" => Rules(Secondary(), Secondary() with { RecipeId = "bad_frame", FrameBindings = [new("other_front", "current"), new("p_back", "current")] }),
            "uncovered" => rules with { Branches = [rules.Branches[0] with { Condition = new(4, 4) }] },
            _ => rules
        };
        var request = Request(rules);
        request = request with { Facts = request.Facts with { Colors = change switch
        {
            "grouping" => request.Facts.Colors with { IsGroupingResolved = false },
            "primary" => request.Facts.Colors with { PrimaryVariantId = "unknown" },
            "duplicates" => request.Facts.Colors with { OrderedVariantIds = ["primary", "primary"] },
            _ => request.Facts.Colors
        } } };
        AssertRejected(TemplateInstanceProjector.Project(request), code);
    }

    [Theory]
    [InlineData("scope", "TPL_INSTANCE_SCOPE_INVALID")]
    [InlineData("revision", "TPL_INSTANCE_REVISION_MISMATCH")]
    [InlineData("digest", "TPL_INSTANCE_RULE_DIGEST_MISMATCH")]
    [InlineData("draft", "TPL_INSTANCE_REVISION_MISMATCH")]
    [InlineData("version", "TPL_INSTANCE_RULE_VERSION_INVALID")]
    [InlineData("legacy", "TPL_INSTANCE_RULES_REQUIRED")]
    [InlineData("tampered", "TPL_INSTANCE_RULE_DIGEST_MISMATCH")]
    public void UncommittedOrMismatchedScopeCannotProduceInstances(string change, string code)
    {
        var request = Request();
        var revision = request.Revision;
        request = change switch
        {
            "scope" => request with { Scope = request.Scope with { BatchId = "../batch" } },
            "revision" => request with { Scope = request.Scope with { RevisionId = "other" } },
            "digest" => request with { Scope = request.Scope with { RuleDigest = "sha256:" + new string('f', 64) } },
            "draft" => request with { Revision = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
                revision.Pages, revision.FrameBindings, revision.RuleWorkspace) },
            _ => request with { Revision = TemplateRevision.Restore("revision", "group", 1, TemplateRevisionState.Committed,
                "templates/revision.json", revision.RuleDigest, DateTimeOffset.UnixEpoch, revision.Pages,
                change == "tampered" ? revision.FrameBindings.Select(frame => frame.Reconfigure(SelectionRequirement.Create("changed"), frame.LayoutIntent)) : revision.FrameBindings,
                change == "legacy" ? null : revision.RuleWorkspace! with { Version = 0 }) }
        };
        // Tampering must keep the original version so the digest check, not the version check, detects it.
        if (change == "tampered") request = request with { Revision = TemplateRevision.Restore("revision", "group", 1,
            TemplateRevisionState.Committed, "templates/revision.json", revision.RuleDigest, DateTimeOffset.UnixEpoch,
            revision.Pages, request.Revision.FrameBindings, revision.RuleWorkspace) };
        AssertRejected(TemplateInstanceProjector.Project(request), code);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("limit")]
    public void MalformedOrUnboundedFactsFailClosed(string change)
    {
        var request = Request();
        var facts = change switch
        {
            "default" => request.Facts with { Overrides = default },
            "null" => request.Facts with { Selections = [null!] },
            "version" => request.Facts with { Version = 0 },
            "limit" => request.Facts with { Assets = [.. Enumerable.Repeat(request.Facts.Assets[0], TemplateInstanceProjector.MaximumFactEntries + 1)] },
            _ => throw new InvalidOperationException()
        };
        AssertRejected(TemplateInstanceProjector.Project(request with { Facts = facts }), "TPL_INSTANCE_FACTS_INVALID");
    }

    [Theory]
    [InlineData("asset_owner", "TPL_INSTANCE_ASSET_OWNERSHIP_INVALID")]
    [InlineData("asset_duplicate", "TPL_INSTANCE_ASSET_OWNERSHIP_INVALID")]
    [InlineData("variant_missing", "TPL_INSTANCE_VARIANTS_INVALID")]
    [InlineData("variant_duplicate", "TPL_INSTANCE_VARIANTS_INVALID")]
    [InlineData("asset_multicolor", "TPL_INSTANCE_VARIANT_ASSETS_INVALID")]
    [InlineData("asset_unknown", "TPL_INSTANCE_ASSET_REFERENCE_INVALID")]
    [InlineData("selection_duplicate", "TPL_INSTANCE_SELECTION_INVALID")]
    [InlineData("frame_unknown", "TPL_INSTANCE_SELECTION_INVALID")]
    [InlineData("override_unknown", "TPL_INSTANCE_OVERRIDE_INVALID")]
    [InlineData("override_page", "TPL_INSTANCE_OVERRIDE_INVALID")]
    [InlineData("override_duplicate", "TPL_INSTANCE_OVERRIDE_INVALID")]
    [InlineData("override_version", "TPL_INSTANCE_OVERRIDE_INVALID")]
    public void InvalidReferencesAreNotSilentlyDroppedOrGuessed(string change, string code)
    {
        var request = Request();
        var manual = new TemplateInstanceSelectionOverride("p", new("branch", "secondary", 0), "p_front", "color_z_front", 1);
        var facts = change switch
        {
            "asset_owner" => request.Facts with { Assets = request.Facts.Assets.SetItem(0, Asset("primary_front", "other_style")) },
            "asset_duplicate" => request.Facts with { Assets = request.Facts.Assets.Add(request.Facts.Assets[0]) },
            "variant_missing" => request.Facts with { Variants = request.Facts.Variants.RemoveAt(0) },
            "variant_duplicate" => request.Facts with { Variants = request.Facts.Variants.Add(request.Facts.Variants[0]) },
            "asset_multicolor" => request.Facts with { Variants = request.Facts.Variants.SetItem(1,
                request.Facts.Variants[1] with { AssetIds = request.Facts.Variants[1].AssetIds.Add("primary_front") }) },
            "asset_unknown" => request.Facts with { Selections = request.Facts.Selections.SetItem(0, request.Facts.Selections[0] with { AssetId = "unknown" }) },
            "selection_duplicate" => request.Facts with { Selections = request.Facts.Selections.Add(request.Facts.Selections[0]) },
            "frame_unknown" => request.Facts with { Selections = request.Facts.Selections.Add(new("unknown", "primary", "primary_front")) },
            "override_unknown" => request.Facts with { Overrides = [manual with { InstanceKey = new("branch", "secondary", 99) }] },
            "override_page" => request.Facts with { Overrides = [manual with { SourcePageId = "other" }] },
            "override_duplicate" => request.Facts with { Overrides = [manual, manual] },
            "override_version" => request.Facts with { Overrides = [manual with { SelectionRevision = 0 }] },
            _ => throw new InvalidOperationException()
        };
        AssertRejected(TemplateInstanceProjector.Project(request with { Facts = facts }), code);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("dimensions")]
    [InlineData("selection")]
    [InlineData("membership")]
    public void InputDigestCoversAssetAndSelectionFactsButDoesNotReassignLogicalInstances(string change)
    {
        var request = Request();
        var facts = change switch
        {
            "version" => request.Facts with { Version = 2 },
            "hash" => request.Facts with { Assets = request.Facts.Assets.SetItem(0, Asset("primary_front", hash: new string('b', 64))) },
            "dimensions" => request.Facts with { Assets = request.Facts.Assets.SetItem(0, Asset("primary_front", width: 901)) },
            "selection" => request.Facts with { Selections = [.. request.Facts.Selections.Where(selection => selection.FrameId != "p_back")] },
            "membership" => request.Facts with { Variants = request.Facts.Variants.SetItem(0,
                request.Facts.Variants[0] with { AssetIds = request.Facts.Variants[0].AssetIds.Remove("primary_alternate") }) },
            _ => throw new InvalidOperationException()
        };
        var before = TemplateInstanceProjector.Project(request);
        var after = TemplateInstanceProjector.Project(request with { Facts = facts });
        Assert.Equal(TemplateInstanceProjectionStatus.Projected, after.Status);
        Assert.NotEqual(before.InputFactsDigest, after.InputFactsDigest);
        Assert.Equal(before.Instances.Select(instance => instance.PageInstanceId), after.Instances.Select(instance => instance.PageInstanceId));
    }

    private static void AssertRejected(TemplateInstanceProjectionResult result, string code)
    {
        Assert.Equal(TemplateInstanceProjectionStatus.Rejected, result.Status);
        Assert.Empty(result.Instances);
        Assert.Null(result.InputFactsDigest);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static TemplateInstanceProjectionRequest Request(TemplateRuleSet? rules = null, int colors = 3,
        string style = "style", string revisionId = "revision")
    {
        rules ??= Rules(Secondary());
        var pages = PageIds.Select((id, index) => TemplatePage.Create(id, revisionId, index,
            id, id == "size" ? TemplatePageContentType.DynamicSizeTable : TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, "resource_" + id));
        var bindings = CompositionPageIds.SelectMany(page => Views.Select((view, index) =>
            FrameBinding.Create(page + "_" + view, page, LayerPath.Create([index + 1]),
                ExpectedLayerEvidence.Create(index + 1, view, true, false, false, false, 255, "normal",
                    LayerStructureSignature.Create("sha256:" + new string('a', 64))), [], SelectionRequirement.Create(view),
                LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 600, 800)))));
        var draft = TemplateRevision.CreateDraft(revisionId, "group", 1, DateTimeOffset.UnixEpoch, pages, bindings,
            new(1, rules, []));
        var revision = Commit(draft);
        var variants = Enumerable.Range(0, colors).Select(index => index < ColorIds.Length ? ColorIds[index] : $"color_{index}").ToImmutableArray();
        var assets = variants.SelectMany(variant => AssetViews.Select(view => Asset(variant + "_" + view, style))).ToImmutableArray();
        var groups = variants.Select(variant => new TemplateInstanceVariantAssets(variant,
            [.. assets.Where(asset => asset.AssetId.StartsWith(variant + "_", StringComparison.Ordinal)).Select(asset => asset.AssetId)])).ToImmutableArray();
        var selections = variants.SelectMany(variant => revision.FrameBindings.Select(frame => new TemplateInstanceFrameSelection(
            frame.FrameId, variant, variant + (frame.FrameId.EndsWith("front", StringComparison.Ordinal) ? "_front" : "_back")))).ToImmutableArray();
        return new(new("batch", style, "snapshot", revisionId, revision.RuleDigest!), revision,
            new(1, new(variants, variants.IsEmpty ? null : variants[0], true), assets, groups, selections, []));
    }

    private static TemplateInstanceProjectionRequest WithDraft(TemplateInstanceProjectionRequest request, TemplateRevision draft)
    {
        var revision = Commit(draft);
        return request with { Revision = revision, Scope = request.Scope with { RevisionId = revision.RevisionId, RuleDigest = revision.RuleDigest! } };
    }

    private static TemplateRevision Commit(TemplateRevision draft) => TemplateRevision.Restore(draft.RevisionId, draft.GroupId,
        1, TemplateRevisionState.Committed, "templates/revision.json", TemplateRuleCodec.BusinessDigest(draft),
        DateTimeOffset.UnixEpoch, draft.Pages, draft.FrameBindings, draft.RuleWorkspace);

    private static SourceAssetMetadata Asset(string id, string style = "style", string? hash = null, int width = 900) =>
        SourceAssetMetadata.CreateReadable(id, style, id + ".jpg", id + ".jpg", 100, hash ?? new string('a', 64),
            width, 1200, SourceImageFormat.Jpeg, "image/jpeg");

    private static TemplateRuleSet Rules(params TemplatePageRecipe[] recipes) => new(1, [new("branch", new(0), [.. recipes])]);

    private static TemplatePageRecipe Secondary(string page = "p") => new("secondary", page, TemplateRuleRepeat.ColorChunks,
        TemplateRuleColorRange.Secondary, [new("current", TemplateRuleColorSource.Allocated)],
        [new(page + "_front", "current"), new(page + "_back", "current")]);
}
