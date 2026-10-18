using System.Windows.Input;
using System.IO;
using Desktop.Application.Templates;

namespace Desktop.App;

public sealed partial class AiAnalysisViewModel
{
    private TemplateInstanceSelectionReview? _instanceReview;
    private TemplateInstanceSelectionTarget? _selectedInstanceTarget;
    public bool IsInstanceSelectionVisible => _instanceReview is not null;
    public IReadOnlyList<TemplateInstanceSelectionTarget> InstanceSelectionTargets => _instanceReview?.Targets ?? [];
    public TemplateInstanceSelectionTarget? SelectedInstanceTarget
    {
        get => _selectedInstanceTarget;
        set { _selectedInstanceTarget = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }
    public ICommand ConfirmInstanceSelectionCommand { get; private set; } = null!;
    public ICommand CancelInstanceSelectionCommand { get; private set; } = null!;

    private async Task<bool> BeginInstanceSelectionAsync(string batchId, string styleId, string frameId, string assetId)
    {
        if (_aiBatchWorkflow is not ITemplateInstanceSelectionWorkflow workflow) return false;
        var dialogVersion = _pageDialogVersion;
        var review = await workflow.ReviewInstanceSelectionAsync(batchId, styleId, frameId, assetId, CancellationToken.None).ConfigureAwait(true);
        if (dialogVersion != _pageDialogVersion) return true;
        if (review is null) return false; // Only legacy templates retain the original frame-wide command.
        if (review.Targets.Count == 0)
        {
            AiStatusMessage = "该照片不属于此框任何实例的颜色素材组，未更改用图。请先核对本款颜色事实。";
            return true;
        }
        _instanceReview = review;
        SelectedInstanceTarget = review.Targets.Count == 1 ? review.Targets[0] : null;
        OnPropertyChanged(nameof(InstanceSelectionTargets)); OnPropertyChanged(nameof(IsInstanceSelectionVisible));
        AiStatusMessage = "请选择本次锁定的输出实例；不会更改同一 PSD 的其他实例。";
        return true;
    }

    private async Task ConfirmInstanceSelectionAsync()
    {
        var review = _instanceReview; var target = SelectedInstanceTarget;
        if (review is null || target is null || _aiBatchWorkflow is not ITemplateInstanceSelectionWorkflow workflow || _aiBusy) return;
        _aiBusy = true; NotifyAiChanged();
        try
        {
            await workflow.LockInstanceSelectionAsync(new(review.BatchId, review.StyleId, review.FrameId, review.AssetId,
                target.PageInstanceId, review.Revision, review.BaseFactsDigest), CancellationToken.None).ConfigureAwait(true);
            CloseInstanceSelection(); ClearAiReplacementState(); CloseAiCandidatePreview();
            AiStatusMessage = $"已保存 {target.Label} 的当前选图。其他实例未更改；生成后冻结，修正须新建批次。";
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            CloseInstanceSelection();
            AiStatusMessage = error.Message == "TPL_INSTANCE_PRODUCTION_FACTS_FROZEN"
                ? "该批次已冻结，不能修改用图。请新建批次。"
                : "实例选图事实或版本已变化，未保存。请重新打开选图；若分析事实已变化，请新建批次。";
        }
        finally { _aiBusy = false; NotifyAiChanged(); }
    }

    private void CloseInstanceSelection()
    {
        _instanceReview = null; SelectedInstanceTarget = null;
        OnPropertyChanged(nameof(IsInstanceSelectionVisible)); OnPropertyChanged(nameof(InstanceSelectionTargets));
    }
}
