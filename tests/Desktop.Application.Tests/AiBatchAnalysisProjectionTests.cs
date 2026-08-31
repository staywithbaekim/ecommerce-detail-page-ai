using Desktop.Application.Ai;
using Desktop.Contracts.Ai;

namespace Desktop.Application.Tests;

public sealed class AiBatchAnalysisProjectionTests
{
    [Fact]
    public void PageBusinessErrorDoesNotBlockReadySiblingPage()
    {
        var ready = Page("page_ready", AiPageUiStatus.Ready);
        var blocked = Page("page_blocked", AiPageUiStatus.BusinessError);
        var style = new AiStyleAnalysisProjection(
            "batch_01",
            "style_01",
            "SKU-01",
            AiStyleUiStatus.ReviewRequired,
            "待人工复核",
            [ready, blocked],
            3);
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.ReviewRequired,
            "部分页面需要人工复核。",
            "继续处理",
            1,
            1,
            1,
            2,
            [style],
            AiAnalysisSchedulingControlState.Running,
            null);

        Assert.Equal(1, projection.ReadyPageCount);
        Assert.True(style.HasReadyPage);
        Assert.True(style.HasBusinessError);
        Assert.True(ready.CanGenerate);
        Assert.False(blocked.CanGenerate);
        Assert.True(projection.CanEnterGeneration);
    }

    [Theory]
    [InlineData(AiBatchUiStatus.NotGeneratable)]
    [InlineData(AiBatchUiStatus.Cancelled)]
    [InlineData(AiBatchUiStatus.RecoveryRequired)]
    [InlineData(AiBatchUiStatus.Failed)]
    public void ZeroOutputAndRecoveryStatesNeverEnableGeneration(AiBatchUiStatus status)
    {
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            status,
            "不能进入生成。",
            "继续处理",
            1,
            1,
            0,
            1,
            [],
            status == AiBatchUiStatus.Cancelled
                ? AiAnalysisSchedulingControlState.Cancelled
                : AiAnalysisSchedulingControlState.Running,
            null);

        Assert.True(projection.IsTerminal);
        Assert.False(projection.CanEnterGeneration);
    }

    [Fact]
    public void ManualConfigurationIsOnlyRequiredWhileASelectionStillNeedsLocking()
    {
        var awaitingLock = Page("page_manual", AiPageUiStatus.ReviewRequired) with
        {
            RequiresManualConfiguration = true
        };
        var lockedYellow = Page("page_review", AiPageUiStatus.ReviewRequired);

        Assert.True(awaitingLock.RequiresManualConfiguration);
        Assert.False(lockedYellow.RequiresManualConfiguration);

        var settled = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.ReviewRequired,
            "分析完成，可进入生成后检查。",
            "进入生成",
            1,
            1,
            1,
            1,
            [new AiStyleAnalysisProjection(
                "batch_01",
                "style_01",
                "SKU-01",
                AiStyleUiStatus.ReviewRequired,
                "待检查",
                [lockedYellow],
                2)],
            AiAnalysisSchedulingControlState.Running,
            null);

        Assert.True(settled.CanEnterGeneration);
    }

    [Fact]
    public void UnconfirmedReviewPageDoesNotEnterGeneration()
    {
        var awaitingConfirmation = Page("page_review", AiPageUiStatus.ReviewRequired) with
        {
            RequiresManualConfiguration = true
        };
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.ReviewRequired,
            "分析完成，部分页面需要人工复核后才能生成。",
            "处理此页",
            1,
            1,
            0,
            1,
            [new AiStyleAnalysisProjection(
                "batch_01",
                "style_01",
                "SKU-01",
                AiStyleUiStatus.ReviewRequired,
                "待人工复核",
                [awaitingConfirmation],
                1)],
            AiAnalysisSchedulingControlState.Running,
            null);

        Assert.Equal(0, projection.ExecutablePageCount);
        Assert.False(projection.CanEnterGeneration);
    }

    [Fact]
    public void UnknownResultUsesActionableTextWithoutSensitiveDetails()
    {
        var page = new AiPageAnalysisProjection(
            "batch_01",
            "style_01",
            "page_01",
            "主图页",
            AiPageUiStatus.SystemError,
            "结果未知，需要恢复",
            "查询状态",
            [],
            ["供应商处理结果未知，禁止自动重发。"]);

        var text = string.Join(' ', page.StatusText, page.ActionText, string.Join(' ', page.WarningTexts));
        Assert.Contains("禁止自动重发", text, StringComparison.Ordinal);
        Assert.DoesNotContain("doubao", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateProjectsTopNLockAndEvidenceWithoutProviderFields()
    {
        var candidate = new AiCandidateProjection(
            "batch_01",
            "style_01",
            "frame_01",
            "asset_01",
            "front.jpg",
            2,
            0.88m,
            ["front_view", "clear_subject"],
            IsLocked: false);

        Assert.Equal("Top-2", candidate.RankText);
        Assert.Equal("选用这张", candidate.LockText);
        Assert.True(candidate.CanLock);
        Assert.Equal("正面 · 主体清晰", candidate.EvidenceText);

        var mismatch = candidate with
        {
            EvidenceCodes =
            [
                "color_mismatch_black_not_gray",
                "missing_shoulder_strap",
                "provider_specific_unmapped_evidence"
            ]
        };
        Assert.Equal(
            "颜色与模板要求不符 · 缺少模板要求的肩带 · 需要人工核对",
            mismatch.EvidenceText);
    }

    [Fact]
    public void TransportImageAliasIsClosedOverProducingRequestForLegacyResultProjection()
    {
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "request_01",
            "correlation_01",
            "product_01",
            "batch_01",
            "style_01",
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/v1",
            "prompt/v1",
            AiAnalysisProtocol.ResultSchemaVersion,
            "copy/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/png", 10, 10, "copy_01")],
            [new AiRequirementInputV1("frame_01", "商品主体", "完整", true)]);
        var legacy = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/v1",
            [new AiRequirementMatchV1("frame_01", "img_0", 1, 0.98m, ["SUBJECT_COMPLETE"])],
            [],
            [new AiSubjectGeometryV1("img_0", 0.1m, 0.1m, 0.9m, 0.9m, 0.99m)],
            null,
            [],
            new AiUsageSummaryV1("usage_01", 1, 1, 1, null, null));

        var normalized = AiAnalysisResultReferenceNormalizer.Normalize(request, legacy);

        Assert.Equal("asset_01", Assert.Single(normalized.RequirementMatches).AssetId);
        Assert.Equal("asset_01", Assert.Single(normalized.SubjectGeometry).AssetId);
    }

    [Fact]
    public void TerminalBatchDoesNotExposePauseOrCancelActions()
    {
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.Ready,
            "分析完成，所有已选页面均可生成。",
            "进入生成",
            1,
            1,
            1,
            1,
            [],
            AiAnalysisSchedulingControlState.Running,
            null);

        Assert.False(projection.CanPause);
        Assert.False(projection.CanCancel);
    }

    [Fact]
    public void BudgetStopBlocksGenerationButPreservesCancelAction()
    {
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.BudgetStopped,
            "本轮 AI 预算已达到停止线；请降低范围或等待人工批准后继续。",
            "继续处理",
            1,
            0,
            0,
            1,
            [new AiStyleAnalysisProjection(
                "batch_01",
                "style_01",
                "SKU-01",
                AiStyleUiStatus.Analysing,
                "分析中",
                [Page("page_01", AiPageUiStatus.Analysing)],
                1)],
            AiAnalysisSchedulingControlState.Running,
            null);

        Assert.False(projection.CanEnterGeneration);
        Assert.False(projection.IsTerminal);
        Assert.False(projection.CanPause);
        Assert.True(projection.CanCancel);
    }

    [Fact]
    public void WaitingForNetworkCannotResumeWhileCommandIsBusy()
    {
        var projection = new AiBatchAnalysisProjection(
            "batch_01",
            AiBatchUiStatus.Analysing,
            "等待网络恢复。",
            "继续处理",
            1,
            0,
            0,
            1,
            [],
            AiAnalysisSchedulingControlState.WaitingForNetwork,
            "等待网络恢复。",
            IsBusy: true);

        Assert.False(projection.CanResume);
    }

    private static AiPageAnalysisProjection Page(string pageId, AiPageUiStatus status) => new(
        "batch_01",
        "style_01",
        pageId,
        pageId,
        status,
        status.ToString(),
        "处理此页",
        [],
        status == AiPageUiStatus.BusinessError ? ["缺少必需商品素材，请补充后重试。"] : []);
}
