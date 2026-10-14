using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Desktop.Application.Templates;

namespace Desktop.App;

/// <summary>One live editor per draft, with page-keyed state and one serialized write path.</summary>
public sealed class TemplateRuleWorkbenchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITemplateWorkspaceService _workspace;
    private readonly ITemplateRuleEditingService _editing;
    private readonly ITemplateCalibrationService _calibration;
    private readonly SemaphoreSlim _writes;
    private CancellationTokenSource? _debounce;
    private readonly ITemplateRuleInterpreter? _interpreter;
    private readonly Func<Task<bool>>? _ensureConsent;
    private readonly object _lifetimeGate = new();
    private CancellationTokenSource? _interpretCancellation;
    private bool _interpreting;
    public bool CanInterpret => !_interpreting;
    private readonly Stack<string> _undo = new();
    private bool _undoRestored;
    private volatile bool _disposed;
    private string _status = "草稿自动保存 · 编辑规则可定位真实框 · Ctrl+Enter 发送";
    private int _segment;
    private IReadOnlyList<TemplateRulePageViewModel> _visiblePages = [];
    public TemplateRuleWorkbenchViewModel(ITemplateWorkspaceService workspace, ITemplateCalibrationService calibration,
        TemplateDraftSession session, SemaphoreSlim writes, ITemplateRuleInterpreter? interpreter = null, Func<Task<bool>>? ensureConsent = null)
    {
        _workspace = workspace;
        _editing = (ITemplateRuleEditingService)workspace;
        _calibration = calibration;
        Session = session;
        _writes = writes;
        _interpreter = interpreter; _ensureConsent = ensureConsent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public TemplateDraftSession Session { get; }
    public TemplateRuleDraftView? Draft { get; private set; }
    public ObservableCollection<TemplateRulePageViewModel> Pages { get; } = [];
    public IReadOnlyList<TemplateRulePageViewModel> VisiblePages => _visiblePages;
    public string SegmentText => Pages.Count == 0 ? "尚未导入 PSD" : $"第 {_segment * 6 + 1}–{Math.Min((_segment + 1) * 6, Pages.Count)} 个 / 共 {Pages.Count} 个 PSD";
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public bool CanUndo => _undo.Count > 0;
    public bool IsEmpty => Pages.Count == 0;
    public IReadOnlyList<TemplateRuleRelationGroup> Relations => TemplateRuleRelations.Build(Draft, Pages);
    public bool HasPendingEdits => Pages.SelectMany(page => page.EditableFrames).Any(frame => !frame.IsExcluded && frame.IsDirty);
    public string GroupRulesText => Draft?.Rules is not { } rules
        ? "沿用模板当前生成规则。跨模板条件尚未修改；不会从备注猜测主副色。"
        : string.Join("\n\n", rules.Branches.Select(branch =>
            $"颜色数 {branch.Condition.Minimum}–{branch.Condition.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不限"}（{Parity(branch.Condition.Parity)}）\n"
            + string.Join("\n", branch.Recipes.Select(recipe => $"{PageLabel(recipe.PageId)}：{Repeat(recipe)}"))));
    public string DiagnosticsText => Draft is null ? ""
        : string.Join("\n", Draft.Diagnostics.Select(item => $"{(item.PageId is null ? "模板组" : PageLabel(item.PageId))}：{DiagnosticMessage(item.Code)}（{item.Code}）"));
    private static string DiagnosticMessage(string code) => code switch
    {
        "TPL_RULE_ROUTE_AMBIGUOUS" => "使用条件重叠，请修改条件，避免同一种情况同时命中两组",
        "TPL_RULE_CONDITION_INVALID" => "颜色数量范围无效，请修改使用条件",
        "TPL_RULE_PAGE_REFERENCE_INVALID" => "引用的模板已移除，请替换或移除这次使用",
        "TPL_RULE_BINDINGS_INVALID" => "框的颜色关系未闭合，请在本页用途中补齐框映射",
        "TPL_RULE_COLOR_ALLOCATION_INVALID" => "重复方式与分配颜色不一致，请检查每次使用的颜色",
        "TPL_RULE_RECIPES_INVALID" or "TPL_RULE_BRANCHES_INVALID" => "缺少使用条件或模板，请补充后再确认",
        _ => "这条规则尚不可执行，请检查对应条件、次数及框引用；草稿已保留"
    };
    public IEnumerable<TemplateRuleReferenceChoice> ReferenceChoices => Draft?.Aliases.Select(alias =>
        new TemplateRuleReferenceChoice(alias.PageId, alias.FrameId, AliasLabel(alias),
            Pages.Any(page => page.PageId == alias.PageId && !page.IsReadOnly
                && (alias.FrameId is null || page.Frames.Any(frame => frame.FrameId == alias.FrameId)
                    || page.EditableFrames.Any(frame => frame.FrameId == alias.FrameId)
                        && Draft.Candidates.Any(candidate => candidate.FrameId == alias.FrameId && candidate.CanInterpret))))
        { DisplayLabel = AliasLabel(alias) + " · " + (Pages.SingleOrDefault(page => page.PageId == alias.PageId)?.Name ?? "已移除") }) ?? [];

    public async Task RefreshAsync(bool flush = true)
    {
        if (flush) await FlushAsync().ConfigureAwait(true);
        var draft = await _workspace.GetDraftAsync(Session, CancellationToken.None).ConfigureAwait(true);
        Draft = await _editing.GetRulesAsync(Session, CancellationToken.None).ConfigureAwait(true);
        if (!_undoRestored)
        {
            foreach (var command in (Draft.UndoCommandIds ?? []).Reverse()) _undo.Push(command);
            _undoRestored = true;
        }
        var existing = Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var ordered = new List<TemplateRulePageViewModel>();
        foreach (var source in draft.Pages.OrderBy(page => page.Page.Order))
        {
            if (!existing.TryGetValue(source.Page.PageId, out var page))
                page = new TemplateRulePageViewModel(source, PageLabel(source.Page.PageId));
            else
                page.UpdatePage(source);
            page.Label = PageLabel(source.Page.PageId);
            if (source.Page.ContentType == TemplatePageContentTypeOption.NormalComposition)
            {
                var calibration = await _calibration.LoadAsync(Session, page.PageId, CancellationToken.None).ConfigureAwait(true);
                page.SetFrames(calibration, Draft, QueueSave);
            }
            ordered.Add(page);
        }
        Pages.Clear();
        foreach (var page in ordered) Pages.Add(page);
        _segment = Math.Min(_segment, Math.Max(0, (Pages.Count - 1) / 6));
        PublishSegment();
        NotifyDraft();
    }

    private async void QueueSave()
    {
        CancelInterpretation();
        CancellationTokenSource delay;
        CancellationToken token;
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _debounce?.Cancel();
            delay = _debounce = new CancellationTokenSource();
            token = delay.Token;
        }
        try
        {
            await Task.Delay(450, token).ConfigureAwait(true);
            await FlushAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException)
        {
            Status = $"保存未完成，输入已保留：{error.Message}";
        }
        finally
        {
            lock (_lifetimeGate)
            {
                if (ReferenceEquals(_debounce, delay)) _debounce = null;
            }
            delay.Dispose();
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed || Draft is null) return;
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            while (HasPendingEdits)
            {
                var candidates = Pages.SelectMany(page => page.EditableFrames
                    .Where(frame => frame.IsCandidate && !frame.IsExcluded && frame.IsDirty)
                    .Select(frame => (Page: page, Frame: frame, Value: frame.Description))).ToArray();
                foreach (var item in candidates)
                {
                    var view = await _calibration.SetSelectionRequirementAsync(Session, item.Page.PageId,
                        item.Frame.Target.FrameId, item.Value, CancellationToken.None).ConfigureAwait(true);
                    item.Frame.AcceptSaved(item.Value.Trim());
                    Draft = await _editing.GetRulesAsync(Session, CancellationToken.None).ConfigureAwait(true);
                    item.Page.SetFrames(view, Draft, QueueSave);
                    Status = "选图规则已保存，无需参考款素材。其他候选图层不会自动登记。";
                    NotifyDraft();
                }
                var pending = Pages.SelectMany(page => page.Frames).Where(frame => !frame.IsExcluded && frame.IsDirty)
                    .Select(frame => (Frame: frame, Value: frame.Description)).ToArray();
                if (pending.Length == 0) continue;
                var command = "rule_" + Guid.NewGuid().ToString("N");
                var patch = new TemplateRulePatch(command, Draft.Version,
                    new([.. pending.Select(item => new TemplateFrameRuleEdit(item.Frame.FrameId, item.Value))], []));
                Draft = await _editing.ApplyRulePatchAsync(Session, patch, CancellationToken.None).ConfigureAwait(true);
                foreach (var item in pending) item.Frame.AcceptSaved(item.Value.Trim());
                _undo.Push(command);
                Status = "规则已保存，可撤销本轮修改。";
                NotifyDraft();
            }
        }
        finally { _writes.Release(); }
    }

    public async Task ApplyBranchesAsync(IEnumerable<TemplateBranchRuleEdit> changes)
    {
        CancelInterpretation();
        await FlushAsync().ConfigureAwait(true);
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            var command = "rule_" + Guid.NewGuid().ToString("N");
            Draft = await _editing.ApplyRulePatchAsync(Session, new(command, Draft!.Version, new([], [.. changes])), CancellationToken.None).ConfigureAwait(true);
            _undo.Push(command);
            Status = "总规则已保存；有问题的草稿仍可保留。";
            NotifyDraft();
        }
        finally { _writes.Release(); }
    }

    public async Task UndoAsync()
    {
        CancelInterpretation();
        await FlushAsync().ConfigureAwait(true);
        if (!_undo.TryPeek(out var command) || Draft is null) return;
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            Draft = await _editing.UndoRulePatchAsync(Session, command, "undo_" + Guid.NewGuid().ToString("N"), Draft.Version, CancellationToken.None).ConfigureAwait(true);
            _undo.Pop();
            foreach (var frame in Pages.SelectMany(page => page.Frames))
                if (Draft.Frames.SingleOrDefault(item => item.FrameId == frame.FrameId) is { } saved) frame.Reset(saved.Description);
            NotifyDraft();
            Status = "已撤销本轮修改，其他规则保持。";
        }
        finally { _writes.Release(); }
        await RefreshAsync(flush: false).ConfigureAwait(true);
    }

    public async Task RegisterAsync(TemplateRulePageViewModel page, TemplateFrameProjection target, string description)
    {
        CancelInterpretation();
        await FlushAsync().ConfigureAwait(true);
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!page.Candidates.Contains(target)) throw new InvalidOperationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            await _calibration.SetSelectionRequirementAsync(Session, page.PageId, target.FrameId, description, CancellationToken.None).ConfigureAwait(true);
        }
        finally { _writes.Release(); }
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task StopReplacingAsync(TemplateRulePageViewModel page, TemplateRuleFrameViewModel frame)
    {
        CancelInterpretation();
        if (page.IsReadOnly) throw new InvalidOperationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        await FlushAsync().ConfigureAwait(true);
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            var command = "exclude_frame_" + Guid.NewGuid().ToString("N");
            Draft = await _editing.ApplyRulePatchAsync(Session, new(command, Draft!.Version,
                new([], []) { CandidateVisibility = [new(frame.FrameId, true)] }), CancellationToken.None).ConfigureAwait(true);
            _undo.Push(command);
        }
        finally { _writes.Release(); }
        await RefreshAsync().ConfigureAwait(true);
        Status = "已设为不替换；PSD 图层与规则均保留，可随时恢复。";
    }

    public async Task RestoreReplacingAsync(TemplateRulePageViewModel page, TemplateRuleFrameViewModel frame)
    {
        CancelInterpretation();
        if (page.IsReadOnly) throw new InvalidOperationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        await FlushAsync().ConfigureAwait(true);
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            var command = "restore_frame_" + Guid.NewGuid().ToString("N");
            Draft = await _editing.ApplyRulePatchAsync(Session, new(command, Draft!.Version,
                new([], []) { CandidateVisibility = [new(frame.FrameId, false)] }), CancellationToken.None).ConfigureAwait(true);
            _undo.Push(command);
        }
        finally { _writes.Release(); }
        await RefreshAsync().ConfigureAwait(true);
        Status = "已恢复替换；原有规则和 PSD 图层保持不变。";
    }

    public async Task RemovePageAsync(TemplateRulePageViewModel page)
    {
        CancelInterpretation();
        if (page.IsReadOnly) throw new InvalidOperationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        await FlushAsync().ConfigureAwait(true);
        var impact = await _editing.GetRuleImpactAsync(Session, page.PageId, CancellationToken.None).ConfigureAwait(true);
        await _writes.WaitAsync().ConfigureAwait(true);
        try
        {
            var command = "remove_page_" + Guid.NewGuid().ToString("N");
            Draft = await _editing.ApplyRulePatchAsync(Session, new(command, Draft!.Version,
                new([], [], Pages: [new(page.PageId, null, [])])), CancellationToken.None).ConfigureAwait(true);
            _undo.Push(command);
        }
        finally { _writes.Release(); }
        await RefreshAsync().ConfigureAwait(true);
        Status = $"已从草稿移除 {page.Label}；影响 {impact.Count} 处关系。可撤销，源 PSD 与历史资源不变。";
    }

    public async Task<bool> InterpretAsync(string text, IReadOnlyList<TemplateRuleReferenceChoice> references)
    {
        if (_interpreting) return false;
        if (_interpreter is null) throw new InvalidOperationException("规则解释服务不可用，输入保留；仍可手工编辑。");
        await FlushAsync().ConfigureAwait(true);
        if (Draft is null || _ensureConsent is not null && !await _ensureConsent().ConfigureAwait(true)) return false;
        _interpreting = true; Changed(nameof(CanInterpret));
        var cancellation = new CancellationTokenSource();
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                _interpreting = false;
                Changed(nameof(CanInterpret));
                return false;
            }
            _interpretCancellation = cancellation;
        }
        Status = "正在解释本条规则；仅发送必要文本和结构，不上传 PSD。编辑其他字段会取消本次应用。";
        try
        {
            var before = Draft;
            var result = await _interpreter.InterpretAsync(Session,
                new("interpret_" + Guid.NewGuid().ToString("N"), Draft.Version, text,
                    [.. references.Select(reference => new TemplateRuleTargetReference(reference.PageId, reference.FrameId))]), cancellation.Token).ConfigureAwait(true);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            Draft = result.Draft;
            if (result.AppliedCommandId is { } command) _undo.Push(command);
            await RefreshAsync(flush: false).ConfigureAwait(true);
            Status = result.AppliedCommandId is null ? result.Message : AppliedSummary(before, Draft!);
            return !result.RequiresClarification;
        }
        finally
        {
            lock (_lifetimeGate)
            {
                if (ReferenceEquals(_interpretCancellation, cancellation)) _interpretCancellation = null;
                cancellation.Dispose();
            }
            _interpreting = false;
            Changed(nameof(CanInterpret));
        }
    }
    public void CancelInterpretation()
    {
        lock (_lifetimeGate) _interpretCancellation?.Cancel();
    }
    private string AppliedSummary(TemplateRuleDraftView before, TemplateRuleDraftView after)
    {
        var fields = after.Frames.Where(frame => before.Frames.SingleOrDefault(old => old.FrameId == frame.FrameId)?.Description != frame.Description)
            .Select(frame => ReferenceChoices.SingleOrDefault(reference => reference.FrameId == frame.FrameId)?.Label ?? PageLabel(frame.PageId)).ToArray();
        return fields.Length > 0 ? "已更新 " + string.Join("、", fields) + "；总览已同步，可撤销。"
            : "已应用模板使用规则；请在总览核对实际条件、顺序和次数，可撤销。";
    }

    public async Task<TemplateRuleConfirmation> OverviewAsync()
    {
        await FlushAsync().ConfigureAwait(true);
        Draft = await _editing.GetRulesAsync(Session, CancellationToken.None).ConfigureAwait(true);
        NotifyDraft();
        return new(Draft.Version, Draft.RuleDigest);
    }

    public Task<TemplateGroupDetails> ConfirmAsync(TemplateRuleConfirmation confirmation) =>
        _editing.ConfirmRulesAsync(Session, confirmation, CancellationToken.None);

    public void ChangeSegment(int delta)
    {
        _segment = Math.Clamp(_segment + delta, 0, Math.Max(0, (Pages.Count - 1) / 6));
        PublishSegment(); Changed(nameof(SegmentText));
    }

    public void Locate(string pageId, string? frameId)
    {
        var page = Pages.SingleOrDefault(page => page.PageId == pageId);
        if (page is null) { Status = "该引用已失效，不会指向其他 PSD。"; return; }
        _segment = Pages.IndexOf(page) / 6;
        PublishSegment(); Changed(nameof(SegmentText));
        page.SelectedFrame = page.EditableFrames.SingleOrDefault(frame => frame.FrameId == frameId);
    }

    private string PageLabel(string? id) => Draft?.Aliases.SingleOrDefault(alias => alias.PageId == id && alias.FrameId is null) is { } alias ? $"@{alias.Number}" : "失效引用";
    private string AliasLabel(TemplateReferenceAliasView alias) => alias.FrameId is null ? $"@{alias.Number}" : $"{PageLabel(alias.PageId)} 的框{alias.Number}";
    private static string Parity(TemplateRuleParityOption parity) => parity switch { TemplateRuleParityOption.Odd => "奇数", TemplateRuleParityOption.Even => "偶数", _ => "不限奇偶" };
    private static string Repeat(TemplatePageRecipeView recipe) => recipe.Repeat == TemplateRuleRepeatOption.Once ? "每款一次"
        : $"{(recipe.ColorRange == TemplateRuleColorRangeOption.Secondary ? "副色" : "全部颜色")}每 {recipe.ColorSlots.Count(slot => slot.Source == TemplateRuleColorSourceOption.Allocated)} 色一张";
    private void NotifyDraft()
    {
        foreach (var page in Pages) page.SetPurpose(TemplateRuleRelations.Purpose(page, Relations));
        foreach (var name in new[] { nameof(Draft), nameof(SegmentText), nameof(GroupRulesText), nameof(DiagnosticsText), nameof(CanUndo), nameof(ReferenceChoices), nameof(IsEmpty), nameof(Relations) }) Changed(name);
    }
    private void PublishSegment()
    {
        var next = Pages.Skip(_segment * 6).Take(6).ToArray();
        if (_visiblePages.SequenceEqual(next)) return;
        _visiblePages = next; Changed(nameof(VisiblePages));
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _interpretCancellation?.Cancel();
            _debounce?.Cancel();
        }
        GC.SuppressFinalize(this);
    }
}

