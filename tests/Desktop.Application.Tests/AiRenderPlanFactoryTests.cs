using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.Tests;

public sealed class AiRenderPlanFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputNamesUseFrozenPageOrderAndNameWithMatchingJpgPsdBasenames(bool psd)
    {
        var fixture = Fixture(pageName: "G-详情-01", includeEditablePsd: psd);
        var request = new AiRenderPlanPreparationRequest(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", [Selection()])], fixture.Now);
        var frozenFacts = JsonSerializer.Serialize(request);
        var factory = Factory(new TestAvailability(true));
        var first = Assert.Single((await factory.PrepareAsync(request, default)).Pages);
        var second = Assert.Single((await factory.PrepareAsync(request, default)).Pages);

        Assert.Equal("01-G-详情-01.jpg", OutputName(first));
        Assert.Equal(psd ? "01-G-详情-01.psd" : null, OutputName(first, "editable_psd"));
        Assert.Equal(first.RenderPlanJson, second.RenderPlanJson);
        Assert.Equal(frozenFacts, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("正面/背面\\:\"*?<>|\u0001", "01-正面_背面.jpg")]
    [InlineData("..", "01-页面.jpg")]
    [InlineData("CON", "01-CON.jpg")]
    [InlineData("e\u0301", "01-é.jpg")]
    [InlineData("展示\u202e页", "01-展示_页.jpg")]
    public async Task OutputNamesAreSafeWindowsLeavesAndNormalizeUnicode(string pageName, string expected)
    {
        var fixture = Fixture(pageName: pageName);
        var page = Assert.Single((await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch,
            fixture.Revision, [fixture.Resource], [new("style_01", [Selection()])], fixture.Now), default)).Pages);
        Assert.Equal(expected, OutputName(page));
        Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(page.RenderPlanJson!).IsValid);
    }

    [Theory]
    [InlineData("红/色", "红\\色", "红_色")]
    [InlineData("Blue", "blue", "Blue")]
    [InlineData("e\u0301", "é", "é")]
    public async Task OutputNamesDisambiguateAllCollisionsWithoutStealingAnotherVariantsName(
        string firstName, string secondName, string cleanName)
    {
        var fixture = Fixture(generationMode: TemplateGenerationMode.OncePerVariant, includeEditablePsd: true);
        AiOutputVariantPlan[] variants = [NamedVariant("color_a", firstName), NamedVariant("color_b", secondName),
            NamedVariant("color_c", cleanName + "-01")];
        var factory = Factory(new TestAvailability(true));
        async Task<AiRenderPlanPreparation> Prepare(IEnumerable<AiOutputVariantPlan> values) => await factory.PrepareAsync(
            new(fixture.Batch, fixture.Revision, [fixture.Resource],
                [new("style_01", [Selection()], VariantPlans: values.ToArray())], fixture.Now), default);
        var forward = await Prepare(variants);
        var reverse = await Prepare(variants.Reverse());

        Assert.Equal(3, forward.Pages.Select(OutputName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal($"01-详情页-{cleanName}-02.jpg", OutputName(forward.Pages.Single(p => p.OutputVariantId == "color_a")));
        Assert.Equal($"01-详情页-{cleanName}-01.jpg", OutputName(forward.Pages.Single(p => p.OutputVariantId == "color_c")));
        foreach (var page in forward.Pages)
        {
            Assert.DoesNotContain("color_", OutputName(page)!);
            Assert.Equal(Path.ChangeExtension(OutputName(page), ".psd"), OutputName(page, "editable_psd"));
            Assert.Equal(page.RenderPlanJson, reverse.Pages.Single(p => p.OutputVariantId == page.OutputVariantId).RenderPlanJson);
        }

        // A temporarily blocked sibling still reserves its name; becoming renderable cannot rename others.
        var blocked = await Prepare([variants[0] with { ConflictCode = "AI_VARIANT_INCOMPLETE" }, variants[1], variants[2]]);
        Assert.False(blocked.Pages.Single(p => p.OutputVariantId == "color_a").HasPlan);
        Assert.Equal(OutputName(forward.Pages.Single(p => p.OutputVariantId == "color_b")),
            OutputName(blocked.Pages.Single(p => p.OutputVariantId == "color_b")));
    }

    [Fact]
    public async Task OutputNamesBoundLongUnicodeTextWithoutSplittingTextElementsOrLosingVariantLabels()
    {
        var fixture = Fixture(pageName: string.Concat(Enumerable.Repeat("夏👚", 65)),
            generationMode: TemplateGenerationMode.OncePerVariant);
        var longLabel = "红色" + string.Concat(Enumerable.Repeat("é👚", 70));
        AiOutputVariantPlan[] variants = [NamedVariant("color_a", longLabel + "一"), NamedVariant("color_b", longLabel + "二")];
        var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", [Selection()], VariantPlans: variants)], fixture.Now), default);
        Assert.Equal(2, result.Pages.Select(OutputName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var page in result.Pages)
        {
            var name = OutputName(page)!;
            Assert.InRange(name.Length, 10, 159);
            Assert.Contains("-红色", name);
            Assert.DoesNotContain('�', name);
            Assert.Equal(name, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(name)));
            Assert.True(name.IsNormalized(NormalizationForm.FormC));
            Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(page.RenderPlanJson!).IsValid);
        }
    }

    [Fact]
    public async Task OutputNamesKeepProductPagesColorlessAndSameNamePagesInFrozenOrder()
    {
        var fixture = Fixture(secondPage: true, secondPageName: "详情页");
        var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", [Selection(), Selection() with { FrameId = "frame_02" }],
                VariantPlans: [NamedVariant("color_a", "红色"), NamedVariant("color_b", "蓝色")])], fixture.Now), default);
        Assert.Equal<string>(["01-详情页.jpg", "02-详情页.jpg"], result.Pages.Select(p => OutputName(p)!));
        Assert.All(result.Pages, p => Assert.Null(p.OutputVariantId));
    }

    [Fact]
    public async Task OutputNamesAreIndependentOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var fixture = Fixture();
            var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
                [fixture.Resource], [new("style_01", [Selection()])], fixture.Now), default);
            Assert.Equal("01-详情页.jpg", OutputName(Assert.Single(result.Pages)));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task OutputNamesParticipateInNewPlanIdentityWhenVariantLabelChanges()
    {
        var fixture = Fixture(generationMode: TemplateGenerationMode.OncePerVariant);
        var factory = Factory(new TestAvailability(true));
        async Task<AiRenderPagePreparation> Prepare(string label) => Assert.Single((await factory.PrepareAsync(
            new(fixture.Batch, fixture.Revision, [fixture.Resource], [new("style_01", [Selection()],
                VariantPlans: [NamedVariant("color_a", label)])], fixture.Now), default)).Pages);
        var before = await Prepare("红色");
        var after = await Prepare("蓝色");
        using var oldPlan = JsonDocument.Parse(before.RenderPlanJson!);
        using var newPlan = JsonDocument.Parse(after.RenderPlanJson!);
        foreach (var property in new[] { "plan_id", "idempotency_key", "plan_digest" })
            Assert.NotEqual(oldPlan.RootElement.GetProperty(property).GetString(), newPlan.RootElement.GetProperty(property).GetString());
        Assert.NotEqual(oldPlan.RootElement.GetProperty("output").GetProperty("artifact_set_id").GetString(),
            newPlan.RootElement.GetProperty("output").GetProperty("artifact_set_id").GetString());
        Assert.Equal("01-详情页-红色.jpg", OutputName(before));
        Assert.Equal("01-详情页-蓝色.jpg", OutputName(after));
    }

    private static string? OutputName(AiRenderPagePreparation page) => OutputName(page, "jpg");

    private static string? OutputName(AiRenderPagePreparation page, string kind)
    {
        using var plan = JsonDocument.Parse(page.RenderPlanJson!);
        return plan.RootElement.GetProperty("output").GetProperty(kind).GetProperty("file_name").GetString();
    }

    private static AiOutputVariantPlan NamedVariant(string id, string name) => new(id, name, id == "color_a", true,
        new Dictionary<string, string> { ["frame_01"] = "asset_front" }, [], []);

    [Fact]
    public async Task FrozenSnapshotSelectionAndGeometryBuildPlanButUnavailablePhotoshopKeepsWaiting()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(false);
        var factory = Factory(availability);

        var result = await factory.PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [Selection()])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.WaitingForPhotoshop, result.Status);
        Assert.Equal("PHOTOSHOP_TEST_UNAVAILABLE", result.ReasonCode);
        Assert.False(result.HasFormalOutput);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.True(page.HasPlan);
        Assert.Equal(1, availability.Calls);

        using var document = JsonDocument.Parse(page.RenderPlanJson!);
        var root = document.RootElement;
        Assert.Equal(page.PlanDigest, root.GetProperty("plan_digest").GetString());
        Assert.Equal("asset_front", root.GetProperty("targets")[0]
            .GetProperty("asset_resource_id").GetString());
        var source = root.GetProperty("resources").EnumerateArray().Single(resource =>
            string.Equals(
                resource.GetProperty("resource_id").GetString(),
                "asset_front",
                StringComparison.Ordinal));
        Assert.Equal("high_resolution_render_source", source.GetProperty("role").GetString());
        Assert.Equal(new string('b', 64), source.GetProperty("sha256").GetString());
        Assert.Equal(128, source.GetProperty("byte_length").GetInt64());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Contains(root.GetProperty("required_capabilities").EnumerateArray(),
            value => value.GetString() == RenderPlanPlacementMigration.Capability);
        var placement = root.GetProperty("targets")[0].GetProperty("layout").GetProperty("placement");
        Assert.Equal(1000, placement.GetProperty("source_width").GetInt32());
        Assert.Equal(1200, placement.GetProperty("source_height").GetInt32());
        Assert.True(placement.GetProperty("left").GetDecimal() <= 0);
        Assert.True(placement.GetProperty("left").GetDecimal() + placement.GetProperty("width").GetDecimal() >= 790);
        Assert.DoesNotContain(fixture.Batch.InputRoot, page.RenderPlanJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("analysis-copy", page.RenderPlanJson!, StringComparison.OrdinalIgnoreCase);
        Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator())
            .Validate(page.RenderPlanJson!).IsValid);
    }

    [Fact]
    public async Task InfeasibleCompleteSubjectIsReviewRequiredWithoutPlanOrPhotoshopSubmission()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var selection = Selection() with { SubjectGeometry = new("asset_front", .1m, .1m, .9m, .9m, .95m) };
        var result = await Factory(availability).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", [selection])], fixture.Now), default);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, page.Status);
        Assert.Equal("TPL_RENDER_LAYOUT_REVIEW_REQUIRED", page.ReasonCode);
        Assert.False(page.HasPlan);
        Assert.Equal(0, availability.Calls);
    }

    [Fact]
    public async Task RealWorkerBareStructureDigestClosesWithoutRewritingStoredInspection()
    {
        var fixture = Fixture();
        var resource = fixture.Resource with { StructureSignature = new string('a', 64),
            Inspection = fixture.Resource.Inspection with { StructureSignature = new string('a', 64) } };
        var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [resource], [new("style_01", [Selection()])], fixture.Now), default);
        Assert.True(Assert.Single(result.Pages).HasPlan);
        Assert.Equal(new string('a', 64), resource.Inspection.StructureSignature);
        var malformed = resource with { StructureSignature = "not-a-digest" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true)).PrepareAsync(new(
            fixture.Batch, fixture.Revision, [malformed], [new("style_01", [Selection()])], fixture.Now), default));
    }

    [Fact]
    public async Task VariantPagesHaveExplicitIndependentIdentityAndProductPagesAreNotDuplicated()
    {
        foreach (var mode in new[] { TemplateGenerationMode.OncePerProduct, TemplateGenerationMode.OncePerVariant })
        {
            var fixture = Fixture(generationMode: mode);
            AiOutputVariantPlan[] variants = [
                new("color_a", "A", true, true, new Dictionary<string,string> { ["frame_01"] = "asset_front" }, [], []),
                new("color_b", "B", false, true, new Dictionary<string,string> { ["frame_01"] = "asset_front" }, [], [])];
            var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
                [fixture.Resource], [new("style_01", [Selection()], VariantPlans: variants)], fixture.Now), default);
            Assert.Equal(mode == TemplateGenerationMode.OncePerProduct ? 1 : 2, result.Pages.Count);
            Assert.Equal(result.Pages.Count, result.Pages.Select(page => page.PlanDigest).Distinct().Count());
            foreach (var page in result.Pages)
            {
                using var plan = JsonDocument.Parse(page.RenderPlanJson!);
                Assert.Equal(page.OutputVariantId, RenderPlanVariantIdentity.Read(plan.RootElement));
                Assert.Equal(mode == TemplateGenerationMode.OncePerVariant, plan.RootElement.GetProperty("required_capabilities")
                    .EnumerateArray().Any(value => value.GetString() == RenderPlanVariantIdentity.Capability));
                if (mode == TemplateGenerationMode.OncePerVariant)
                {
                    var changed = JsonNode.Parse(page.RenderPlanJson!)!;
                    changed["document"]!.AsObject().Remove("output_variant_id");
                    changed["plan_digest"] = RenderPlanDigest.Compute(changed.ToJsonString());
                    Assert.False(new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(changed.ToJsonString()).IsValid);
                }
            }
        }
    }

    [Fact]
    public async Task MissingSelectionIsBusinessIsolatedAndDoesNotProbePhotoshop()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        Assert.Equal("AI_RENDER_NO_EXECUTABLE_PAGE", result.ReasonCode);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, page.Status);
        Assert.Equal("AI_RENDER_REQUIRED_SELECTION_MISSING", page.ReasonCode);
        Assert.False(page.HasPlan);
        Assert.Equal(0, availability.Calls);
    }

    [Theory]
    [InlineData("available")]
    [InlineData("missing")]
    [InlineData("infeasible")]
    public async Task SecondaryColorUsesOnlyItsOwnGeometryAndKeepsReviewBoundary(string geometryCase)
    {
        var fixture = Fixture(generationMode: TemplateGenerationMode.OncePerVariant, secondaryAsset: true);
        AiOutputVariantPlan[] variants = [
            new("color_a", "A", true, true, new Dictionary<string, string> { ["frame_01"] = "asset_front" }, [], []),
            new("color_b", "B", false, true, new Dictionary<string, string> { ["frame_01"] = "asset_second" }, [], [])];
        var geometry = new Dictionary<string, AiSubjectGeometryV1>();
        if (geometryCase != "missing") geometry["asset_second"] = geometryCase == "infeasible"
            ? new("asset_second", .1m, .1m, .9m, .9m, .95m)
            : new("asset_second", .05m, .1m, .55m, .9m, .95m);
        var result = await Factory(new TestAvailability(true)).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", [Selection()], VariantPlans: variants, SubjectGeometryByAsset: geometry)], fixture.Now), default);
        var primary = Assert.Single(result.Pages, p => p.OutputVariantId == "color_a");
        var secondary = Assert.Single(result.Pages, p => p.OutputVariantId == "color_b");
        Assert.True(primary.HasPlan);
        if (geometryCase == "infeasible")
        {
            Assert.False(secondary.HasPlan);
            Assert.Equal("TPL_RENDER_LAYOUT_REVIEW_REQUIRED", secondary.ReasonCode);
            return;
        }
        Assert.True(secondary.HasPlan);
        Assert.Equal(geometryCase == "missing" ? "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK" : "AI_RENDER_PLAN_VALID", secondary.ReasonCode);
        using var plan = JsonDocument.Parse(secondary.RenderPlanJson!);
        var target = plan.RootElement.GetProperty("targets")[0];
        Assert.Equal("asset_second", target.GetProperty("asset_resource_id").GetString());
        if (geometryCase == "available") Assert.Equal(0m, target.GetProperty("layout").GetProperty("placement").GetProperty("left").GetDecimal());
    }

    [Theory]
    [InlineData("outside_style", "outside_style")]
    [InlineData("asset_front", "different_asset")]
    public async Task GeometryMapCannotCrossAssetOrStyleBoundary(string key, string assetId)
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(availability).PrepareAsync(new(fixture.Batch,
            fixture.Revision, [fixture.Resource], [new("style_01", [Selection()], SubjectGeometryByAsset:
                new Dictionary<string, AiSubjectGeometryV1> { [key] = new(assetId, .2m, .1m, .8m, .9m, .95m) })], fixture.Now), default));
        Assert.Equal("AI_RENDER_GEOMETRY_ASSET_MISMATCH", error.Message);
        Assert.Equal(0, availability.Calls);
    }

    [Theory]
    [InlineData("frame_01", "page_01", "page_02")]
    [InlineData("frame_02", "page_02", "page_01")]
    public async Task SelectionConflictOnlyBlocksThePageThatUsesThatFrame(
        string conflictFrame, string blockedPage, string readyPage)
    {
        var fixture = Fixture(secondPage: true);
        var availability = new TestAvailability(true);
        var selections = new[] { Selection(), Selection() with { FrameId = "frame_02" } }
            .Select(selection => selection.FrameId == conflictFrame
                ? selection with { SelectionConflictCode = "AI_VARIANT_ROLE_SELECTION_MISSING" }
                : selection).ToArray();

        var result = await Factory(availability).PrepareAsync(new(fixture.Batch, fixture.Revision,
            [fixture.Resource], [new("style_01", selections)], fixture.Now), default);

        var blocked = Assert.Single(result.Pages, page => page.PageId == blockedPage);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, blocked.Status);
        Assert.Equal("AI_VARIANT_ROLE_SELECTION_MISSING", blocked.ReasonCode);
        Assert.False(blocked.HasPlan);
        var ready = Assert.Single(result.Pages, page => page.PageId == readyPage);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, ready.Status);
        Assert.True(ready.HasPlan);
        Assert.Equal(1, availability.Calls);
        Assert.Equal(1, result.ReadyPageCount);
    }

    [Fact]
    public async Task SelectedAssetWithoutGeometryUsesCalibratedLayoutFallback()
    {
        var fixture = Fixture();
        var availability = new TestAvailability(true);
        var selection = Selection() with { SubjectGeometry = null };
        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [selection])],
            fixture.Now), CancellationToken.None);

        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.ReadyToRender, page.Status);
        Assert.Equal("AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", page.ReasonCode);
        Assert.True(page.HasPlan);
        Assert.Equal(1, availability.Calls);
    }

    [Fact]
    public async Task ChangedRevisionCannotReplaceOldBatchSnapshot()
    {
        var fixture = Fixture();
        var changed = TemplateRevision.Restore(
            fixture.Revision.RevisionId,
            fixture.Revision.GroupId,
            fixture.Revision.RevisionNo,
            fixture.Revision.State,
            fixture.Revision.ManifestRef,
            "sha256:" + new string('f', 64),
            fixture.Revision.CreatedAt,
            fixture.Revision.Pages,
            fixture.Revision.FrameBindings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                changed,
                [fixture.Resource],
                [new AiRenderStyleInput("style_01", [Selection()])],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_SNAPSHOT_MISMATCH", error.Message);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("revision")]
    [InlineData("length")]
    [InlineData("file-digest")]
    [InlineData("structure")]
    public async Task TemplateResourceMustCloseOverFrozenPageAndInspection(string mutation)
    {
        var fixture = Fixture();
        var resource = mutation switch
        {
            "group" => fixture.Resource with { GroupId = "group_other" },
            "revision" => fixture.Resource with { DraftId = "revision_other" },
            "length" => fixture.Resource with
            {
                Inspection = fixture.Resource.Inspection with { SizeBytes = fixture.Resource.Length + 1 }
            },
            "file-digest" => fixture.Resource with
            {
                Inspection = fixture.Resource.Inspection with { FileSha256 = new string('f', 64) }
            },
            "structure" => fixture.Resource with
            {
                StructureSignature = "sha256:" + new string('f', 64)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                fixture.Revision,
                [resource],
                [new AiRenderStyleInput("style_01", [Selection()])],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_TEMPLATE_RESOURCE_OWNERSHIP_MISMATCH", error.Message);
    }

    [Fact]
    public async Task DynamicSizeCandidateMustReferenceAnAssetOwnedByTheStyle()
    {
        var fixture = DynamicSizeFixture();
        var foreignCandidate = new AiSizeTableCandidateV1(
            "asset_foreign",
            0.99m,
            ["尺码", "胸围"],
            [["M", "96"]]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(new TestAvailability(true))
            .PrepareAsync(new AiRenderPlanPreparationRequest(
                fixture.Batch,
                fixture.Revision,
                [fixture.Resource],
                [new AiRenderStyleInput("style_01", [], foreignCandidate)],
                fixture.Now), CancellationToken.None));

        Assert.Equal("AI_RENDER_SIZE_TABLE_SOURCE_MISMATCH", error.Message);
    }

    [Fact]
    public async Task ValidDynamicSizeCandidateStopsBeforeSizeRasterizerAndPhotoshop()
    {
        var fixture = DynamicSizeFixture();
        var availability = new TestAvailability(true);
        var candidate = new AiSizeTableCandidateV1(
            "asset_front",
            0.99m,
            ["尺码", "胸围"],
            [["M", "96"]]);

        var result = await Factory(availability).PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput("style_01", [], candidate)],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        Assert.Equal("AI_RENDER_NO_EXECUTABLE_PAGE", result.ReasonCode);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.AwaitingSizeTableRasterization, page.Status);
        Assert.Equal("AI_SIZE_TABLE_RASTERIZER_PENDING", page.ReasonCode);
        Assert.False(page.HasPlan);
        Assert.Equal(0, availability.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedDynamicSizeResourceRetainsLegacyPlanButDoesNotClaimExecutableWithoutCalibration(bool psd)
    {
        var fixture = Fixture(dynamicSize: true, pageName: "F-尺码表", includeEditablePsd: psd);
        var availability = new TestAvailability(true);
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var table = ReadySizeTable(fixture.Now);
        var resource = new SizeTableRasterizedResource(
            "size_raster_0123456789abcdef01234567",
            "deterministic_size_table",
            "image/png",
            new string('c', 64),
            4096,
            1000,
            420,
            table.DataDigest,
            "gold_v1",
            "dynamic_grid_v1",
            "batches/batch_01/size-tables/style_01/page_01/resources/private.png");
        var factory = new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(validator),
            availability,
            new SizeTableRenderPlanBuilder(validator));

        var result = await factory.PrepareAsync(new AiRenderPlanPreparationRequest(
            fixture.Batch,
            fixture.Revision,
            [fixture.Resource],
            [new AiRenderStyleInput(
                "style_01",
                [],
                null,
                [new AiPreparedSizeTablePage("page_01", table, resource)])],
            fixture.Now), CancellationToken.None);

        Assert.Equal(AiRenderPreparationStatus.UserActionRequired, result.Status);
        var page = Assert.Single(result.Pages);
        Assert.Equal(AiRenderPagePreparationStatus.BusinessBlocked, page.Status);
        Assert.Equal("SIZE_TABLE_RENDER_TARGET_REQUIRED", page.ReasonCode);
        Assert.True(page.HasPlan);
        Assert.Equal("01-F-尺码表.jpg", OutputName(page));
        Assert.Equal(psd ? "01-F-尺码表.psd" : null, OutputName(page, "editable_psd"));
        Assert.Equal(0, availability.Calls);
        using var document = JsonDocument.Parse(page.RenderPlanJson!);
        var root = document.RootElement;
        Assert.Equal("dynamic_size_table", root.GetProperty("document").GetProperty("content_type").GetString());
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.Equal("/", root.GetProperty("size_table").GetProperty("rows")[0]
            .GetProperty("cells").GetProperty("col_002").GetString());
        Assert.Equal("deterministic_size_table", root.GetProperty("resources")[1]
            .GetProperty("role").GetString());
        Assert.DoesNotContain(resource.OwnedRelativePath, page.RenderPlanJson!, StringComparison.Ordinal);
        Assert.True(validator.Validate(page.RenderPlanJson!).IsValid);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("orphan-resource")]
    [InlineData("wrong-role")]
    [InlineData("safe-bounds")]
    public void SemanticValidatorRejectsTamperedDigestOrResourceClosure(string mutation)
    {
        var fixture = Fixture();
        var builder = new TemplateRenderPlanBuilder(
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var json = builder.Build(
            fixture.Revision,
            fixture.Revision.Pages[0],
            fixture.Resource,
            new TemplateRenderPlanRequest(
                "plan_valid",
                "render/batch_01/style_01/page_01/revision_01",
                "corr_valid",
                "batch_01",
                "style_01",
                "artifact_valid",
                "001-01.jpg",
                false,
                null,
                fixture.Now,
                [new TemplateRenderSource("asset_front", new string('b', 64), 128)],
                [new TemplateRenderSelection(
                    "frame_01",
                    "asset_front",
                    new TemplateRenderSubjectGeometry("asset_front", 0.1m, 0.1m, 0.9m, 0.9m, 0.95m))]));
        var root = JsonNode.Parse(json)!.AsObject();
        switch (mutation)
        {
            case "digest":
                root["plan_digest"] = "sha256:" + new string('f', 64);
                break;
            case "orphan-resource":
                root["resources"]!.AsArray().Add(JsonNode.Parse("""
                    {"resource_id":"asset_orphan","role":"high_resolution_render_source","media_type":"image/jpeg","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","byte_length":64}
                    """));
                break;
            case "wrong-role":
                root["resources"]![1]!["role"] = "template_copy";
                break;
            case "safe-bounds":
                root["targets"]![0]!["layout"]!["safe_bounds"]!["left"] = 700;
                root["targets"]![0]!["layout"]!["safe_bounds"]!["right"] = 600;
                break;
        }

        var validation = new RenderPlanValidator(new JsonSchemaContractValidator())
            .Validate(root.ToJsonString());

        Assert.False(validation.IsValid);
        Assert.StartsWith("RENDER_PLAN_", Assert.Single(validation.Errors).Code, StringComparison.Ordinal);
    }

    private static AiRenderPlanFactory Factory(TestAvailability availability) =>
        new AiRenderPlanFactory(
            new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            availability);

    private static AiConfirmedRenderSelection Selection() => new(
        "frame_01",
        "asset_front",
        new AiSubjectGeometryV1("asset_front", 0.2m, 0.1m, 0.8m, 0.9m, 0.95m),
        1);

    private static RenderFixture DynamicSizeFixture() => Fixture(dynamicSize: true);

    private static SizeTable ReadySizeTable(DateTimeOffset now)
    {
        var candidate = SizeTableCandidate.Create(
            "candidate_01",
            SizeTableSource.Create("asset_front", new string('b', 64)),
            0.99m,
            ["尺码", "袖长"],
            [["L", "/"], ["XL", "/"]]);
        return SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, now);
    }

    private static RenderFixture Fixture(bool dynamicSize = false, TemplateGenerationMode generationMode = TemplateGenerationMode.OncePerProduct,
        bool secondaryAsset = false, bool secondPage = false, string pageName = "详情页", string secondPageName = "第二页",
        bool includeEditablePsd = false)
    {
        var now = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var page = TemplatePage.Create(
            "page_01",
            "revision_01",
            0,
            pageName,
            dynamicSize
                ? TemplatePageContentType.DynamicSizeTable
                : TemplatePageContentType.NormalComposition,
            generationMode,
            null,
            true,
            "resource_template");
        var binding = FrameBinding.Create(
            "frame_01",
            "page_01",
            LayerPath.Create([2]),
            ExpectedLayerEvidence.Create(
                257,
                "registered_frame",
                true,
                false,
                false,
                false,
                byte.MaxValue,
                "normal",
                LayerStructureSignature.Create("sha256:" + new string('a', 64))),
            [],
            SelectionRequirement.Create("商品主体清晰完整，构图适合当前图片框。"),
            LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252)));
        var pages = new List<TemplatePage> { page };
        var bindings = new List<FrameBinding>(dynamicSize ? [] : [binding]);
        if (secondPage)
        {
            pages.Add(TemplatePage.Create("page_02", "revision_01", 1, secondPageName,
                TemplatePageContentType.NormalComposition, generationMode, null, true, "resource_template"));
            bindings.Add(FrameBinding.Create("frame_02", "page_02", LayerPath.Create([2]),
                ExpectedLayerEvidence.Create(257, "registered_frame", true, false, false, false,
                    byte.MaxValue, "normal", LayerStructureSignature.Create("sha256:" + new string('a', 64))),
                [], SelectionRequirement.Create("商品主体清晰完整，构图适合当前图片框。"),
                LayoutIntent.Create("subject_complete", 1, 0, 0, TemplateRect.Create(0, 0, 790, 1252))));
        }
        var revision = Commit(TemplateRevision.CreateDraft(
            "revision_01",
            "group_01",
            1,
            now,
            pages,
            bindings));
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_01",
            "batch_01",
            revision,
            pages.Select(item => item.PageId),
            now);
        var sourceAssets = new List<SourceAssetMetadata> { SourceAssetMetadata.CreateReadable("asset_front", "style_01", "front.jpg",
            "front.jpg", 128, new string('b', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg") };
        if (secondaryAsset) sourceAssets.Add(SourceAssetMetadata.CreateReadable("asset_second", "style_01", "second.jpg",
            "second.jpg", 128, new string('f', 64), 1000, 1200, SourceImageFormat.Jpeg, "image/jpeg"));
        var batch = BatchDraft.Create(
                "batch_01",
                @"C:\readonly\source",
                @"D:\formal-output",
                snapshot,
                BatchSelectionMode.Automatic,
                includeEditablePsd: includeEditablePsd,
                now)
            .BeginScanning(now.AddTicks(1))
            .MarkScanReady(
                BatchInputType.SingleStyle,
                [StyleDraft.Create("style_01", "款式 1", "", 0, sourceAssets.Select(asset => asset.AssetId))],
                sourceAssets,
                "batches/batch_01/asset-manifest/manifest.json",
                "sha256:" + new string('c', 64),
                now.AddTicks(2));
        var resource = new ImportedTemplateResource(
            "resource_template",
            "group_01",
            "revision_01",
            "templates/groups/group_01/revisions/revision_01/pages/page_01/template.psd",
            64,
            new string('d', 64),
            "sha256:" + new string('a', 64),
            new PsdInspectionDocument(
                new string('d', 64),
                64,
                new PsdCanvasInspection(790, 1252, 8, "RGB"),
                1,
                1,
                0,
                0,
                0,
                "sha256:" + new string('a', 64),
                new PsdInspectionTiming(1, 1, 2),
                1024,
                null,
                []),
            now);
        return new RenderFixture(now, revision, batch, resource);
    }

    private static TemplateRevision Commit(TemplateRevision revision)
    {
        var importing = revision.AdvanceTo(
            TemplateRevisionState.ImportingCopy,
            new RevisionTransitionEvidence(SourceReadable: true));
        var inspecting = importing.AdvanceTo(
            TemplateRevisionState.StructureInspecting,
            new RevisionTransitionEvidence(CopyMatchesSource: true));
        var binding = inspecting.AdvanceTo(
            TemplateRevisionState.FrameBinding,
            new RevisionTransitionEvidence(StructureReadable: true));
        var calibrating = binding.AdvanceTo(
            TemplateRevisionState.Calibrating,
            new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true));
        var review = calibrating.AdvanceTo(
            TemplateRevisionState.AwaitingReview,
            new RevisionTransitionEvidence(CalibrationComplete: true));
        var committing = review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
        return committing.Commit(
            "templates/groups/group_01/revisions/revision_01/revision-manifest.json",
            "sha256:" + new string('e', 64),
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private sealed record RenderFixture(
        DateTimeOffset Now,
        TemplateRevision Revision,
        BatchDraft Batch,
        ImportedTemplateResource Resource);

    private sealed class TestAvailability(bool available) : IPhotoshopAvailabilityPort
    {
        public int Calls { get; private set; }

        public Task<PhotoshopAvailability> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new PhotoshopAvailability(
                available,
                available ? null : "PHOTOSHOP_TEST_UNAVAILABLE"));
        }
    }
}
