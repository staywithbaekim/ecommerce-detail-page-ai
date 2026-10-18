using System.Collections.Immutable;
using Desktop.Domain.Templates;

namespace Desktop.Domain.Tests;

public sealed class TemplateRuleEvaluatorTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(3, 1, 1)]
    [InlineData(4, 0, 2)]
    [InlineData(5, 1, 2)]
    [InlineData(6, 0, 3)]
    public void R2OddSingleThenPairsEvenAllPairsCoversEachColorOnce(
        int colors, int singlePages, int pairPages)
    {
        var result = TemplateRuleEvaluator.Evaluate(PairingRules(),
            Revision(("single", 1), ("pair", 2)), Colors(colors));

        Assert.True(result.CanExpand);
        Assert.Equal(singlePages, result.Instances.Count(instance => instance.PageId == "single"));
        Assert.Equal(pairPages, result.Instances.Count(instance => instance.PageId == "pair"));
        Assert.Equal(Enumerable.Range(1, colors).Select(index => $"color_{index}"),
            result.Instances.SelectMany(instance => instance.Colors).Select(slot => slot.VariantId));
        Assert.Equal("color_1", result.Instances[0].Colors[0].VariantId);
        Assert.Equal(result.Instances.Length, result.Instances.Select(instance => instance.Key).Distinct().Count());
    }

    [Fact]
    public void R2ZeroColorsDoesNotInventAGroup()
    {
        var result = TemplateRuleEvaluator.Evaluate(PairingRules(),
            Revision(("single", 1), ("pair", 2)), Colors(0));

        AssertBlocked(result, "TPL_RULE_COLOR_DATA_MISSING");
    }

    [Fact]
    public void R1OnePageMayUsePrimaryAndSecondaryWithoutChangingFrameSemantics()
    {
        var revision = Revision(("pair", 2));
        var result = TemplateRuleEvaluator.Evaluate(MixedColorRules(), revision, Colors(2));

        var instance = Assert.Single(result.Instances);
        Assert.True(result.CanExpand);
        Assert.Equal(["color_1", "color_2"], instance.Frames.Select(frame => frame.VariantId));
        Assert.Equal("Choose view 1.", revision.FrameBindings[0].SelectionRequirement.Description);
        Assert.Equal("Choose view 2.", revision.FrameBindings[1].SelectionRequirement.Description);
        Assert.Equal(["pair_frame_1", "pair_frame_2"], instance.Frames.Select(frame => frame.FrameId));
    }

    [Theory]
    [InlineData(1, "TPL_RULE_SECONDARY_MISSING")]
    [InlineData(3, "TPL_RULE_SECONDARY_AMBIGUOUS")]
    public void R1DoesNotSilentlyPickTheFirstSecondary(int colors, string code)
    {
        var result = TemplateRuleEvaluator.Evaluate(MixedColorRules(), Revision(("pair", 2)), Colors(colors));

        AssertBlocked(result, code);
        Assert.Equal("pair", Assert.Single(result.Diagnostics).PageId);
    }

    [Fact]
    public void R1ExplicitIterationResolvesSecondaryAmbiguity()
    {
        var recipe = new TemplatePageRecipe("mixed", "pair", TemplateRuleRepeat.ColorChunks,
            TemplateRuleColorRange.Secondary,
            [new("primary", TemplateRuleColorSource.Primary), new("other", TemplateRuleColorSource.Allocated)],
            [new("pair_frame_1", "primary"), new("pair_frame_2", "other")]);
        var result = TemplateRuleEvaluator.Evaluate(Rules(recipe), Revision(("pair", 2)), Colors(3));

        Assert.True(result.CanExpand);
        Assert.Equal(2, result.Instances.Length);
        Assert.Equal(["color_1", "color_2"], result.Instances[0].Frames.Select(frame => frame.VariantId));
        Assert.Equal(["color_1", "color_3"], result.Instances[1].Frames.Select(frame => frame.VariantId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void R3ExactColorCountSelectsOnlyItsDefinedPage(int count)
    {
        var result = TemplateRuleEvaluator.Evaluate(CountRoutingRules(),
            Revision(("p1", 1), ("p2", 2), ("p3", 3), ("p4", 4)), Colors(count));

        Assert.True(result.CanExpand);
        Assert.Equal($"p{count}", Assert.Single(result.Instances).PageId);
    }

    [Fact]
    public void R3UncoveredFiveColorsDoesNotFallBackToPairing()
    {
        var result = TemplateRuleEvaluator.Evaluate(CountRoutingRules(),
            Revision(("p1", 1), ("p2", 2), ("p3", 3), ("p4", 4)), Colors(5));

        AssertBlocked(result, "TPL_RULE_ROUTE_UNCOVERED");
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    public void R4EachSecondaryGetsOneSameColorFrontBackInstance(int count, int expected)
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(SecondaryFrontBack()),
            Revision(("front_back", 2)), Colors(count));

        Assert.True(result.CanExpand);
        Assert.Equal(expected, result.Instances.Length);
        Assert.Equal(Enumerable.Range(2, expected).Select(index => $"color_{index}"),
            result.Instances.Select(instance => Assert.Single(instance.Colors).VariantId));
        Assert.All(result.Instances, instance =>
        {
            Assert.Equal(2, instance.Frames.Length);
            Assert.Single(instance.Frames.Select(frame => frame.VariantId).Distinct());
            Assert.DoesNotContain(instance.Frames, frame => frame.VariantId == "color_1");
        });
    }

    [Fact]
    public void R4FourSecondaryColorsCannotAlsoMeanTwoInstances()
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(SecondaryFrontBack() with { ExpectedInstanceCount = 2 }),
            Revision(("front_back", 2)), Colors(5));

        AssertBlocked(result, "TPL_RULE_INSTANCE_COUNT_CONFLICT");
        Assert.Equal("front_back", Assert.Single(result.Diagnostics).PageId);
    }

    [Fact]
    public void R5RuleValidationNeedsNoProductOrSizeImageAndExpansionDoesNotInventSizeData()
    {
        var page = TemplatePage.Create("size", "revision", 0, "尺码页",
            TemplatePageContentType.DynamicSizeTable, TemplateGenerationMode.OncePerProduct,
            null, true, "size_resource");
        var revision = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch, [page]);
        var rules = Rules(new TemplatePageRecipe("size_once", "size", TemplateRuleRepeat.Once,
            TemplateRuleColorRange.None, [], []));

        Assert.Empty(TemplateRuleEvaluator.Validate(rules, revision));
        var result = TemplateRuleEvaluator.Evaluate(rules, revision, Colors(0));
        Assert.True(result.CanExpand);
        var instance = Assert.Single(result.Instances);
        Assert.Equal("size", instance.PageId);
        Assert.Empty(instance.Colors);
        Assert.Empty(instance.Frames);
        // Rule validity is not proof of template calibration or production size-source readiness.
        Assert.Null(revision.Pages[0].SizeTableCalibration);
    }

    [Fact]
    public void FrozenSecondaryOrderAndLogicalInstanceKeysAreDeterministic()
    {
        var revision = Revision(("single", 1), ("pair", 2));
        var facts = new TemplateRuleColorFacts(["color_z", "primary", "color_a", "color_b"], "primary", true);
        var first = TemplateRuleEvaluator.Evaluate(PairingRules(), revision, facts);
        var restored = TemplateRuleEvaluator.Evaluate(PairingRules(), revision, facts);

        Assert.True(first.CanExpand);
        Assert.Equal(["primary", "color_z", "color_a", "color_b"],
            first.Instances.SelectMany(instance => instance.Colors).Select(slot => slot.VariantId));
        Assert.Equal(first.Instances.Select(instance => instance.Key), restored.Instances.Select(instance => instance.Key));
        Assert.Equal(first.Instances.SelectMany(instance => instance.Frames),
            restored.Instances.SelectMany(instance => instance.Frames));
    }

    [Fact]
    public void TwoRecipesUsingTheSamePsdHaveDifferentKeys()
    {
        var first = PrimaryOnce("p");
        var rules = Rules(first, first with { RecipeId = "second_use" });
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(1));

        Assert.True(result.CanExpand);
        Assert.Equal(2, result.Instances.Length);
        Assert.NotEqual(result.Instances[0].Key, result.Instances[1].Key);
        Assert.All(result.Instances, instance => Assert.Equal("p", instance.PageId));
    }

    [Fact]
    public void OverlappingConditionsDoNotUseImplicitPriority()
    {
        var rules = Rules(PrimaryOnce("p"));
        rules = rules with { Branches = rules.Branches.Add(rules.Branches[0] with { BranchId = "overlap" }) };
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(1));

        AssertBlocked(result, "TPL_RULE_ROUTE_AMBIGUOUS");
    }

    [Fact]
    public void IncompleteColorChunkDoesNotSilentlyDropRemainingColor()
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(Chunks("pair", 2, TemplateRuleColorRange.All)),
            Revision(("pair", 2)), Colors(3));

        AssertBlocked(result, "TPL_RULE_COLOR_CHUNK_INCOMPLETE");
    }

    [Theory]
    [InlineData(false, "color_1", "TPL_RULE_COLOR_GROUPING_UNRESOLVED")]
    [InlineData(true, null, "TPL_RULE_PRIMARY_INVALID")]
    [InlineData(true, "missing", "TPL_RULE_PRIMARY_INVALID")]
    public void UnresolvedFactsDoNotBecomeMadeUpAssignments(bool resolved, string? primary, string code)
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(PrimaryOnce("p")), Revision(("p", 1)),
            new TemplateRuleColorFacts(["color_1"], primary, resolved));

        AssertBlocked(result, code);
    }

    [Fact]
    public void DuplicateColorsAreRejectedRatherThanDeduplicatedSilently()
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(PrimaryOnce("p")), Revision(("p", 1)),
            new TemplateRuleColorFacts(["color_1", "color_1"], "color_1", true));

        AssertBlocked(result, "TPL_RULE_COLOR_IDS_INVALID");
    }

    [Fact]
    public void CompleteCoverageCountsSlotsNotFrontBackFrames()
    {
        var recipe = SecondaryFrontBack() with { ColorRange = TemplateRuleColorRange.All };
        var rules = Rules(recipe);
        rules = rules with { Branches = [rules.Branches[0] with { RequireCompleteColorCoverage = true }] };
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("front_back", 2)), Colors(3));

        Assert.True(result.CanExpand);
        Assert.Equal(3, result.Instances.Length);
        Assert.Equal(6, result.Instances.Sum(instance => instance.Frames.Length));
    }

    [Fact]
    public void CoverageRejectsMissingAndRepeatedColors()
    {
        var recipe = PrimaryOnce("p");
        var rules = Rules(recipe, recipe with { RecipeId = "duplicate" });
        rules = rules with { Branches = [rules.Branches[0] with { RequireCompleteColorCoverage = true }] };
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(2));

        AssertBlocked(result, "TPL_RULE_COLOR_COVERAGE_INVALID");
    }

    [Fact]
    public void CrossPageOrUnregisteredFrameNeverBecomesAReplacementTarget()
    {
        var recipe = PrimaryOnce("p") with { FrameBindings = [new("other_frame_1", "primary")] };
        var result = TemplateRuleEvaluator.Evaluate(Rules(recipe), Revision(("p", 1), ("other", 1)), Colors(1));

        AssertBlocked(result, "TPL_RULE_FRAME_REFERENCE_INVALID");
        Assert.Contains(result.Diagnostics, item => item.FrameId == "other_frame_1" && item.PageId == "p");
    }

    [Fact]
    public void DeletedPageInAnInactiveBranchStillPreventsPublication()
    {
        var rules = PairingRules();
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("pair", 2)), Colors(2));

        AssertBlocked(result, "TPL_RULE_PAGE_REFERENCE_INVALID");
        Assert.Contains(result.Diagnostics, item => item.BranchId == "odd" && item.PageId == "single");
    }

    [Fact]
    public void EveryRegisteredFrameNeedsExactlyOneColorBinding()
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(PrimaryOnce("p")), Revision(("p", 2)), Colors(1));

        AssertBlocked(result, "TPL_RULE_FRAME_BINDINGS_INCOMPLETE");
    }

    [Fact]
    public void ExcludedCandidateRemainsBoundButDoesNotProduceRuleAssignments()
    {
        var revision = Revision(("p", 1));
        var binding = Assert.Single(revision.FrameBindings);
        var excluded = TemplateRuleWorkspace.Empty.SetCandidateExcluded("p", new string('b', 64),
            "sha256:" + new string('a', 64), 1, [0], true);
        revision = revision.WithRuleWorkspace(excluded);

        Assert.Same(binding, Assert.Single(revision.FrameBindings));
        Assert.Empty(revision.ActiveFrameBindings);
        var rules = Rules(PrimaryOnce("p"));
        rules = rules with { Branches = [rules.Branches[0] with { RequireCompleteColorCoverage = true }] };
        Assert.Empty(TemplateRuleEvaluator.Validate(rules, revision));
        var result = TemplateRuleEvaluator.Evaluate(rules, revision, Colors(1));
        Assert.True(result.CanExpand);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public void AValidEarlierRecipeCannotEscapeALaterRuleFailure()
    {
        var rules = Rules(PrimaryOnce("single"), Chunks("pair", 2, TemplateRuleColorRange.Secondary));
        var result = TemplateRuleEvaluator.Evaluate(rules,
            Revision(("single", 1), ("pair", 2)), Colors(2));

        AssertBlocked(result, "TPL_RULE_COLOR_CHUNK_INCOMPLETE");
    }

    [Fact]
    public void OversizedExpansionIsRejectedNotTruncated()
    {
        var result = TemplateRuleEvaluator.Evaluate(Rules(Chunks("p", 1, TemplateRuleColorRange.All)),
            Revision(("p", 1)), Colors(TemplateRuleEvaluator.MaximumInstances + 1));

        AssertBlocked(result, "TPL_RULE_INSTANCE_LIMIT_EXCEEDED");
    }

    [Theory]
    [InlineData(false, TemplatePageContentType.NormalComposition)]
    [InlineData(true, (TemplatePageContentType)99)]
    public void DisabledOrUnsupportedPageCannotBeSilentlyActivatedByARule(bool enabled, TemplatePageContentType contentType)
    {
        if (!Enum.IsDefined(contentType))
        {
            var error = Assert.Throws<TemplateRuleViolationException>(() => TemplatePage.Create("p", "revision", 0, "invalid", contentType,
                TemplateGenerationMode.OncePerProduct, null, enabled, "resource_p"));
            Assert.Equal("TPL_PAGE_CONTENT_TYPE_INVALID", error.Code);
            return;
        }
        var source = Revision(("p", 1));
        var page = TemplatePage.Create("p", "revision", 0, "invalid", contentType,
            TemplateGenerationMode.OncePerProduct, null, enabled, "resource_p");
        var revision = TemplateRevision.CreateDraft("revision", "group", 1, DateTimeOffset.UnixEpoch,
            [page], source.FrameBindings);

        AssertBlocked(TemplateRuleEvaluator.Evaluate(Rules(PrimaryOnce("p")), revision, Colors(1)),
            "TPL_RULE_PAGE_REFERENCE_INVALID");
    }

    [Fact]
    public void StaticValidationFindsOverlapOutsideTheCurrentProductCount()
    {
        var recipe = PrimaryOnce("p");
        var rules = new TemplateRuleSet(1,
        [
            new("current", new(1, 1), [recipe]),
            new("later", new(5, 9), [recipe]),
            new("overlap", new(6, 8, TemplateRuleColorParity.Even), [recipe])
        ]);

        AssertBlocked(TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(1)),
            "TPL_RULE_ROUTE_AMBIGUOUS");
    }

    [Theory]
    [InlineData("default_branches")]
    [InlineData("null_branch")]
    [InlineData("null_condition")]
    [InlineData("default_recipes")]
    [InlineData("null_recipe")]
    [InlineData("default_slots")]
    [InlineData("null_slot")]
    [InlineData("default_bindings")]
    [InlineData("null_binding")]
    public void MissingRuleCollectionsAndNodesAreDiagnosedNotExecuted(string mutation)
    {
        var recipe = PrimaryOnce("p");
        var rules = Rules(recipe);
        var branch = rules.Branches[0];
        switch (mutation)
        {
            case "default_branches": rules = rules with { Branches = default }; break;
            case "null_branch": rules = rules with { Branches = [null!] }; break;
            case "null_condition": branch = branch with { Condition = null! }; break;
            case "default_recipes": branch = branch with { Recipes = default }; break;
            case "null_recipe": branch = branch with { Recipes = [null!] }; break;
            case "default_slots": recipe = recipe with { ColorSlots = default }; break;
            case "null_slot": recipe = recipe with { ColorSlots = [null!] }; break;
            case "default_bindings": recipe = recipe with { FrameBindings = default }; break;
            case "null_binding": recipe = recipe with { FrameBindings = [null!] }; break;
        }
        if (!rules.Branches.IsDefault && rules.Branches[0] is not null)
        {
            if (!branch.Recipes.IsDefault && branch.Recipes[0] is not null)
                branch = branch with { Recipes = [recipe] };
            rules = rules with { Branches = [branch] };
        }

        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(1));
        Assert.False(result.CanExpand);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public void ImpossibleParityAtIntegerBoundaryDoesNotOverflow()
    {
        var rules = new TemplateRuleSet(1,
            [new("impossible", new(int.MaxValue, null, TemplateRuleColorParity.Even), [PrimaryOnce("p")])]);

        Assert.Contains(TemplateRuleEvaluator.Validate(rules, Revision(("p", 1))),
            diagnostic => diagnostic.Code == "TPL_RULE_CONDITION_INVALID");
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("repeat")]
    [InlineData("range")]
    [InlineData("source")]
    [InlineData("parity")]
    [InlineData("condition")]
    [InlineData("duplicate_recipe")]
    [InlineData("duplicate_branch")]
    [InlineData("duplicate_slot")]
    [InlineData("duplicate_frame")]
    [InlineData("unknown_slot")]
    [InlineData("unused_slot")]
    [InlineData("allocated_once")]
    [InlineData("no_allocated_chunk")]
    [InlineData("empty_branches")]
    [InlineData("negative_count")]
    public void InvalidRulesFailClosed(string mutation)
    {
        var recipe = PrimaryOnce("p");
        var rules = Rules(recipe);
        var branch = rules.Branches[0];
        switch (mutation)
        {
            case "schema": rules = rules with { SchemaVersion = 99 }; break;
            case "repeat": recipe = recipe with { Repeat = (TemplateRuleRepeat)99 }; break;
            case "range": recipe = recipe with { ColorRange = (TemplateRuleColorRange)99 }; break;
            case "source": recipe = recipe with { ColorSlots = [new("primary", (TemplateRuleColorSource)99)] }; break;
            case "parity": branch = branch with { Condition = new(0, null, (TemplateRuleColorParity)99) }; break;
            case "condition": branch = branch with { Condition = new(3, 1) }; break;
            case "duplicate_recipe": branch = branch with { Recipes = [recipe, recipe] }; break;
            case "duplicate_branch": rules = rules with { Branches = [branch, branch] }; break;
            case "duplicate_slot": recipe = recipe with { ColorSlots = [recipe.ColorSlots[0], recipe.ColorSlots[0]] }; break;
            case "duplicate_frame": recipe = recipe with { FrameBindings = [recipe.FrameBindings[0], recipe.FrameBindings[0]] }; break;
            case "unknown_slot": recipe = recipe with { FrameBindings = [new("p_frame_1", "unknown")] }; break;
            case "unused_slot": recipe = recipe with { ColorSlots = recipe.ColorSlots.Add(new("unused", TemplateRuleColorSource.Primary)) }; break;
            case "allocated_once": recipe = recipe with { ColorSlots = [new("primary", TemplateRuleColorSource.Allocated)] }; break;
            case "no_allocated_chunk": recipe = recipe with { Repeat = TemplateRuleRepeat.ColorChunks, ColorRange = TemplateRuleColorRange.All }; break;
            case "empty_branches": rules = rules with { Branches = [] }; break;
            case "negative_count": recipe = recipe with { ExpectedInstanceCount = -1 }; break;
        }
        if (rules.Branches.Length == 1)
        {
            if (branch.Recipes.Length == 1)
                branch = branch with { Recipes = [recipe] };
            rules = rules with { Branches = [branch] };
        }

        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(("p", 1)), Colors(1));
        Assert.False(result.CanExpand);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Empty(result.Instances);
    }

    private static void AssertBlocked(TemplateRuleEvaluation result, string code)
    {
        Assert.False(result.CanExpand);
        Assert.Empty(result.Instances);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static TemplateRuleSet MixedColorRules() => Rules(new TemplatePageRecipe("mixed", "pair",
        TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
        [new("primary", TemplateRuleColorSource.Primary), new("other", TemplateRuleColorSource.SingleSecondary)],
        [new("pair_frame_1", "primary"), new("pair_frame_2", "other")]));

    private static TemplateRuleSet PairingRules() => new(1,
    [
        new("odd", new(1, null, TemplateRuleColorParity.Odd),
            [PrimaryOnce("single"), Chunks("pair", 2, TemplateRuleColorRange.Secondary)], true),
        new("even", new(2, null, TemplateRuleColorParity.Even),
            [Chunks("pair", 2, TemplateRuleColorRange.All)], true)
    ]);

    private static TemplateRuleSet CountRoutingRules() => new(1, Enumerable.Range(1, 4)
        .Select(count => new TemplateRuleBranch($"count_{count}", new(count, count),
            [Chunks($"p{count}", count, TemplateRuleColorRange.All)], true)).ToImmutableArray());

    private static TemplatePageRecipe PrimaryOnce(string page) => new($"once_{page}", page,
        TemplateRuleRepeat.Once, TemplateRuleColorRange.None,
        [new("primary", TemplateRuleColorSource.Primary)], [new($"{page}_frame_1", "primary")]);

    private static TemplatePageRecipe Chunks(string page, int slots, TemplateRuleColorRange range) => new(
        $"chunks_{page}", page, TemplateRuleRepeat.ColorChunks, range,
        Enumerable.Range(1, slots).Select(index => new TemplateRuleColorSlot($"slot_{index}",
            TemplateRuleColorSource.Allocated)).ToImmutableArray(),
        Enumerable.Range(1, slots).Select(index => new TemplateFrameColorBinding($"{page}_frame_{index}",
            $"slot_{index}")).ToImmutableArray());

    private static TemplatePageRecipe SecondaryFrontBack() => new("each_secondary", "front_back",
        TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.Secondary,
        [new("current", TemplateRuleColorSource.Allocated)],
        [new("front_back_frame_1", "current"), new("front_back_frame_2", "current")]);

    private static TemplateRuleSet Rules(params TemplatePageRecipe[] recipes) =>
        new(1, [new("default_branch", new(0), [.. recipes])]);

    private static TemplateRuleColorFacts Colors(int count) => new(
        Enumerable.Range(1, count).Select(index => $"color_{index}").ToImmutableArray(),
        count == 0 ? null : "color_1", true);

    private static TemplateRevision Revision(params (string Page, int Frames)[] pages) => TemplateRevision.CreateDraft(
        "revision", "group", 1, DateTimeOffset.UnixEpoch,
        pages.Select((page, index) => TemplateDomainTests.Page(page.Page, "revision", index)),
        pages.SelectMany(page => Enumerable.Range(1, page.Frames).Select(index => FrameBinding.Create(
            $"{page.Page}_frame_{index}", page.Page, LayerPath.Create([index]),
            ExpectedLayerEvidence.Create(index, $"target_{index}", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))), [],
            SelectionRequirement.Create($"Choose view {index}."),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 600, 800))))));
}
