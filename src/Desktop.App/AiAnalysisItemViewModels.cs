using System.Collections.ObjectModel;
using System.ComponentModel;
using Desktop.Application.Ai;
using Desktop.Application.Templates;

namespace Desktop.App;

public sealed class AiStyleAnalysisViewModel
{
    private readonly AiAnalysisViewModel _owner;
    private readonly AiStyleAnalysisProjection _projection;
    private bool _isExpanded;
    private bool _isReplacementActive;
    private string _replacementInstructionText = "点击左侧“替换用图”，再选择一张照片。";

    public AiStyleAnalysisViewModel(AiStyleAnalysisProjection projection, AiAnalysisViewModel owner)
    {
        _projection = projection;
        _owner = owner;
        _isExpanded = owner.IsAiStyleExpanded(projection.StyleId);
        Pages = new ObservableCollection<AiPageAnalysisViewModel>(projection.Pages.Select(page =>
            new AiPageAnalysisViewModel(page)));
        AllRequirements = new ObservableCollection<AiRequirementUsageViewModel>(
            Pages.SelectMany(page => page.Requirements));
        AllUsageCards = new ObservableCollection<AiRequirementUsageViewModel>(
            Pages.SelectMany(page => page.SizeTableUsage is { } sizeTableUsage
                ? new[] { sizeTableUsage }.Concat(page.Requirements)
                : page.Requirements));
        Assets = new ObservableCollection<AiStyleAssetViewModel>((projection.Assets ?? [])
            .Select(asset => new AiStyleAssetViewModel(asset)));
        var selectedSizeTableAssetId = SizeTableSource?.SelectedAssetId;
        foreach (var asset in Assets)
        {
            asset.SetSizeTableSourceSelected(
                selectedSizeTableAssetId is not null
                && string.Equals(asset.AssetId, selectedSizeTableAssetId, StringComparison.Ordinal));
        }
        var useCounts = Pages
            .SelectMany(page => page.Requirements)
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.SelectedAssetId))
            .GroupBy(requirement => requirement.SelectedAssetId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            asset.SetUsageCount(useCounts.GetValueOrDefault(asset.AssetId));
        }
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string StyleName => _projection.StyleName;
    public string StatusText => _projection.StatusText;
    public string StatusState => _projection.StatusState;
    public AiStyleUiStatus Status => _projection.Status;
    public bool CanGenerate => _projection.CanGenerate;
    public int AssetCount => _projection.AssetCount;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            _owner.SetAiStyleExpanded(StyleId, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public int AnalyzedPageCount => Pages.Count(page => page.Status is
        AiPageUiStatus.Ready
            or AiPageUiStatus.ReviewRequired
            or AiPageUiStatus.BusinessError
            or AiPageUiStatus.SystemError
            or AiPageUiStatus.Cancelled);
    public int ProcessedPageCount => AnalyzedPageCount;
    public string MetaText => $"{AssetCount} 张素材 · 已处理 {ProcessedPageCount}/{Pages.Count} 页";
    public ObservableCollection<AiPageAnalysisViewModel> Pages { get; }
    public ObservableCollection<AiRequirementUsageViewModel> AllRequirements { get; }
    public ObservableCollection<AiRequirementUsageViewModel> AllUsageCards { get; }
    public ObservableCollection<AiStyleAssetViewModel> Assets { get; }
    public AiSizeTableSourceViewModel? SizeTableSource => Pages
        .Select(page => page.SizeTableSource)
        .FirstOrDefault(source => source is not null);
    public bool HasSizeTableSource => SizeTableSource is not null;
    public int RequirementCount => Pages.Sum(page => page.Requirements.Count);
    public string UsageSummaryText => $"{Pages.Count} 个页面 · {RequirementCount} 个图片框 · 向下滚动查看全部";
    public string AssetSummaryText => $"本款已登记 {Assets.Count} 张照片 · 不显示文件名";
    public bool IsReplacementActive
    {
        get => _isReplacementActive;
        private set
        {
            if (_isReplacementActive == value)
            {
                return;
            }

            _isReplacementActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacementActive)));
        }
    }
    public string ReplacementInstructionText
    {
        get => _replacementInstructionText;
        private set
        {
            if (_replacementInstructionText == value)
            {
                return;
            }

            _replacementInstructionText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReplacementInstructionText)));
        }
    }

    internal void SetReplacementActive(bool active, AiRequirementUsageViewModel? requirement)
    {
        IsReplacementActive = active;
        ReplacementInstructionText = active && requirement is not null
            ? $"正在替换：{requirement.SelectionLabel}"
            : "点击左侧“替换用图”，再选择一张照片。";
        foreach (var page in Pages)
        {
            foreach (var item in page.Requirements)
            {
                item.IsReplacementTarget = active && ReferenceEquals(item, requirement);
            }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiPageAnalysisViewModel
{
    private readonly AiPageAnalysisProjection _projection;
    private readonly AiSizeTableSourceViewModel? _sizeTableSource;

    public AiPageAnalysisViewModel(AiPageAnalysisProjection projection)
    {
        _projection = projection;
        Candidates = new ObservableCollection<AiCandidateViewModel>(projection.Candidates.Select(candidate =>
            new AiCandidateViewModel(candidate)));
        var requirements = projection.Requirements ?? projection.Candidates
            .GroupBy(candidate => candidate.RequirementId, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = group.FirstOrDefault(candidate => candidate.IsLocked)
                    ?? group.OrderBy(candidate => candidate.Rank).First();
                return new AiRequirementSelectionProjection(
                    selected.BatchId,
                    selected.StyleId,
                    projection.PageId,
                    projection.PageName,
                    selected.RequirementId,
                    AiSelectionLabelFormatter.FormatFrameDisplayName(
                        selected.FrameDisplayName,
                        selected.SelectionRequirement),
                    selected.SelectionRequirement,
                    selected.IsLocked
                        ? AiRequirementSelectionUiStatus.ManualLocked
                        : AiRequirementSelectionUiStatus.AiSelected,
                    selected.IsLocked ? "人工选用 · 已锁定" : "AI 推荐 · 已确认",
                    selected.AssetId,
                    selected.PreviewPath);
            })
            .ToArray();
        Requirements = new ObservableCollection<AiRequirementUsageViewModel>(requirements.Select(requirement =>
            new AiRequirementUsageViewModel(requirement)));
        _sizeTableSource = projection.SizeTableSource is null
            ? null
            : new AiSizeTableSourceViewModel(projection.SizeTableSource);
        SizeTableUsage = _sizeTableSource is null
            ? null
            : AiRequirementUsageViewModel.CreateSizeTableSource(PageName, _sizeTableSource);
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string PageId => _projection.PageId;
    public string PageName => _projection.PageName;
    public string PageContextText => $"PSD 页面：{PageName}";
    public string StatusText => _projection.StatusText;
    public string StatusState => _projection.StatusState;
    public AiPageUiStatus Status => _projection.Status;
    public string ActionText => _projection.ActionText;
    public bool RequiresManualConfiguration => _projection.RequiresManualConfiguration;
    public bool HasWarnings => _projection.HasWarnings;
    public IReadOnlyList<string> WarningTexts => _projection.WarningTexts;
    public bool IsSizeTablePage => _projection.SizeTableSource is not null;
    public AiSizeTableSourceViewModel? SizeTableSource => _sizeTableSource;
    public AiRequirementUsageViewModel? SizeTableUsage { get; }
    public ObservableCollection<AiCandidateViewModel> Candidates { get; }
    public ObservableCollection<AiRequirementUsageViewModel> Requirements { get; }
}

public sealed class AiSizeTableSourceViewModel
{
    private readonly AiSizeTableSourceProjection _projection;

    public AiSizeTableSourceViewModel(AiSizeTableSourceProjection projection)
    {
        _projection = projection;
        Candidates = new ObservableCollection<AiSizeTableSourceCandidateViewModel>(
            projection.Candidates.Select(candidate => new AiSizeTableSourceCandidateViewModel(candidate)));
    }

    public AiSizeTableSourceStatus Status => _projection.Status;
    public string StatusText => _projection.StatusText;
    public string? SelectedAssetId => _projection.SelectedAssetId;
    public string SelectedPreviewPath => _projection.SelectedPreviewPath ?? "";
    public bool HasSelection => _projection.HasSelection;
    public string SelectedDisplayName => Candidates.FirstOrDefault(candidate => candidate.IsSelected)?.DisplayName ?? "尚未选择";
    public string SelectedSelectionText => HasSelection ? "当前尺码表来源" : "请选择照片";
    public ObservableCollection<AiSizeTableSourceCandidateViewModel> Candidates { get; }
}

public sealed class AiSizeTableSourceCandidateViewModel
{
    private readonly AiSizeTableSourceCandidateProjection _projection;

    public AiSizeTableSourceCandidateViewModel(AiSizeTableSourceCandidateProjection projection)
    {
        _projection = projection;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string AssetId => _projection.AssetId;
    public string DisplayName => _projection.DisplayName;
    public string PreviewPath => _projection.PreviewPath ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(_projection.PreviewPath);
    public bool IsDetected => _projection.IsDetected;
    public bool IsSelected => _projection.IsSelected;
    public decimal? Confidence => _projection.Confidence;
    public string ConfidenceText => _projection.ConfidenceText;
    public bool IsSizeTableCandidate => _projection.IsSizeTableCandidate;
    public string SelectionText => IsSelected ? "当前尺码图" : IsDetected ? "AI 候选" : "选择此图";
    public string ActionText => IsSelected ? "替换" : "替换";
    public string SelectionLabel => IsSelected ? "尺码图" : "尺码图";
    public string PageContextText => IsDetected ? "动态尺码表页" : "动态尺码表页";
    public string StatusState => IsSelected ? "AiSelected" : IsDetected ? "ReviewRequired" : "Missing";
    public string StatusText => IsSelected
        ? (IsDetected ? "AI 推荐 · 已选用" : "已选用")
        : IsDetected ? "AI 推荐" : "可选照片";
    public string PreviewText => HasPreview ? "点击查看" : "预览不可用";
    public string PreviewAutomationName => $"查看{DisplayName}尺码图";
    public string AutomationName => $"{DisplayName}，{SelectionText}";

    public AiCandidateViewModel CreatePreviewCandidate() => new(new AiCandidateProjection(
        BatchId,
        StyleId,
        $"size_table_source_{StyleId}",
        AssetId,
        DisplayName,
        1,
        Confidence ?? 1m,
        [],
        IsSelected,
        "尺码图",
        PreviewPath,
        "清晰可读的市场尺码表图片",
        "动态尺码表页"));
}

public sealed class AiRequirementUsageViewModel : INotifyPropertyChanged
{
    private readonly AiRequirementSelectionProjection _projection;
    private readonly AiSizeTableSourceViewModel? _sizeTableSource;
    private bool _isReplacementTarget;

    public AiRequirementUsageViewModel(AiRequirementSelectionProjection projection)
    {
        _projection = projection;
    }

    private AiRequirementUsageViewModel(string pageName, AiSizeTableSourceViewModel sizeTableSource)
    {
        _projection = new AiRequirementSelectionProjection(
            sizeTableSource.Candidates.FirstOrDefault()?.BatchId ?? string.Empty,
            sizeTableSource.Candidates.FirstOrDefault()?.StyleId ?? string.Empty,
            $"size_table_source_{sizeTableSource.SelectedAssetId ?? "pending"}",
            pageName,
            $"size_table_source_{sizeTableSource.SelectedAssetId ?? "pending"}",
            "尺码表来源",
            "清晰可读的市场尺码表图片",
            sizeTableSource.HasSelection
                ? AiRequirementSelectionUiStatus.AiSelected
                : AiRequirementSelectionUiStatus.Missing,
            sizeTableSource.HasSelection ? "尺码表来源 · 已选用" : "尺码表来源 · 待选择",
            sizeTableSource.SelectedAssetId,
            sizeTableSource.SelectedPreviewPath,
            sizeTableSource.SelectedDisplayName);
        _sizeTableSource = sizeTableSource;
    }

    internal static AiRequirementUsageViewModel CreateSizeTableSource(
        string pageName,
        AiSizeTableSourceViewModel sizeTableSource) =>
        new(pageName, sizeTableSource);

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string PageId => _projection.PageId;
    public string PageName => _projection.PageName;
    public string RequirementId => _projection.RequirementId;
    public string FrameDisplayName => AiSelectionLabelFormatter.FormatFrameDisplayName(
        _projection.FrameDisplayName,
        _projection.SelectionRequirement);
    public string SelectionRequirement => _projection.SelectionRequirement;
    public string SelectionLabel => _sizeTableSource is not null
        ? "尺码表来源"
        : AiSelectionLabelFormatter.Format(SelectionRequirement);
    public string PageContextText => $"PSD 页面：{PageName}";
    public string CurrentAssetDisplayName => _projection.SelectedAssetDisplayName ?? "未选图";
    public string TechnicalFrameContext => $"{PageName} · {FrameDisplayName}";
    public string StatusText => _sizeTableSource is not null
        ? _sizeTableSource.HasSelection ? "尺码表来源 · 已选用" : "尺码表来源 · 待选择"
        : _projection.StatusText;
    public string StatusState => _sizeTableSource is not null
        ? _sizeTableSource.HasSelection ? "AiSelected" : "Missing"
        : _projection.StatusState;
    public string? SelectedAssetId => _projection.SelectedAssetId;
    public string PreviewPath => _sizeTableSource?.SelectedPreviewPath ?? _projection.SelectedPreviewPath ?? "";
    public bool HasSelection => _sizeTableSource?.HasSelection ?? _projection.HasSelection;
    public bool IsManualLocked => _projection.IsManualLocked;
    public string PreviewAutomationName => HasSelection
        ? $"查看{PageName}的{SelectionLabel}当前用图"
        : $"{PageName}的{SelectionLabel}尚未选图";
    public bool IsSizeTableSource => _sizeTableSource is not null;
    public bool IsReplacementTarget
    {
        get => _isReplacementTarget;
        set
        {
            if (_isReplacementTarget == value)
            {
                return;
            }

            _isReplacementTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacementTarget)));
        }
    }

    public AiCandidateViewModel? CreatePreviewCandidate()
    {
        if (_sizeTableSource is not null)
        {
            return _sizeTableSource.Candidates.FirstOrDefault(candidate => candidate.IsSelected)
                ?.CreatePreviewCandidate();
        }

        if (!HasSelection || SelectedAssetId is null)
        {
            return null;
        }

        return new AiCandidateViewModel(new AiCandidateProjection(
            BatchId,
            StyleId,
            RequirementId,
            SelectedAssetId,
            "已登记素材",
            1,
            1m,
            [],
            _projection.IsManualLocked,
            FrameDisplayName,
            PreviewPath,
            SelectionRequirement,
            PageName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiStyleAssetViewModel : INotifyPropertyChanged
{
    private readonly AiStyleAssetProjection _projection;
    private bool _isSelected;
    private int _usageCount;
    private bool _isSelectedAsSizeTableSource;

    public AiStyleAssetViewModel(AiStyleAssetProjection projection)
    {
        _projection = projection;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string AssetId => _projection.AssetId;
    public int DisplayOrder => _projection.DisplayOrder;
    public string DisplayName => $"照片 {DisplayOrder:D2}";
    public string PreviewPath => _projection.PreviewPath ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(_projection.PreviewPath);
    public bool IsCurrentlyUsed => _usageCount > 0;
    public string UsageText => _usageCount switch
    {
        <= 0 => "可选照片",
        1 => "1 个图片框正在使用",
        _ => $"{_usageCount} 个图片框正在使用"
    };
    public string SelectionStateText => IsSelectedAsSizeTableSource
        ? "尺码表来源"
        : IsSelected ? "已选择" : IsCurrentlyUsed ? "当前使用" : "可选照片";
    public bool IsSelectedAsSizeTableSource
    {
        get => _isSelectedAsSizeTableSource;
        internal set
        {
            if (_isSelectedAsSizeTableSource == value) return;
            _isSelectedAsSizeTableSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedAsSizeTableSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        }
    }
    public string AutomationName => $"{DisplayName}，{SelectionStateText}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        }
    }

    internal void SetUsageCount(int count)
    {
        _usageCount = Math.Max(0, count);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentlyUsed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
    }

    internal void SetSizeTableSourceSelected(bool selected) => IsSelectedAsSizeTableSource = selected;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiCandidateViewModel : INotifyPropertyChanged
{
    private readonly AiCandidateProjection _projection;

    private bool _isTopNSelected;

    public AiCandidateViewModel(AiCandidateProjection projection)
    {
        _projection = projection;
        _isTopNSelected = projection.IsLocked || projection.Rank == 1;
    }

    public string BatchId => _projection.BatchId;
    public string StyleId => _projection.StyleId;
    public string RequirementId => _projection.RequirementId;
    public string AssetId => _projection.AssetId;
    public string AssetName => _projection.AssetName;
    public string RankText => _projection.RankText;
    public string ConfidenceText => _projection.ConfidenceText;
    public string EvidenceText => _projection.EvidenceText;
    public string FrameDisplayName => AiSelectionLabelFormatter.FormatFrameDisplayName(
        _projection.FrameDisplayName,
        _projection.SelectionRequirement);
    public string PageName => _projection.PageName;
    public string SelectionLabel => AiSelectionLabelFormatter.Format(SelectionRequirement);
    public string PageContextText => $"PSD 页面：{PageName}";
    public string PreviewPath => _projection.PreviewPath ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(_projection.PreviewPath);
    public string SelectionRequirement => _projection.SelectionRequirement;
    public string UsageText => $"用于：PSD 页面 {PageName} · {SelectionLabel}";
    public string RecommendationText => _projection.Rank == 1 ? "AI 推荐" : _projection.RankText;
    public string AnalysisHint => _projection.IsLocked
        ? "当前图片为已确认用图；如需更换，请关闭预览后点击“替换用图”。"
        : "当前图片为 AI 默认用图；如需更换，请关闭预览后点击“替换用图”。";
    public bool IsLocked => _projection.IsLocked;
    public bool IsLockEnabled => IsTopNSelected && !_projection.IsLocked;
    public bool IsTopNSelected
    {
        get => _isTopNSelected;
        set
        {
            if (_isTopNSelected == value)
            {
                return;
            }

            _isTopNSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTopNSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLockEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        }
    }
    public string SelectionText => IsTopNSelected ? "当前首选" : "选为首选";
    public string LockText => _projection.LockText;
    public string ChooseText => _projection.IsLocked ? "已确认" : "确认";

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class AiSelectionLabelFormatter
{
    private const string GenericFallback = "商品主体清晰完整，构图适合当前图片框。";

    public static string Format(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, GenericFallback, StringComparison.Ordinal)
            || string.Equals(normalized, ReferenceSelectionRequirementPolicy.UnconfirmedLabel, StringComparison.Ordinal)
            || normalized.Contains("请选择", StringComparison.Ordinal)
            || normalized.Contains("请为这个图片框", StringComparison.Ordinal))
        {
            return ReferenceSelectionRequirementPolicy.UnconfirmedLabel;
        }

        if (ReferenceSelectionRequirementPolicy.TryNormalize(normalized, out var compact))
        {
            return compact;
        }

        return normalized.Length <= ReferenceSelectionRequirementPolicy.MaxLength
            ? normalized
            : normalized[..(ReferenceSelectionRequirementPolicy.MaxLength - 1)] + "…";
    }

    public static string FormatFrameDisplayName(string? frameDisplayName, string? selectionRequirement)
    {
        var normalized = frameDisplayName?.Trim() ?? string.Empty;
        if (IsTechnicalFrameName(normalized))
        {
            return Format(selectionRequirement);
        }

        return Format(normalized);
    }

    private static bool IsTechnicalFrameName(string value) =>
        value.Equals("图片框", StringComparison.Ordinal)
        || value.StartsWith("图片框 ", StringComparison.Ordinal)
        || value.StartsWith("图片框_", StringComparison.Ordinal)
        || value.StartsWith("frame_", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("frame-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("frame ", StringComparison.OrdinalIgnoreCase);
}

public enum AiStyleFilter
{
    All = 0,
    Processing,
    Review,
    Ready,
    Blocked
}

public sealed class AiStatusFilterViewModel(
    AiStyleFilter filter,
    string label,
    int count,
    string tone,
    bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public AiStyleFilter Filter { get; } = filter;
    public string Label { get; } = label;
    public int Count { get; } = count;
    public string Tone { get; } = tone;
    public string DisplayText => Filter == AiStyleFilter.All ? Label : $"{Label} {Count}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
