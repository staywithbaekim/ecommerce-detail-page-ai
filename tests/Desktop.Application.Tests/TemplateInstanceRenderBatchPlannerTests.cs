using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplateInstanceRenderBatchPlannerTests
{
    [Fact]
    public void SingleInstanceUsesExistingFactoryAndNeverCreatesExecutionOrOutput()
    {
        var request = Fixture(recipes: 1, colors: 1);
        var result = Planner().Build(request);
        Assert.True(result.CanContinue);
        Assert.Equal(TemplateInstanceRenderBatchStatus.Ready, result.Status);
        Assert.Equal("template-instance-render-batch/v2", result.SchemaVersion);
        Assert.True(result.HasCompleteMembership);
        Assert.Equal(TemplateInstanceBatchMemberStatus.Plannable, Assert.Single(result.Members).Status);
        Assert.Equal(request.BatchId, result.BatchId);
        Assert.Equal(request.StyleId, result.StyleId);
        Assert.Equal(request.RevisionId, result.RevisionId);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(new TemplateInstanceRenderPlanFactory(Validator()).Build(request.Instances[0].PlanRequest).RenderPlanJson,
            plan.RenderPlanJson);
        Assert.Equal(plan.PlanDigest, RenderPlanDigest.Compute(plan.RenderPlanJson));
        Assert.StartsWith("sha256:", result.BatchPlanDigest, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", result.InputFactsDigest, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(plan.RenderPlanJson);
        Assert.False(json.RootElement.TryGetProperty("execution_id", out _));
        Assert.False(json.RootElement.GetProperty("document").TryGetProperty("output_variant_id", out _));
        Assert.Equal(DateTimeOffset.UnixEpoch, json.RootElement.GetProperty("created_at").GetDateTimeOffset());
        Assert.DoesNotContain("ExecutionId", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        // Inputs deliberately point at nonexistent files: planning has no filesystem/output port.
        Assert.False(File.Exists(request.Instances[0].PlanRequest.TemplateResource.InternalRelativePath));
        Assert.False(File.Exists(json.RootElement.GetProperty("output").GetProperty("jpg").GetProperty("file_name").GetString()));
    }

    [Fact]
    public void ExcludedBindingIsPreservedButOnlyActiveTargetsEnterTheRenderPlan()
    {
        var excluded = Planner().Build(Fixture(recipes: 1, colors: 1, twoFrames: true, excludedFrame: "frame"));
        Assert.True(excluded.CanContinue, string.Join(", ", excluded.Diagnostics.Select(item => item.Code)));
        var excludedPlan = Assert.Single(excluded.Plans);
        using (var document = JsonDocument.Parse(excludedPlan.RenderPlanJson))
        {
            Assert.Equal(["another_frame"], document.RootElement.GetProperty("targets")
                .EnumerateArray().Select(target => target.GetProperty("frame_id").GetString()));
        }

        var restored = Planner().Build(Fixture(recipes: 1, colors: 1, twoFrames: true));
        Assert.True(restored.CanContinue);
        using var restoredDocument = JsonDocument.Parse(Assert.Single(restored.Plans).RenderPlanJson);
        Assert.Equal(2, restoredDocument.RootElement.GetProperty("targets").GetArrayLength());
    }

    [Theory]
    [InlineData(2, 1, 2)]
    [InlineData(1, 3, 3)]
    [InlineData(2, 3, 6)]
    public void RecipesAndRepetitionsOnSamePsdAndPageHaveIndependentPlans(int recipes, int colors, int count)
    {
        var result = Planner().Build(Fixture(recipes, colors));
        Assert.True(result.CanContinue);
        Assert.Equal(count, result.Plans.Length);
        Assert.Single(result.Plans.Select(plan => plan.SourcePageId).Distinct());
        Assert.Equal(recipes, result.Plans.Select(plan => plan.RecipeId).Distinct().Count());
        Assert.Equal(colors, result.Plans.Select(plan => plan.RepetitionIndex).Distinct().Count());
        Assert.Equal(count, result.Plans.Select(plan => plan.PageInstanceId).Distinct().Count());
        Assert.Equal(count, result.Plans.Select(plan => plan.PlanId).Distinct().Count());
        Assert.Equal(count, result.Plans.Select(plan => plan.ArtifactSetId).Distinct().Count());
        Assert.Equal(count, result.Plans.Select(plan => plan.PlanDigest).Distinct().Count());
        Assert.All(result.Plans, plan => Assert.True(Validator().Validate(plan.RenderPlanJson).IsValid));
        Assert.Empty(TemplateInstanceRenderBatchCollisionGuard.Validate(result.Plans));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("zh-CN")]
    public void InputAndUnorderedFactOrderCultureAndRepeatedCallsProduceIdenticalResults(string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var request = Fixture();
            var baseline = Planner().Build(request);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var shuffled = request with { Instances = [.. request.Instances.Reverse().Select(input => input with
            {
                Facts = input.Facts with
                {
                    Assets = [.. input.Facts.Assets.Reverse()], Variants = [.. input.Facts.Variants.Reverse()],
                    Selections = [.. input.Facts.Selections.Reverse()]
                },
                PlanRequest = input.PlanRequest with { Assets = input.PlanRequest.Assets.Reverse().ToArray() }
            })] };
            Assert.Equal(Enumerable.Range(0, baseline.Plans.Length), baseline.Plans.Select(plan => plan.StableOrder));
            Assert.Equal("recipe_z", baseline.Plans[0].RecipeId); // StableOrder precedes lexical recipe order.
            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(Planner().Build(shuffled)));
            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(Planner().Build(request)));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void DuplicatePageInstanceIsNotSilentlyDeduplicatedAndDiagnosticsAreOrderIndependent()
    {
        var request = Fixture();
        request = request with { Instances = [.. request.Instances, request.Instances[0]] };
        var result = Planner().Build(request);
        Blocked(result, "TPL_INSTANCE_BATCH_PAGE_INSTANCE_DUPLICATE");
        Assert.Contains(result.Diagnostics, item => item.Code == "TPL_INSTANCE_BATCH_PLAN_ID_COLLISION");
        Assert.Contains(result.Diagnostics, item => item.Code == "TPL_INSTANCE_BATCH_ARTIFACT_SET_ID_COLLISION");
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(Planner().Build(
            request with { Instances = request.Instances.Reverse().ToArray() })));
    }

    [Theory]
    [InlineData("plan_id", "TPL_INSTANCE_BATCH_PLAN_ID_COLLISION")]
    [InlineData("artifact_set_id", "TPL_INSTANCE_BATCH_ARTIFACT_SET_ID_COLLISION")]
    [InlineData("idempotency_key", "TPL_INSTANCE_BATCH_IDEMPOTENCY_KEY_COLLISION")]
    [InlineData("file_name", "TPL_INSTANCE_BATCH_OUTPUT_NAME_COLLISION")]
    public void ProductionCollisionGuardRejectsCollidingActualPlanPayloads(string property, string code)
    {
        var ready = Planner().Build(Fixture());
        Assert.True(ready.CanContinue);
        var first = ready.Plans[0];
        var second = ready.Plans[1];
        var json = JsonNode.Parse(second.RenderPlanJson)!;
        var other = JsonNode.Parse(first.RenderPlanJson)!;
        switch (property)
        {
            case "artifact_set_id": json["output"]![property] = first.ArtifactSetId; break;
            case "file_name": json["output"]!["jpg"]![property] = other["output"]!["jpg"]![property]!.GetValue<string>(); break;
            default: json[property] = other[property]!.GetValue<string>(); break;
        }
        json["plan_digest"] = RenderPlanDigest.Compute(json.ToJsonString());
        second = second with
        {
            PlanId = json["plan_id"]!.GetValue<string>(),
            ArtifactSetId = json["output"]!["artifact_set_id"]!.GetValue<string>(),
            PlanDigest = json["plan_digest"]!.GetValue<string>(), RenderPlanJson = json.ToJsonString()
        };
        Assert.True(Validator().Validate(second.RenderPlanJson).IsValid);
        Assert.Contains(TemplateInstanceRenderBatchCollisionGuard.Validate([first, second]), item => item.Code == code);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("revision")]
    public void MixedScopeCannotEnterBatch(string scope)
    {
        var request = Fixture();
        var other = Fixture(batch: scope == "batch" ? "other_batch" : "batch",
            style: scope == "style" ? "other_style" : "style",
            revisionId: scope == "revision" ? "other_revision" : "revision_batch");
        var result = Planner().Build(request with { Instances = [request.Instances[0], other.Instances[1]] });
        Blocked(result, "TPL_INSTANCE_BATCH_SCOPE_MISMATCH");
    }

    [Theory]
    [InlineData("rule", "TPL_INSTANCE_BATCH_RULE_DIGEST_CONFLICT")]
    [InlineData("facts", "TPL_INSTANCE_BATCH_INPUT_FACTS_CONFLICT")]
    public void SameInstanceWithDifferentDigestsCannotEnterBatch(string change, string code)
    {
        var request = Fixture();
        var first = request.Instances[0];
        var snapshot = first.PlanRequest.Snapshot;
        var different = "sha256:" + new string('e', 64);
        snapshot = change == "rule"
            ? snapshot with { RuleDigest = different, Instances = [.. snapshot.Instances.Select(item => item with { RuleDigest = different })] }
            : snapshot with { InputFactsDigest = different, Instances = [.. snapshot.Instances.Select(item => item with { InputFactsDigest = different })] };
        snapshot = TemplateInstanceProjectionSnapshotCodec.WithVersion(snapshot, 1);
        TemplateInstanceProjectionSnapshotCodec.Validate(snapshot); // Valid E1 checksum does not bypass E2 fact binding.
        var altered = first with { PlanRequest = first.PlanRequest with { Snapshot = snapshot } };
        Blocked(Planner().Build(request with { Instances = [first, altered] }), code);
    }

    [Fact]
    public void DifferentSnapshotsForSameLogicalPositionFailClosedEvenWhenInstanceIdsDiffer()
    {
        var request = Fixture();
        var other = Fixture(snapshotId: "snapshot_other");
        Blocked(Planner().Build(request with { Instances = [request.Instances[0], other.Instances[0]] }),
            "TPL_INSTANCE_BATCH_LOGICAL_POSITION_CONFLICT");
    }

    [Theory]
    [InlineData("missing_asset", "TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID")]
    [InlineData("wrong_resource", "TPL_INSTANCE_RENDER_TEMPLATE_MISMATCH")]
    [InlineData("invalid_canvas", "TPL_RENDER_PLAN_SCHEMA_REJECTED")]
    public void OneInvalidInstanceDoesNotBorrowInputsOrReturnPartialPlans(string change, string code)
    {
        var request = Fixture(recipes: 1, colors: 2);
        var bad = request.Instances[1];
        var plan = bad.PlanRequest;
        plan = change switch
        {
            "missing_asset" => plan with { Assets = [] },
            "wrong_resource" => plan with { TemplateResource = plan.TemplateResource with { ResourceId = "other_resource" } },
            "invalid_canvas" => plan with { TemplateResource = plan.TemplateResource with
            { Inspection = plan.TemplateResource.Inspection with { Canvas = new(1, 1, 8, "RGB") } } },
            _ => throw new InvalidOperationException()
        };
        var result = Planner().Build(request with { Instances = [request.Instances[0], bad with { PlanRequest = plan }] });
        Blocked(result, code);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(plan.PageInstanceId, diagnostic.PageInstanceId));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(Planner().Build(request with
        { Instances = [bad with { PlanRequest = plan }, request.Instances[0]] })));
    }

    [Fact]
    public void BusinessBlockedFrozenInstancePreservesHealthyPlanAndOnlyItsOwnFrameDiagnostic()
    {
        var request = Fixture(recipes: 1, colors: 2, missingColor: true);
        var blocked = request.Instances.Single(input => input.PlanRequest.Snapshot.Instances.Single(item =>
            item.PageInstanceId == input.PlanRequest.PageInstanceId).Status == TemplateInstancePreflightStatus.BusinessBlocked
            && input.PlanRequest.Snapshot.Instances.Single(item => item.PageInstanceId == input.PlanRequest.PageInstanceId).RecipeId == "recipe_z");
        var result = Planner().Build(request with { Instances = [request.Instances[0], blocked] });
        Assert.Equal(TemplateInstanceRenderBatchStatus.PartiallyReady, result.Status);
        Assert.True(result.CanContinue);
        Assert.True(result.HasCompleteMembership);
        Assert.NotNull(result.BatchPlanDigest);
        Assert.NotNull(result.InputFactsDigest);
        Assert.Equal(2, result.Members.Length);
        var healthy = Assert.Single(result.Plans);
        Assert.Equal(request.Instances[0].PlanRequest.PageInstanceId, healthy.PageInstanceId);
        var unchanged = new TemplateInstanceRenderPlanFactory(Validator()).Build(request.Instances[0].PlanRequest);
        Assert.Equal(unchanged.PlanId, healthy.PlanId);
        Assert.Equal(unchanged.PlanDigest, healthy.PlanDigest);
        Assert.Equal(unchanged.RenderPlanJson, healthy.RenderPlanJson);
        Assert.Equal(TemplateInstanceBatchMemberStatus.BusinessBlocked, result.Members[1].Status);
        Assert.Null(result.Members[1].PlanId);
        Assert.All(result.Diagnostics, item => Assert.Equal(TemplateInstanceBatchDiagnosticKind.BusinessBlock, item.Kind));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(blocked.PlanRequest.PageInstanceId, diagnostic.PageInstanceId));
        Assert.Contains(result.Diagnostics, item => item.FrameId == "frame" && item.VariantId == "color_1");
    }

    [Theory]
    [InlineData("projection", "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID")]
    [InlineData("body", "TPL_INSTANCE_SNAPSHOT_VERSION_INVALID")]
    [InlineData("rule", "TPL_INSTANCE_RULE_DIGEST_MISMATCH")]
    [InlineData("facts", "TPL_INSTANCE_INPUT_FACTS_DIGEST_MISMATCH")]
    [InlineData("variant", "TPL_INSTANCE_ASSET_VARIANT_MISMATCH")]
    [InlineData("frame", "TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID")]
    public void TamperedSnapshotsFactsAndFrameBindingsFailClosed(string change, string code)
    {
        var request = Fixture();
        var input = request.Instances[0];
        var snapshot = input.PlanRequest.Snapshot;
        if (change == "projection") snapshot = snapshot with { ProjectionDigest = "sha256:" + new string('0', 64) };
        if (change == "body") snapshot = snapshot with { Instances = default };
        if (change == "rule") snapshot = TemplateInstanceProjectionSnapshotCodec.WithVersion(snapshot with
        {
            RuleDigest = "sha256:" + new string('e', 64),
            Instances = [.. snapshot.Instances.Select(item => item with { RuleDigest = "sha256:" + new string('e', 64) })]
        }, 1);
        if (change is "variant" or "frame")
        {
            var instance = snapshot.Instances[0];
            instance = change == "variant"
                ? instance with { Frames = [instance.Frames[0] with { VariantId = "other_color" }] }
                : instance with { Frames = [] };
            snapshot = TemplateInstanceProjectionSnapshotCodec.WithVersion(snapshot with
            { Instances = snapshot.Instances.SetItem(0, instance) }, 1);
        }
        if (change == "facts") input = input with { Facts = input.Facts with { Version = 2 } };
        input = input with { PlanRequest = input.PlanRequest with { Snapshot = snapshot } };
        Blocked(Planner().Build(request with { Instances = [input] }), code);
    }

    [Fact]
    public void EmptyRequestsAndEmptyFrozenExpansionsAreNotExecutableSuccesses()
    {
        var request = Fixture();
        var empty = Planner().Build(request with { Instances = [] });
        Assert.Equal(TemplateInstanceRenderBatchStatus.Empty, empty.Status);
        Assert.False(empty.CanContinue);
        Assert.Empty(empty.Plans);
        var frozenEmpty = Planner().Build(Fixture(recipes: 1, colors: 1, emptyExpansion: true));
        Assert.Equal(TemplateInstanceRenderBatchStatus.Empty, frozenEmpty.Status);
        Assert.True(frozenEmpty.HasCompleteMembership);
        Assert.False(frozenEmpty.CanContinue);
        Assert.NotNull(frozenEmpty.BatchPlanDigest);
        Assert.Empty(frozenEmpty.Plans);
        Assert.Empty(frozenEmpty.Members);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("page_instance")]
    [InlineData("recipe")]
    [InlineData("repetition")]
    [InlineData("page")]
    [InlineData("rule")]
    [InlineData("facts")]
    [InlineData("resource")]
    [InlineData("asset_binding")]
    [InlineData("output")]
    public void ChangedInputsCannotReuseBatchDigest(string change)
    {
        var request = Fixture();
        var baseline = Planner().Build(request);
        var changed = change switch
        {
            "set" => Fixture(recipes: 1),
            "page_instance" => Fixture(snapshotId: "other_snapshot"),
            "recipe" => Fixture(recipeId: "other_recipe"),
            "repetition" => Fixture(colors: 2),
            "page" => Fixture(pageId: "other_page"),
            "rule" => Fixture(pageName: "different_rule_name"),
            "facts" => Fixture(factsVersion: 2),
            "resource" => Fixture(resourceId: "other_template"),
            "asset_binding" => Fixture(assetPrefix: "different_asset"),
            "output" => request with { Instances = [.. request.Instances.Select(input => input with
                { PlanRequest = input.PlanRequest with { IncludeEditablePsd = true } })] },
            _ => throw new InvalidOperationException()
        };
        var result = Planner().Build(changed);
        Assert.True(result.CanContinue, JsonSerializer.Serialize(result.Diagnostics));
        Assert.NotEqual(baseline.BatchPlanDigest, result.BatchPlanDigest);
        if (change != "set") Assert.NotEqual(baseline.Plans[0].PlanDigest, result.Plans[0].PlanDigest);
    }

    [Fact]
    public void SharedResourceIdWithDifferentImmutableContentBlocksWholeBatch()
    {
        var request = Fixture();
        var input = request.Instances[1];
        var resource = input.PlanRequest.TemplateResource;
        resource = resource with { Sha256 = new string('f', 64), Inspection = resource.Inspection with { FileSha256 = new string('f', 64) } };
        input = input with { PlanRequest = input.PlanRequest with { TemplateResource = resource } };
        Blocked(Planner().Build(request with { Instances = [request.Instances[0], input] }),
            "TPL_INSTANCE_BATCH_RESOURCE_IDENTITY_COLLISION");
    }

    [Fact]
    public void ReorderedRevisionFrameRowsDoNotChangeTargetOrderOrBatchDigest()
    {
        var request = Fixture(twoFrames: true);
        var baseline = Planner().Build(request);
        Assert.True(baseline.CanContinue);
        var reversed = request with { Instances = [.. request.Instances.Select(input =>
        {
            var revision = input.PlanRequest.Revision;
            var reordered = TemplateRevision.Restore(revision.RevisionId, revision.GroupId, revision.RevisionNo,
                revision.State, revision.ManifestRef, revision.RuleDigest, revision.CreatedAt,
                revision.Pages.Reverse(), revision.FrameBindings.Reverse(), revision.RuleWorkspace);
            return input with { PlanRequest = input.PlanRequest with { Revision = reordered } };
        })] };
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(Planner().Build(reversed)));
    }

    [Fact]
    public void StaleInstanceLocalAssetMetadataCannotOverrideVerifiedFacts()
    {
        var request = Fixture(recipes: 1, colors: 2);
        var input = request.Instances[1];
        var original = Assert.Single(input.PlanRequest.Assets);
        var changed = SourceAssetMetadata.CreateReadable(original.AssetId, original.StyleId,
            original.SourceRelativePath, original.FileName, original.Length, new string('f', 64),
            original.PixelWidth, original.PixelHeight, original.Format, original.MimeType);
        var result = Planner().Build(request with { Instances = [request.Instances[0], input with
        { PlanRequest = input.PlanRequest with { Assets = [changed] } }] });
        Blocked(result, "TPL_INSTANCE_INPUT_FACTS_DIGEST_MISMATCH");
        Assert.All(result.Diagnostics, item => Assert.Equal(input.PlanRequest.PageInstanceId, item.PageInstanceId));
    }

    [Fact]
    public void SameSnapshotIdCannotHideDifferentBodiesOnDisjointMembers()
    {
        var request = Fixture();
        var other = Fixture(factsVersion: 2);
        Blocked(Planner().Build(request with { Instances = [request.Instances[0], other.Instances[1]] }),
            "TPL_INSTANCE_BATCH_SNAPSHOT_CONFLICT");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCannotOmitFrozenMembersToHideBlockedInstancesOrClaimPartialCompletion(bool blocked)
    {
        var request = Fixture(recipes: 1, colors: 2, missingColor: blocked);
        var result = Planner().Build(request with { Instances = [request.Instances[0]] });
        Blocked(result, "TPL_INSTANCE_BATCH_INSTANCE_SET_INCOMPLETE");
        Assert.Equal(request.Instances[1].PlanRequest.PageInstanceId, Assert.Single(result.Diagnostics).PageInstanceId);
    }

    [Fact]
    public void SameTemplateIdentityWithConflictingCanvasIsNotShareable()
    {
        var request = Fixture();
        var input = request.Instances[1];
        var resource = input.PlanRequest.TemplateResource;
        resource = resource with { Inspection = resource.Inspection with { Canvas = new(800, 1300, 8, "RGB") } };
        Blocked(Planner().Build(request with { Instances = [request.Instances[0], input with
        { PlanRequest = input.PlanRequest with { TemplateResource = resource } }] }),
            "TPL_INSTANCE_BATCH_RESOURCE_IDENTITY_COLLISION");
    }

    [Theory]
    [InlineData("page_instance")]
    [InlineData("repetition")]
    public void FrozenIdentityFieldsAreConsumedNotRegenerated(string field)
    {
        var request = Fixture(recipes: 1, colors: 1);
        var baseline = Planner().Build(request);
        var input = request.Instances[0];
        var instance = input.PlanRequest.Snapshot.Instances[0];
        instance = field == "page_instance" ? instance with { PageInstanceId = "instance_" + new string('f', 64) }
            : instance with { RepetitionIndex = 12 };
        // A checksummed frozen-body mutation isolates consumption from the E0 ID algorithm; no parallel identity factory.
        var snapshot = TemplateInstanceProjectionSnapshotCodec.WithVersion(input.PlanRequest.Snapshot with { Instances = [instance] }, 1);
        var result = Planner().Build(request with { Instances = [input with { PlanRequest = input.PlanRequest with
        { Snapshot = snapshot, PageInstanceId = instance.PageInstanceId } }] });
        Assert.True(result.CanContinue);
        Assert.Equal(instance.PageInstanceId, result.Plans[0].PageInstanceId);
        Assert.Equal(instance.RepetitionIndex, result.Plans[0].RepetitionIndex);
        Assert.NotEqual(baseline.Plans[0].PlanId, result.Plans[0].PlanId);
        Assert.NotEqual(baseline.BatchPlanDigest, result.BatchPlanDigest);
    }

    [Theory]
    [InlineData("null_entry")]
    [InlineData("null_id")]
    [InlineData("duplicate_asset")]
    [InlineData("duplicate_slot")]
    [InlineData("default_facts")]
    public void MalformedCollectionsFailClosedInsteadOfThrowingOrReturningCandidates(string mutation)
    {
        var request = Fixture();
        var input = request.Instances[0];
        var plan = input.PlanRequest;
        if (mutation == "duplicate_asset") plan = plan with { Assets = [.. plan.Assets, plan.Assets[0]] };
        if (mutation == "default_facts") input = input with { Facts = input.Facts with { Selections = default } };
        if (mutation is "null_id" or "duplicate_slot")
        {
            var instance = plan.Snapshot.Instances[0];
            instance = mutation == "null_id" ? instance with { PageInstanceId = null! }
                : instance with { Slots = [.. instance.Slots, instance.Slots[0]] };
            plan = plan with { Snapshot = TemplateInstanceProjectionSnapshotCodec.WithVersion(plan.Snapshot with
            { Instances = plan.Snapshot.Instances.SetItem(0, instance) }, 1) };
        }
        var result = Planner().Build(request with { Instances = mutation == "null_entry" ? [null!]
            : [request.Instances[1], input with { PlanRequest = plan }] });
        Assert.Equal(TemplateInstanceRenderBatchStatus.Blocked, result.Status);
        Assert.False(result.CanContinue);
        Assert.Empty(result.Plans);
    }

    private static void Blocked(TemplateInstanceRenderBatchResult result, string code)
    {
        Assert.False(result.CanContinue);
        Assert.Equal(TemplateInstanceRenderBatchStatus.Blocked, result.Status);
        Assert.Empty(result.Plans);
        Assert.Null(result.BatchPlanDigest);
        Assert.Null(result.InputFactsDigest);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
        Assert.Contains(result.Diagnostics, item => item.Kind == TemplateInstanceBatchDiagnosticKind.IntegrityError);
        Assert.False(result.HasCompleteMembership);
        Assert.All(result.Members, member => Assert.Null(member.PlanId));
    }

    [Fact]
    public void MissingSecondaryBackNeverBorrowsAnotherColorOrItsOwnFront()
    {
        var request = Fixture(recipes: 1, colors: 3, twoFrames: true, missingBackOnly: true);
        var result = Planner().Build(request);
        Assert.Equal(TemplateInstanceRenderBatchStatus.PartiallyReady, result.Status);
        Assert.True(result.CanContinue);
        Assert.Equal(3, result.Members.Length);
        Assert.Equal(2, result.Plans.Length);
        var blocked = Assert.Single(result.Members.Where(member => member.Status == TemplateInstanceBatchMemberStatus.BusinessBlocked));
        var reason = Assert.Single(blocked.Diagnostics);
        Assert.Equal("another_frame", reason.FrameId);
        Assert.Equal("color_1", reason.VariantId);
        Assert.Equal("TPL_INSTANCE_SELECTION_MISSING", reason.Code);
        Assert.DoesNotContain(result.Plans, plan => plan.PageInstanceId == blocked.PageInstanceId);
        foreach (var plan in result.Plans)
        {
            using var json = JsonDocument.Parse(plan.RenderPlanJson);
            Assert.Equal(2, json.RootElement.GetProperty("targets").GetArrayLength());
            var input = request.Instances.Single(item => item.PlanRequest.PageInstanceId == plan.PageInstanceId);
            // Canonical frame order is the only normalization; the plan is the existing factory's plan, not a replacement identity.
            var revision = input.PlanRequest.Revision;
            var canonical = TemplateRevision.Restore(revision.RevisionId, revision.GroupId, revision.RevisionNo,
                revision.State, revision.ManifestRef, revision.RuleDigest, revision.CreatedAt, revision.Pages,
                revision.FrameBindings.OrderBy(frame => frame.FrameId, StringComparer.Ordinal), revision.RuleWorkspace);
            Assert.Equal(new TemplateInstanceRenderPlanFactory(Validator()).Build(input.PlanRequest with { Revision = canonical }).RenderPlanJson,
                plan.RenderPlanJson);
            Assert.DoesNotContain("assetcolor_1", plan.RenderPlanJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllBusinessBlockedRequiresUserActionAndIsNotEmptyOrCompleted()
    {
        var result = Planner().Build(Fixture(recipes: 1, colors: 3, missingAll: true));
        Assert.Equal(TemplateInstanceRenderBatchStatus.UserActionRequired, result.Status);
        Assert.False(result.CanContinue);
        Assert.True(result.HasCompleteMembership);
        Assert.Empty(result.Plans);
        Assert.Equal(3, result.Members.Length);
        Assert.All(result.Members, member =>
        {
            Assert.Equal(TemplateInstanceBatchMemberStatus.BusinessBlocked, member.Status);
            Assert.NotEmpty(member.Diagnostics);
            Assert.Null(member.PlanId);
        });
        Assert.NotNull(result.BatchPlanDigest); // A disposition summary, not proof of successful output.
        Assert.NotNull(result.InputFactsDigest);
        var empty = Planner().Build(Fixture(recipes: 1, colors: 1, emptyExpansion: true));
        Assert.NotEqual(result.Status, empty.Status);
        Assert.Empty(empty.Members);
        Assert.False(empty.CanContinue);
    }

    [Theory]
    [InlineData("digest", "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID")]
    [InlineData("duplicate", "TPL_INSTANCE_BATCH_PAGE_INSTANCE_DUPLICATE")]
    [InlineData("wrong_local_variant", "TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID")]
    [InlineData("red_resource", "TPL_INSTANCE_BATCH_RESOURCE_IDENTITY_COLLISION")]
    public void BusinessBlockCannotHideAnIntegrityError(string mutation, string code)
    {
        var request = Fixture(recipes: 1, colors: 2, missingColor: true);
        var healthy = request.Instances[0];
        var red = request.Instances[1];
        if (mutation == "digest") healthy = healthy with { PlanRequest = healthy.PlanRequest with
        { Snapshot = healthy.PlanRequest.Snapshot with { ProjectionDigest = "sha256:" + new string('0', 64) } } };
        if (mutation == "wrong_local_variant") healthy = healthy with { PlanRequest = healthy.PlanRequest with
        { Assets = [healthy.Facts.Assets[1]] } };
        if (mutation == "red_resource")
        {
            var resource = red.PlanRequest.TemplateResource;
            resource = resource with { Sha256 = new string('f', 64), Inspection = resource.Inspection with { FileSha256 = new string('f', 64) } };
            red = red with { PlanRequest = red.PlanRequest with { TemplateResource = resource } };
        }
        var result = Planner().Build(request with { Instances = mutation == "duplicate" ? [healthy, red, healthy] : [healthy, red] });
        Blocked(result, code);
        Assert.Contains(result.Diagnostics, item => item.Kind == TemplateInstanceBatchDiagnosticKind.BusinessBlock);
    }

    [Theory]
    [InlineData("unknown", "TPL_INSTANCE_BATCH_BLOCK_REASON_UNCLASSIFIED")]
    [InlineData("false_missing", "TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID")]
    [InlineData("wrong_reason_frame", "TPL_INSTANCE_BATCH_BLOCK_REASON_MISMATCH")]
    [InlineData("wrong_variant", "TPL_INSTANCE_ASSET_VARIANT_MISMATCH")]
    [InlineData("missing_reason", "TPL_INSTANCE_BATCH_BLOCK_REASON_MISMATCH")]
    public void ChecksummedRedLabelsMustMatchTheFrozenFacts(string mutation, string code)
    {
        var request = Fixture(recipes: 1, colors: 2, missingColor: true);
        var index = mutation == "false_missing" ? 0 : 1;
        request = RewriteBody(request, snapshot =>
        {
            var member = snapshot.Instances[index];
            member = mutation switch
            {
                "unknown" => member with { BlockingReasons = [new("UNKNOWN_OR_SYSTEM_FAILURE", "frame", "color_1")] },
                "false_missing" => member with { Status = TemplateInstancePreflightStatus.BusinessBlocked,
                    Frames = [member.Frames[0] with { AssetId = null }],
                    BlockingReasons = [new("TPL_INSTANCE_SELECTION_MISSING", "frame", "color_0")] },
                "wrong_reason_frame" => member with { BlockingReasons = [member.BlockingReasons[0] with { FrameId = "wrong_frame" }] },
                "wrong_variant" => member with { Frames = [member.Frames[0] with { VariantId = "other_color" }] },
                "missing_reason" => member with { BlockingReasons = [] },
                _ => throw new InvalidOperationException()
            };
            return snapshot with { Instances = snapshot.Instances.SetItem(index, member) };
        });
        Blocked(Planner().Build(request), code);
    }

    [Fact]
    public void FrozenRejectedWrongColorCandidateIsBusinessButIncomingBindingTamperingIsNot()
    {
        var request = Fixture(recipes: 1, colors: 2, wrongFrozenColor: true);
        var result = Planner().Build(request);
        Assert.Equal(TemplateInstanceRenderBatchStatus.PartiallyReady, result.Status);
        Assert.Single(result.Plans);
        var reason = Assert.Single(result.Diagnostics);
        Assert.Equal("TPL_INSTANCE_ASSET_VARIANT_MISMATCH", reason.Code);
        Assert.Equal(TemplateInstanceBatchDiagnosticKind.BusinessBlock, reason.Kind);
        Assert.Equal("assetcolor_0", reason.AssetId); // Rejected candidate is diagnostic evidence, never a rendered fallback.
        Assert.Null(result.Members[1].PlanId);
        request = RewriteBody(request, snapshot => snapshot with { Instances = snapshot.Instances.SetItem(1,
            snapshot.Instances[1] with { Frames = [snapshot.Instances[1].Frames[0] with { AssetId = "assetcolor_0" }] }) });
        Blocked(Planner().Build(request), "TPL_INSTANCE_RENDER_FRAME_BINDING_INVALID");
    }

    [Fact]
    public void MixedMembershipOrderReasonsAndV2DigestAreDeterministicAndCoverBlockedMembers()
    {
        var request = Fixture(recipes: 1, colors: 3, missingColor: true);
        var result = Planner().Build(request);
        var reversed = Planner().Build(request with { Instances = request.Instances.Reverse().ToArray() });
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(reversed));
        Assert.Equal(Enumerable.Range(0, 3), result.Members.Select(member => member.StableOrder));
        var redParameters = request with { Instances = [.. request.Instances.Select(input =>
            input.PlanRequest.PageInstanceId == result.Members[1].PageInstanceId
                ? input with { PlanRequest = input.PlanRequest with { IncludeEditablePsd = true } } : input)] };
        var changed = Planner().Build(redParameters);
        Assert.NotEqual(result.BatchPlanDigest, changed.BatchPlanDigest);
        Assert.Equal(JsonSerializer.Serialize(result.Plans), JsonSerializer.Serialize(changed.Plans));
        var changedReason = Planner().Build(Fixture(recipes: 1, colors: 3, wrongFrozenColor: true));
        Assert.Equal(result.Members.Select(item => item.PageInstanceId), changedReason.Members.Select(item => item.PageInstanceId));
        Assert.NotEqual(result.Members[1].Diagnostics[0].Code, changedReason.Members[1].Diagnostics[0].Code);
        Assert.NotEqual(result.BatchPlanDigest, changedReason.BatchPlanDigest);
        var allRed = Planner().Build(Fixture(recipes: 1, colors: 3, missingAll: true));
        Assert.NotEqual(result.BatchPlanDigest, allRed.BatchPlanDigest);
    }

    [Fact]
    public void DynamicSizeIsProductionValidationGapNotBusinessMissingImage()
    {
        var result = Planner().Build(Fixture(recipes: 1, colors: 1, dynamicSize: true));
        Assert.Equal(TemplateInstanceRenderBatchStatus.ProductionValidationRequired, result.Status);
        Assert.False(result.CanContinue);
        Assert.True(result.HasCompleteMembership);
        Assert.Empty(result.Plans);
        Assert.Null(result.BatchPlanDigest);
        Assert.Equal(TemplateInstanceBatchMemberStatus.RequiresProductionValidation, Assert.Single(result.Members).Status);
        Assert.Equal(TemplateInstanceBatchDiagnosticKind.ProductionValidationRequired, Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void EmptySnapshotChannelCannotHideNonemptyMembersOrDigestCorruption()
    {
        var nonempty = Fixture(recipes: 1, colors: 2);
        var input = nonempty.Instances[0];
        Blocked(Planner().Build(nonempty with { Instances = [], EmptySnapshots =
            [new(input.PlanRequest.Snapshot, input.PlanRequest.Revision, input.Facts)] }), "TPL_INSTANCE_BATCH_EMPTY_SNAPSHOT_INVALID");
        var empty = Fixture(recipes: 1, colors: 1, emptyExpansion: true);
        var context = empty.EmptySnapshots![0];
        Blocked(Planner().Build(empty with { EmptySnapshots = [context with
        { Snapshot = context.Snapshot with { ProjectionDigest = "sha256:" + new string('0', 64) } }] }), "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");
    }

    private static TemplateInstanceRenderBatchRequest RewriteBody(TemplateInstanceRenderBatchRequest request,
        Func<TemplateInstanceProjectionSnapshot, TemplateInstanceProjectionSnapshot> change)
    {
        var body = TemplateInstanceProjectionSnapshotCodec.WithVersion(change(request.Instances[0].PlanRequest.Snapshot), 1);
        TemplateInstanceProjectionSnapshotCodec.Validate(body);
        return request with { Instances = [.. request.Instances.Select(input => input with
        { PlanRequest = input.PlanRequest with { Snapshot = body } })] };
    }

    private static RenderPlanValidator Validator() => new(new JsonSchemaContractValidator());
    private static TemplateInstanceRenderBatchPlanner Planner() => new(Validator());

    private static TemplateInstanceRenderBatchRequest Fixture(int recipes = 2, int colors = 3,
        string batch = "batch", string style = "style", string revisionId = "revision_batch",
        string snapshotId = "snapshot", string pageId = "page_z", string recipeId = "recipe_z",
        string pageName = "page", string resourceId = "template", string assetPrefix = "asset", long factsVersion = 1,
        bool missingColor = false, bool emptyExpansion = false, bool twoFrames = false,
        bool missingAll = false, bool missingBackOnly = false, bool wrongFrozenColor = false, bool dynamicSize = false,
        string? excludedFrame = null)
    {
        var page = TemplatePage.Create(pageId, revisionId, 0, pageName,
            dynamicSize ? TemplatePageContentType.DynamicSizeTable : TemplatePageContentType.NormalComposition,
            TemplateGenerationMode.OncePerProduct, null, true, resourceId);
        var binding = FrameBinding.Create("frame", pageId, LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(257, "registered", true, false, false, false, 255, "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))), [],
            SelectionRequirement.Create("商品图", [SelectionCriterion.Create("reference_only", "true")]),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var recipe = new TemplatePageRecipe(recipeId, pageId, TemplateRuleRepeat.ColorChunks,
            emptyExpansion ? TemplateRuleColorRange.Secondary : TemplateRuleColorRange.All,
            [new("slot", TemplateRuleColorSource.Allocated)], [new("frame", "slot")]);
        var bindings = new[] { binding };
        if (twoFrames)
        {
            var second = FrameBinding.Create("another_frame", pageId, LayerPath.Create([3]), binding.ExpectedLayer,
                [], binding.SelectionRequirement, binding.LayoutIntent);
            bindings = [binding, second];
            recipe = recipe with { FrameBindings = [.. recipe.FrameBindings, new("another_frame", "slot")] };
        }
        var rules = new TemplateRuleSet(1, [new("branch", new(1), recipes == 1 ? [recipe] : [recipe, recipe with { RecipeId = "recipe_a" }])]);
        var workspace = new TemplateRuleWorkspace(1, rules, []);
        if (excludedFrame is not null)
        {
            var excluded = bindings.Single(binding => binding.FrameId == excludedFrame);
            workspace = workspace.SetCandidateExcluded(pageId, new string('c', 64),
                excluded.ExpectedLayer.StructureSignature.Value, excluded.ExpectedLayer.LayerId,
                excluded.LayerPath.Segments.Select(segment => segment - 1).ToArray(), true);
        }
        var draft = TemplateRevision.CreateDraft(revisionId, "group", 1, DateTimeOffset.UnixEpoch,
            [page], bindings, workspace);
        var revision = TemplateRevision.Restore(revisionId, "group", 1, TemplateRevisionState.Committed,
            "templates/revision.json", TemplateRuleCodec.BusinessDigest(draft), DateTimeOffset.UnixEpoch,
            draft.Pages, draft.FrameBindings, draft.RuleWorkspace);
        var variants = Enumerable.Range(0, colors).Select(i => "color_" + i.ToString(CultureInfo.InvariantCulture)).ToImmutableArray();
        var assets = variants.Select(color => SourceAssetMetadata.CreateReadable(assetPrefix + color, style,
            "nonexistent/" + assetPrefix + color + ".jpg", "source.jpg", 100, new string('b', 64),
            1000, 1200, SourceImageFormat.Jpeg, "image/jpeg")).ToImmutableArray();
        var activeBindings = revision.ActiveFrameBindings;
        var facts = new TemplateInstanceInputFacts(factsVersion, new(variants, variants[0], true), assets,
            [.. variants.Select((color, i) => new TemplateInstanceVariantAssets(color, [assets[i].AssetId]))],
            [.. variants.Where(color => !missingAll && (!missingColor || color != "color_1" || missingBackOnly))
                .SelectMany(color => activeBindings.Where(frame => !missingBackOnly || color != "color_1" || frame.FrameId != "another_frame")
                    .Select(frame => new TemplateInstanceFrameSelection(frame.FrameId, color,
                        assetPrefix + (wrongFrozenColor && color == "color_1" ? "color_0" : color))))], []);
        var projectionRequest = new TemplateInstanceProjectionRequest(new(batch, style, snapshotId, revisionId, revision.RuleDigest!), revision, facts);
        var projection = TemplateInstanceProjector.Project(projectionRequest);
        Assert.NotEqual(TemplateInstanceProjectionStatus.Rejected, projection.Status);
        var snapshot = TemplateInstanceProjectionSnapshotCodec.Freeze(projectionRequest, projection);
        var resource = new ImportedTemplateResource(resourceId, "group", revisionId, "nonexistent/template.psd",
            64, new string('c', 64), "sha256:" + new string('d', 64), new PsdInspectionDocument(new string('c', 64), 64,
                new(790, 1252, 8, "RGB"), 1, 1, 0, 0, 0, "sha256:" + new string('d', 64), new(1, 1, 1), 1024, null, []), DateTimeOffset.UnixEpoch);
        if (emptyExpansion)
            return new(batch, style, revisionId, [], [new(snapshot, revision, facts)]);
        return new(batch, style, revisionId, snapshot.Instances.Select(instance => new TemplateInstanceRenderBatchInput(
            new(snapshot, instance.PageInstanceId, revision, resource,
                assets.Where(asset => instance.Frames.Any(frame => frame.AssetId == asset.AssetId)).ToArray()), facts)).ToArray());
    }
}
