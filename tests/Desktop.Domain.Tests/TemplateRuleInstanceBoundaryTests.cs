using Desktop.Domain.Templates;

namespace Desktop.Domain.Tests;

public sealed class TemplateRuleInstanceBoundaryTests
{
    [Fact]
    public void CandidateReferencesAreMonotonicAndDoNotCreateProductionBindings()
    {
        var revision = TemplateRevision.CreateDraft("draft", "group", 1, DateTimeOffset.UnixEpoch,
            [TemplatePage.Create("p", "draft", 0, "page", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource")], ruleWorkspace: TemplateRuleWorkspace.Empty);
        var before = revision.RuleWorkspace!.Reconcile(revision);
        var first = before.ReserveFrames([("p", "candidate_a"), ("p", "candidate_b")]);
        var next = first.ReserveFrames([("p", "candidate_b"), ("p", "candidate_c")]);
        Assert.All(first.Aliases, alias => Assert.Contains(alias, next.Aliases));
        Assert.Equal(next.Aliases.Single(alias => alias.FrameId == "candidate_b").Number + 1,
            next.Aliases.Single(alias => alias.FrameId == "candidate_c").Number);
        Assert.Null(next.Rules);
        Assert.Equal(revision.FrameBindings, revision.WithRuleWorkspace(next).FrameBindings);
        Assert.Equal(next.Aliases.ToArray(), next.ReserveFrames([("p", "candidate_a")]).Aliases.ToArray());
    }

    [Fact]
    public void CandidateReferenceCannotCreateAnUnknownPageAlias()
    {
        var error = Assert.Throws<TemplateRuleViolationException>(() => TemplateRuleWorkspace.Empty
            .ReserveFrames([("unknown_page", "candidate")]));
        Assert.Equal("TPL_RULE_ALIAS_INVALID", error.Code);
    }

    [Fact]
    public void NewRulesStillCannotCreateOrReplaceALegacyProductionSnapshot()
    {
        var legacy = Revision(null);
        var existing = BatchTemplateSnapshot.Create("snapshot", "batch", legacy, ["p"], DateTimeOffset.UnixEpoch);
        var ruled = Revision(Rules());
        var create = Assert.Throws<TemplateRuleViolationException>(() =>
            BatchTemplateSnapshot.Create("next", "batch", ruled, ["p"], DateTimeOffset.UnixEpoch));
        var replace = Assert.Throws<TemplateRuleViolationException>(() =>
            existing.ReplaceForDraftBatch("next", ruled, ["p"], false, DateTimeOffset.UnixEpoch));
        Assert.Equal("TPL_RULE_INSTANCE_PIPELINE_REQUIRED", create.Code);
        Assert.Equal(create.Code, replace.Code);
        Assert.Equal("snapshot", existing.SnapshotId);
        Assert.Single(existing.Pages);
    }

    [Fact]
    public void AliasOnlyWorkspaceRetainsExplicitLegacySemantics()
    {
        var revision = Revision(null);
        var snapshot = BatchTemplateSnapshot.Create("snapshot", "batch", revision, ["p"], DateTimeOffset.UnixEpoch);
        Assert.Null(revision.RuleWorkspace!.Rules);
        Assert.Equal("p", Assert.Single(snapshot.Pages).PageId);
    }

    [Fact]
    public void SecondaryLoopHasNoInstancesWithOneColorButRuleFailureIsNotEmptySuccess()
    {
        var rules = Rules();
        var revision = Revision(rules);
        var empty = TemplateRuleEvaluator.Evaluate(rules, revision, new(["primary"], "primary", true));
        Assert.True(empty.CanExpand);
        Assert.Empty(empty.Instances);
        var invalid = TemplateRuleEvaluator.Evaluate(rules, revision, new(["primary"], null, true));
        Assert.False(invalid.CanExpand);
        Assert.Empty(invalid.Instances);
        Assert.NotEmpty(invalid.Diagnostics);
    }

    [Fact]
    public void LogicalKeysUseRecipeAndRepetitionRatherThanPageOrAlias()
    {
        var rules = Rules();
        rules = rules with { Branches = [rules.Branches[0] with
        { Recipes = [rules.Branches[0].Recipes[0], rules.Branches[0].Recipes[0] with { RecipeId = "again" }] }] };
        var result = TemplateRuleEvaluator.Evaluate(rules, Revision(rules), new(["primary", "z", "a"], "primary", true));
        Assert.True(result.CanExpand);
        Assert.Equal(4, result.Instances.Select(instance => instance.Key).Distinct().Count());
        Assert.Equal([0, 1, 0, 1], result.Instances.Select(instance => instance.Key.RepetitionIndex));
        Assert.All(result.Instances, instance => Assert.Equal("p", instance.PageId));
        Assert.Equal(["z", "a", "z", "a"], result.Instances.Select(instance => Assert.Single(instance.Colors).VariantId));
    }

    private static TemplateRuleSet Rules() => new(1, [new("branch", new(1),
        [new("secondary", "p", TemplateRuleRepeat.ColorChunks, TemplateRuleColorRange.Secondary,
            [new("current", TemplateRuleColorSource.Allocated)], [new("frame", "current")])])]);

    private static TemplateRevision Revision(TemplateRuleSet? rules) => TemplateRevision.Restore("revision", "group", 1,
        TemplateRevisionState.Committed, "templates/revision.json", "sha256:" + new string('a', 64), DateTimeOffset.UnixEpoch,
        [TemplateDomainTests.Page("p", "revision", 0)], [TemplateDomainTests.Binding("frame", "p")],
        new(1, rules, [new("p", null, 7), new("p", "frame", 99)]));
}
