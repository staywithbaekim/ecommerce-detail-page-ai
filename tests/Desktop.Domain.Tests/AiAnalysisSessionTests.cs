using Desktop.Domain.Ai;

namespace Desktop.Domain.Tests;

public sealed class AiAnalysisSessionTests
{
    [Fact]
    public void SessionFollowsBoundedLifecycleAndLocksWithCas()
    {
        var session = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01")
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
    public void UnknownResultCanEnterManualReviewButTerminalSessionCannotChange()
    {
        var unknown = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01")
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.AiResultUnknown)
            .AdvanceTo(AiAnalysisSessionStatus.ManualReview);

        Assert.Equal(AiAnalysisSessionStatus.ManualReview, unknown.Status);
        var cancelled = AiAnalysisSession.Create("session_02", "batch_01", "style_01", "snapshot_01", "key_02")
            .AdvanceTo(AiAnalysisSessionStatus.Cancelled);
        Assert.Throws<InvalidOperationException>(() => cancelled.AdvanceTo(AiAnalysisSessionStatus.Queued));
    }
}
