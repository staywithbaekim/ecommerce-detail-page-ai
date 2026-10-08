using Desktop.Application.Templates;

namespace Desktop.App;

public sealed partial class TemplateWorkspaceViewModel
{
    private readonly ITemplateRuleInterpreter? _ruleInterpreter;
    public TemplateRuleWorkbenchViewModel? RuleWorkbench { get; private set; }
    public bool IsRuleWorkbenchVisible => _templateScreen is TemplateScreen.Canvas or TemplateScreen.ReferenceLearning
        && _templateWorkspace is ITemplateRuleEditingService && _templateCalibration is not null;

    private async Task RefreshRuleWorkbenchAsync()
    {
        if (_templateDraft is null || _templateWorkspace is not ITemplateRuleEditingService || _templateCalibration is null) return;
        if (RuleWorkbench?.Session.DraftRevisionId != _templateDraft.DraftRevisionId)
        {
            RuleWorkbench?.Dispose();
            RuleWorkbench = new(_templateWorkspace, _templateCalibration, _templateDraft, _templateDraftWrite,
                _ruleInterpreter, _host.EnsureUploadConsentAsync);
            OnPropertyChanged(nameof(RuleWorkbench));
        }
        await RuleWorkbench!.RefreshAsync(flush: false).ConfigureAwait(true);
    }

    public async Task FlushRuleWorkbenchAsync()
    {
        if (RuleWorkbench is { } workbench) { workbench.CancelInterpretation(); await workbench.FlushAsync().ConfigureAwait(true); }
    }

    public async Task LearnRulesInWorkbenchAsync()
    {
        if (ReferenceLearningBusy || TemplateBusy || RuleWorkbench is null) return;
        await FlushRuleWorkbenchAsync().ConfigureAwait(true);
        if (_folderPicker is null) { TemplateStatusMessage = "本机文件夹选择器不可用；仍可手工编辑。"; return; }
        var folder = await _folderPicker.PickFolderAsync("选择参考款素材（商品照片与尺码图共用）",
            HasReferenceLearningPhotoDirectory ? _referenceLearningPhotoDirectory : null, CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder)) return;
        _referenceLearningPhotoDirectory = System.IO.Path.GetFullPath(folder);
        try
        {
            await StartReferenceLearningAsync().ConfigureAwait(true);
            if (_referenceLearningCommit is null || _templateDraft is null || _referenceLearningDocuments.Count == 0) return;
            // This reuses the evidence-closed batch adoption path. Local AI-failure
            // placeholders and ambiguous candidates are not promoted into bindings.
            await ConfirmAllQualifiedReferenceLearningAsync().ConfigureAwait(true);
            var ready = _referenceLearningDocuments.Values.Where(document => document.CanCommit).ToArray();
            if (ready.Length > 0)
            {
                await _templateDraftWrite.WaitAsync().ConfigureAwait(true);
                try
                {
                    var result = await _referenceLearningCommit.StageDraftAsync(ready, _templateDraft, CancellationToken.None).ConfigureAwait(true);
                    if (!result.IsSuccess) { TemplateStatusMessage = result.Error?.UserAction ?? "学习结果未写入草稿。"; return; }
                }
                finally { _templateDraftWrite.Release(); }
                await LoadDraftPagesAsync(populateImportedFiles: true).ConfigureAwait(true);
            }
            TemplateStatusMessage = ready.Length == _referenceLearningDocuments.Count
                ? "有效学习结果已填入逐框规则；可直接修改，最后只需整组确认一次。"
                : "部分页面的学习证据未闭合，未自动登记；" + ReferenceLearningStatusMessage;
        }
        finally { SetTemplateScreen(TemplateScreen.Canvas); }
    }

    public async Task ConfirmRuleWorkbenchAsync(TemplateRuleConfirmation confirmation)
    {
        if (RuleWorkbench is not { } workbench) return;
        await workbench.FlushAsync().ConfigureAwait(true);
        _templateDetails = await workbench.ConfirmAsync(confirmation).ConfigureAwait(true);
        TemplateStatusMessage = "模板组规则已保存；实际出图仍须通过生产预检。";
        OnPropertyChanged(nameof(TemplateDetailsTitle)); OnPropertyChanged(nameof(TemplateDetailsMeta));
        OnPropertyChanged(nameof(TemplateDetailPages)); OnPropertyChanged(nameof(CanIncrementTemplate));
        SetTemplateScreen(TemplateScreen.Details);
    }
}