public sealed record TemplateRuleReferenceChoice(string PageId, string? FrameId, string Label, bool IsActive)
{
    public string DisplayLabel { get; init; } = Label;
}

public sealed class TemplateRulePageViewModel : INotifyPropertyChanged
{
    private TemplateRuleFrameViewModel? _selected;
    public TemplateRulePageViewModel(TemplateDraftPage page, string label)
    {
        PageId = page.Page.PageId; Label = label;
        UpdatePage(page, notify: false);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string PageId { get; }
    public string Name { get; private set; } = string.Empty;
    public string DisplayName => Label + "  " + Name;
    public string Label { get; internal set; }
    public bool IsReadOnly { get; private set; }
    public bool IsSizeTable { get; private set; }
    public string? PreviewSource { get; private set; }
    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }
    public TemplateGenerationModeOption GenerationMode { get; private set; }
    public bool Enabled { get; private set; }
    public int? VariantCapacity { get; private set; }
    public string Purpose { get; private set; } = "每款使用一次";
    public string EmptyFrameMessage => IsSizeTable ? "按本款素材中的尺码图换数据；与商品照片共用输入，不另选目录。"
        : EditableFrames.Count > 0 ? "" : "未找到有效图片位。隐藏、被覆盖和无效残留不会显示为框；复杂合并内容仍需受支持的替换结构。参考款学习可选。";
    internal void SetPurpose(string value)
    {
        if (Purpose == value) return;
        Purpose = value; PropertyChanged?.Invoke(this, new(nameof(Purpose)));
    }
    public ObservableCollection<TemplateRuleFrameViewModel> Frames { get; } = [];
    // Candidate rows are editable but not production bindings. Only deliberate
    // nonempty user text takes the existing rule-only calibration write path.
    public ObservableCollection<TemplateRuleFrameViewModel> EditableFrames { get; } = [];
    public IReadOnlyList<TemplateFrameProjection> Candidates { get; private set; } = [];
    public TemplateRuleFrameViewModel? SelectedFrame
    {
        get => _selected;
        set { if (ReferenceEquals(_selected, value)) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(SelectedFrame))); }
    }

    internal void UpdatePage(TemplateDraftPage page, bool notify = true)
    {
        if (!string.Equals(PageId, page.Page.PageId, StringComparison.Ordinal))
            throw new InvalidOperationException("TPL_RULE_PAGE_ID_CHANGED");

        var nameChanged = !string.Equals(Name, page.Page.Name, StringComparison.Ordinal);
        var readOnlyChanged = IsReadOnly != !page.IsAffected;
        var sizeTableChanged = IsSizeTable != (page.Page.ContentType == TemplatePageContentTypeOption.DynamicSizeTable);
        var previewChanged = !string.Equals(PreviewSource, page.PreviewSourceReference, StringComparison.Ordinal);
        var widthChanged = CanvasWidth != page.Resource.Inspection.Canvas.Width;
        var heightChanged = CanvasHeight != page.Resource.Inspection.Canvas.Height;

        Name = page.Page.Name;
        IsReadOnly = !page.IsAffected;
        IsSizeTable = page.Page.ContentType == TemplatePageContentTypeOption.DynamicSizeTable;
        PreviewSource = page.PreviewSourceReference;
        CanvasWidth = page.Resource.Inspection.Canvas.Width;
        CanvasHeight = page.Resource.Inspection.Canvas.Height;
        GenerationMode = page.Page.GenerationMode; Enabled = page.Page.Enabled; VariantCapacity = page.Page.VariantCapacity;

        if (sizeTableChanged && IsSizeTable)
        {
            Frames.Clear();
            EditableFrames.Clear();
            SelectedFrame = null;
        }

        if (!notify) return;
        if (nameChanged) PropertyChanged?.Invoke(this, new(nameof(Name)));
        if (readOnlyChanged) PropertyChanged?.Invoke(this, new(nameof(IsReadOnly)));
        if (sizeTableChanged) PropertyChanged?.Invoke(this, new(nameof(IsSizeTable)));
        if (previewChanged) PropertyChanged?.Invoke(this, new(nameof(PreviewSource)));
        if (widthChanged) PropertyChanged?.Invoke(this, new(nameof(CanvasWidth)));
        if (heightChanged) PropertyChanged?.Invoke(this, new(nameof(CanvasHeight)));
        if (sizeTableChanged) PropertyChanged?.Invoke(this, new(nameof(Frames)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyFrameMessage)));
    }
    internal void SetFrames(TemplateCalibrationDraftView view, TemplateRuleDraftView draft, Action changed)
    {
        var selectedTarget = SelectedFrame?.Target.FrameId;
        var existing = EditableFrames.ToDictionary(frame => frame.Target.FrameId, StringComparer.Ordinal);
        var rows = new List<TemplateRuleFrameViewModel>();
        var registered = new List<TemplateRuleFrameViewModel>();
        var candidates = DraftCandidates(draft, PageId);
        var orderedTargets = TemplateCalibrationService.OrderFramesVisually(view.Frames);
        foreach (var target in orderedTargets.Where(target => target.HasPersistedBinding && target.PersistedFrameId is not null
            && target.IsVisible && HasRulePreviewGeometry(target))
            .OrderBy(target => CandidateNumber(candidates, target, draft)))
        {
            var id = target.PersistedFrameId!;
            var candidate = candidates.SingleOrDefault(item => item.Target.LayerId == target.LayerId
                && item.Target.LayerPath.SequenceEqual(target.LayerPath));
            var alias = draft.Aliases.Single(item => item.PageId == PageId && item.FrameId == id);
            var authoritative = draft.Frames.SingleOrDefault(item => item.FrameId == id);
            var excluded = candidate?.IsExcluded == true;
            if (existing.TryGetValue(target.FrameId, out var frame))
            {
                frame.UpdateTarget(target, id, $"框{(candidate is null ? alias.Number : CandidateNumber(candidates, target, draft))}", isCandidate: false, isExcluded: excluded);
                if (!frame.IsDirty) frame.Reset(authoritative?.Description ?? target.SelectionRequirement);
            }
            else frame = new(target, id, $"框{(candidate is null ? alias.Number : CandidateNumber(candidates, target, draft))}",
                authoritative?.Description ?? target.SelectionRequirement, changed,
                isExcluded: excluded);
            rows.Add(frame); registered.Add(frame);
        }
        var available = orderedTargets.Where(target => !target.HasPersistedBinding && target.IsPreviewSupported && target.IsVisible
            && HasMeaningfulCompositeContribution(target) && HasRulePreviewGeometry(target)).ToArray();
        Candidates = available;
        foreach (var target in available)
        {
            var reserved = candidates.SingleOrDefault(candidate => candidate.Target.LayerId == target.LayerId
                && candidate.Target.LayerPath.SequenceEqual(target.LayerPath));
            var id = reserved?.FrameId ?? target.FrameId;
            var number = reserved is null ? 0 : CandidateNumber(candidates, target, draft);
            var label = $"框{number}";
            if (!existing.TryGetValue(target.FrameId, out var frame))
                frame = new(target, id, label, "", changed, isCandidate: true, isExcluded: reserved?.IsExcluded == true);
            else frame.UpdateTarget(target, id, label, isCandidate: true, isExcluded: reserved?.IsExcluded == true);
            rows.Add(frame);
        }
        SyncRows(Frames, registered);
        var visualOrder = orderedTargets.Select((target, index) => (target.FrameId, index))
            .ToDictionary(item => item.FrameId, item => item.index, StringComparer.Ordinal);
        rows.Sort((left, right) => visualOrder[left.Target.FrameId].CompareTo(visualOrder[right.Target.FrameId]));
        SyncRows(EditableFrames, rows);
        SelectedFrame = EditableFrames.SingleOrDefault(frame => frame.Target.FrameId == selectedTarget);
        PropertyChanged?.Invoke(this, new(nameof(Candidates)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyFrameMessage)));
    }
    private static TemplateRuleCandidateView[] DraftCandidates(TemplateRuleDraftView draft, string pageId) =>
        [.. draft.Candidates.Concat(draft.ExcludedCandidates).Where(candidate => candidate.PageId == pageId)];

    private static int CandidateNumber(TemplateRuleCandidateView[] candidates, TemplateFrameProjection target,
        TemplateRuleDraftView draft)
    {
        var candidate = candidates.SingleOrDefault(item => item.Target.LayerId == target.LayerId
            && item.Target.LayerPath.SequenceEqual(target.LayerPath));
        return candidate is null ? int.MaxValue
            : draft.Aliases.Single(alias => alias.PageId == candidate.PageId && alias.FrameId == candidate.FrameId).Number;
    }
    private static void SyncRows(ObservableCollection<TemplateRuleFrameViewModel> collection,
        List<TemplateRuleFrameViewModel> rows)
    {
        for (var i = collection.Count - 1; i >= 0; i--) if (!rows.Contains(collection[i])) collection.RemoveAt(i);
        for (var i = 0; i < rows.Count; i++)
        {
            var position = collection.IndexOf(rows[i]);
            if (position < 0) collection.Insert(i, rows[i]);
            else if (position != i) collection.Move(position, i);
        }
    }
    // The legacy live-replacement canvas cannot simulate masks. This control uses
    // the PSD composite and an already calibrated document-space rectangle instead.
    // Do not discard a persisted binding merely because that older canvas cannot
    // render it. Unregistered candidates retain their stricter support gate above.
    private static bool HasRulePreviewGeometry(TemplateFrameProjection target) => target.PreviewBounds is { } bounds
        ? double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
            && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
            && bounds.Width > 0 && bounds.Height > 0
        : target.IsPreviewSupported && target.Width > 0 && target.Height > 0;

    private bool HasMeaningfulCompositeContribution(TemplateFrameProjection target)
    {
        if (target.HasCompositeContribution is null)
        {
            // Older inspection workers cannot provide this evidence. Keep the
            // candidate visible, but existing registration gates remain fail-closed.
            return true;
        }
        if (target.HasCompositeContribution != true || target.PreviewBounds is not { } bounds
            || CanvasWidth <= 0 || CanvasHeight <= 0
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return false;
        }

        // Reject remnants in absolute pixels, not as a percentage of a long PSD:
        // a real small detail photograph must not disappear when the canvas grows.
        var visibleArea = Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
        return Math.Min(bounds.Width, bounds.Height) >= 4 && visibleArea >= 64;
    }
}

