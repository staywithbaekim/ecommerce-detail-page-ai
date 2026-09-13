using Desktop.Domain.Ai;

namespace Desktop.Domain.Tests;

public sealed class AiAnalysisSessionTests
{
    [Fact]
    public void SessionFollowsBoundedLifecycleAndLocksWithCas()
    {
        var session = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01", "asset_02"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.Validated);

        var locked = session.LockSelection("frame_01", "asset_01", 0);

        Assert.Equal(AiAnalysisSessionStatus.ManualReview, locked.Status);
        Assert.Equal(1, locked.SelectionRevision);
        Assert.Equal("asset_01", locked.LockedSelections["frame_01"].AssetId);
        Assert.Throws<AiSelectionConcurrencyException>(() =>
            locked.LockSelection("frame_01", "asset_02", 0));
    }

    [Fact]
    public void UnknownResultCannotEnterManualReviewOrPlanClosed()
    {
        var unknown = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.AiResultUnknown);

        Assert.Throws<InvalidOperationException>(() => unknown.AdvanceTo(AiAnalysisSessionStatus.ManualReview));
        Assert.Throws<InvalidOperationException>(() => unknown.AdvanceTo(AiAnalysisSessionStatus.PlanClosed));
        var locked = unknown.LockSelection("frame_01", "asset_01", 0);
        Assert.Equal(AiAnalysisSessionStatus.AiResultUnknown, locked.Status);
        Assert.False(locked.HasVerifiedResult);
        Assert.Equal(1, locked.SelectionRevision);
        Assert.Equal("asset_01", locked.LockedSelections["frame_01"].AssetId);
        var restored = AiAnalysisSession.Restore(
            locked.SessionId,
            locked.BatchId,
            locked.StyleId,
            locked.TemplateSnapshotId,
            locked.AnalysisKey,
            locked.Status,
            locked.SelectionRevision,
            locked.LockedSelections.Values,
            locked.AllowedRequirementIds,
            locked.AllowedAssetIds,
            locked.HasVerifiedResult);
        Assert.Equal(AiAnalysisSessionStatus.AiResultUnknown, restored.Status);
        Assert.Equal("asset_01", restored.LockedSelections["frame_01"].AssetId);
        Assert.Throws<AiSelectionConcurrencyException>(() =>
            locked.LockSelection("frame_01", "asset_01", 0));
        Assert.Throws<InvalidOperationException>(() =>
            unknown.LockSelection("frame_02", "asset_01", 0));
        Assert.Throws<InvalidOperationException>(() =>
            unknown.LockSelection("frame_01", "asset_02", 0));
        var cancelled = AiAnalysisSession.Create("session_02", "batch_01", "style_01", "snapshot_01", "key_02", ["frame_01"], ["asset_01"])
            .AdvanceTo(AiAnalysisSessionStatus.Cancelled);
        Assert.Throws<InvalidOperationException>(() => cancelled.AdvanceTo(AiAnalysisSessionStatus.Queued));
    }

    [Fact]
    public void UploadAndAnalysisTerminalErrorsAreReachableAndImmutable()
    {
        var uploadFailure = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.TerminalError);
        Assert.Throws<InvalidOperationException>(() => uploadFailure.AdvanceTo(AiAnalysisSessionStatus.Queued));

        var analysisFailure = AiAnalysisSession.Create("session_02", "batch_01", "style_01", "snapshot_01", "key_02", ["frame_01"], ["asset_01"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.TerminalError);
        Assert.Throws<InvalidOperationException>(() => analysisFailure.AdvanceTo(AiAnalysisSessionStatus.ManualReview));
    }

    [Fact]
    public void LockRejectsUnknownReferencesAndStaleRevisionButAllowsExplicitReplacement()
    {
        var session = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01", "asset_02"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.Validated);

        Assert.Throws<InvalidOperationException>(() => session.LockSelection("frame_02", "asset_01", 0));
        Assert.Throws<InvalidOperationException>(() => session.LockSelection("frame_01", "asset_03", 0));
        var first = session.LockSelection("frame_01", "asset_01", 0);
        Assert.Throws<AiSelectionConcurrencyException>(() => first.LockSelection("frame_01", "asset_02", 0));
        var replaced = first.LockSelection("frame_01", "asset_02", 1);
        Assert.Equal("asset_02", replaced.LockedSelections["frame_01"].AssetId);
    }

    [Fact]
    public void RestoreRejectsUndefinedStatusValues()
    {
        Assert.Throws<InvalidOperationException>(() => AiAnalysisSession.Restore(
            "session_01", "batch_01", "style_01", "snapshot_01", "key_01",
            (AiAnalysisSessionStatus)999, 0, [], ["frame_01"], ["asset_01"], false));
    }

    [Fact]
    public void ValidatedCannotBeRestoredWithManualLocks()
    {
        Assert.Throws<InvalidOperationException>(() => AiAnalysisSession.Restore(
            "session_01", "batch_01", "style_01", "snapshot_01", "key_01",
            AiAnalysisSessionStatus.Validated, 1,
            [new AiLockedSelection("frame_01", "asset_01", 1)], ["frame_01"], ["asset_01"], true));
    }
}