public sealed class TemplateRuleFrameViewModel : INotifyPropertyChanged
{
    private string _description;
    private string _saved;
    private readonly Action _changed;
    public TemplateRuleFrameViewModel(TemplateFrameProjection target, string frameId, string label, string description, Action changed,
        bool isCandidate = false, bool isExcluded = false)
    { Target = target; FrameId = frameId; Label = label; IsCandidate = isCandidate; IsExcluded = isExcluded; _description = _saved = description; _changed = changed; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public TemplateFrameProjection Target { get; private set; }
    public string FrameId { get; private set; }
    public string Label { get; private set; }
    public bool IsCandidate { get; private set; }
    public bool IsExcluded { get; private set; }
    public void UpdateTarget(TemplateFrameProjection target, string frameId, string label, bool isCandidate, bool isExcluded = false)
    {
        Target = target; FrameId = frameId; Label = label; IsCandidate = isCandidate; IsExcluded = isExcluded;
        foreach (var name in new[] { nameof(Target), nameof(FrameId), nameof(Label), nameof(IsCandidate), nameof(IsExcluded) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public string Description
    {
        get => _description;
        set { if (_description == value) return; _description = value; PropertyChanged?.Invoke(this, new(nameof(Description))); _changed(); }
    }
    public bool IsDirty => _description.Trim() != _saved;
    public void AcceptSaved(string saved) => _saved = saved;
    public void Reset(string text) { _description = _saved = text; PropertyChanged?.Invoke(this, new(nameof(Description))); }
}
