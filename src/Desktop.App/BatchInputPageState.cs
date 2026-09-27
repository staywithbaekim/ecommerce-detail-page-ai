using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Desktop.App;

/// <summary>
/// Presentation state owned by the batch input page.
///
/// The state contains only the values shown or edited by the page: template
/// and page selection, directories, status text and the page-selection dialog.
/// The batch coordinator owns directory scanning and draft persistence;
/// the shell supplies cross-page consent and AI handoff.
/// </summary>
public sealed class BatchInputPageState : INotifyPropertyChanged
{
    private readonly Dictionary<string, bool> _pageSelectionBackup = new(StringComparer.Ordinal);
    private TemplateLibraryCardViewModel? _selectedTemplate;
    private string _inputDirectory = string.Empty;
    private string _outputDirectory = string.Empty;
    private BatchOutputMode? _outputMode = BatchOutputMode.UnifiedDirectory;
    private string _recognitionText = "请选择直接包含商品图片的目录，或包含款式子文件夹的批次目录。";
    private string _outputConfirmationText = "请选择本批次的正式输出目录。";
    private string _statusMessage = "请确认模板、商品目录、输出目录和至少一个页面。";
    private BatchInputUiStatus _status = BatchInputUiStatus.Draft;
    private bool _automaticSelection = true;
    private bool _includeEditablePsd;
    private bool _pageSelectionVisible;
    private bool _busy;
    private string? _currentBatchId;

    public BatchInputPageState()
    {
        BatchPages.CollectionChanged += BatchPages_CollectionChanged;
    }

    public ObservableCollection<BatchPageOptionViewModel> BatchPages { get; } = [];

    public TemplateLibraryCardViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (ReferenceEquals(_selectedTemplate, value))
            {
                return;
            }

            _selectedTemplate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatchTemplateHelperText));
            OnPropertyChanged(nameof(CanScanBatch));
        }
    }

    public string InputDirectory
    {
        get => _inputDirectory;
        set => SetField(ref _inputDirectory, value ?? string.Empty, nameof(InputDirectory),
            nameof(BatchInputDirectoryDisplay), nameof(CanScanBatch));
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value ?? string.Empty, nameof(OutputDirectory),
            nameof(BatchOutputDirectoryDisplay), nameof(CanScanBatch));
    }

    public BatchOutputMode? OutputMode
    {
        get => _outputMode;
        set => SetField(ref _outputMode, value, nameof(OutputMode), nameof(IsStyleSourceOutput), nameof(IsUnifiedOutput),
            nameof(CanChooseOutputDirectory), nameof(BatchOutputDirectoryDisplay), nameof(CanScanBatch));
    }
    public bool IsStyleSourceOutput => OutputMode == BatchOutputMode.StyleSourceDirectories;
    public bool IsUnifiedOutput => !IsStyleSourceOutput;
    public bool CanChooseOutputDirectory => IsBatchConfigurationEditable && !IsStyleSourceOutput;

    public string BatchInputDirectoryDisplay => string.IsNullOrWhiteSpace(InputDirectory)
        ? "尚未选择商品目录"
        : $"已选择：{DirectoryDisplayName(InputDirectory)}";

    public string BatchOutputDirectoryDisplay => IsStyleSourceOutput ? "各款实际源目录 / 生成详情页"
        : string.IsNullOrWhiteSpace(OutputDirectory)
        ? "尚未选择正式输出目录"
        : $"已选择：{DirectoryDisplayName(OutputDirectory)}";

    public string RecognitionText
    {
        get => _recognitionText;
        set => SetField(ref _recognitionText, value ?? string.Empty, nameof(RecognitionText));
    }

    public string OutputConfirmationText
    {
        get => _outputConfirmationText;
        set => SetField(ref _outputConfirmationText, value ?? string.Empty, nameof(OutputConfirmationText));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value, nameof(StatusMessage));
    }

    public BatchInputUiStatus Status => _status;

    public bool IsBatchScanning => _status == BatchInputUiStatus.Scanning;

    public bool IsBatchScanReady => _status == BatchInputUiStatus.ScanReady;

    public bool IsBatchScanBlocked => _status == BatchInputUiStatus.ScanBlocked;

    public bool AutomaticSelection
    {
        get => _automaticSelection;
        set
        {
            if (!SetField(ref _automaticSelection, value, nameof(AutomaticSelection)))
            {
                return;
            }

            OnPropertyChanged(nameof(ManualSelection));
        }
    }

    public bool ManualSelection
    {
        get => !AutomaticSelection;
        set
        {
            if (value)
            {
                AutomaticSelection = false;
            }
        }
    }

    public bool IncludeEditablePsd
    {
        get => _includeEditablePsd;
        set => SetField(ref _includeEditablePsd, value, nameof(IncludeEditablePsd),
            nameof(BatchOutputPreviewSummary));
    }

    public bool IsPageSelectionVisible
    {
        get => _pageSelectionVisible;
        private set => SetField(ref _pageSelectionVisible, value, nameof(IsPageSelectionVisible));
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!SetField(ref _busy, value, nameof(Busy)))
            {
                return;
            }

            OnPropertyChanged(nameof(HasBatchRetry));
            OnPropertyChanged(nameof(IsBatchConfigurationEditable));
            OnPropertyChanged(nameof(CanChooseOutputDirectory));
            OnPropertyChanged(nameof(CanScanBatch));
        }
    }

    public string? CurrentBatchId
    {
        get => _currentBatchId;
        set => SetField(ref _currentBatchId, value, nameof(CurrentBatchId), nameof(BatchDraftSaveText));
    }

    public string BatchTemplateHelperText => SelectedTemplate is null
        ? "请先选择一个已校准模板组。"
        : $"已校准 · {BatchPages.Count} 个可用页面";

    public string BatchStatusState => Status.ToString();

    public bool HasBatchRetry => IsBatchScanBlocked && !Busy;

    public string BatchPrimaryActionText => IsBatchScanReady ? "扫描完成" : "扫描并分析";

    public bool IsBatchConfigurationEditable => !Busy && !IsBatchScanReady;

    public bool CanScanBatch => IsBatchConfigurationEditable
        && SelectedTemplate is not null
        && !string.IsNullOrWhiteSpace(InputDirectory)
        && (IsStyleSourceOutput || !string.IsNullOrWhiteSpace(OutputDirectory))
        && SelectedBatchPageCount > 0;

    public int SelectedBatchPageCount => BatchPages.Count(page => page.IsSelected);

    public bool? AreAllBatchPagesSelected
    {
        get
        {
            if (BatchPages.Count == 0 || SelectedBatchPageCount == 0)
            {
                return false;
            }

            return SelectedBatchPageCount == BatchPages.Count ? true : null;
        }
        set
        {
            if (!value.HasValue)
            {
                return;
            }

            foreach (var page in BatchPages)
            {
                page.IsSelected = value.Value;
            }

            NotifyPageSelectionChanged();
        }
    }

    public string BatchPageSelectionSummary => $"已选择 {SelectedBatchPageCount}/{BatchPages.Count} 个页面";

    public IReadOnlyList<string> BatchOutputPreviewFiles => BatchPages
        .Where(page => page.IsSelected)
        .Select((page, index) => $"{index + 1:D2}-{page.Name}.jpg")
        .ToArray();

    public string BatchOutputPreviewSummary => IncludeEditablePsd
        ? $"每款独立目录 · {SelectedBatchPageCount} 个 JPG + 同名可编辑 PSD"
        : $"每款独立目录 · {SelectedBatchPageCount} 个 JPG";

    public string BatchDraftSaveText => CurrentBatchId is null ? "新批次草稿" : "草稿已自动保存";

    public void SetStatus(BatchInputUiStatus status, string message)
    {
        if (_status != status)
        {
            _status = status;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(BatchStatusState));
            OnPropertyChanged(nameof(IsBatchScanning));
            OnPropertyChanged(nameof(IsBatchScanReady));
            OnPropertyChanged(nameof(IsBatchScanBlocked));
            OnPropertyChanged(nameof(HasBatchRetry));
            OnPropertyChanged(nameof(BatchPrimaryActionText));
            OnPropertyChanged(nameof(IsBatchConfigurationEditable));
            OnPropertyChanged(nameof(CanChooseOutputDirectory));
            OnPropertyChanged(nameof(CanScanBatch));
        }

        StatusMessage = message;
    }

    public void ResetForNewInput()
    {
        OutputMode = BatchOutputMode.UnifiedDirectory;
        InputDirectory = string.Empty;
        OutputDirectory = string.Empty;
        RecognitionText = "请选择直接包含商品图片的目录，或包含款式子文件夹的批次目录。";
        OutputConfirmationText = "请选择本批次的正式输出目录。";
        AutomaticSelection = true;
        IncludeEditablePsd = false;
        OnPropertyChanged(nameof(BatchDraftSaveText));
        SetStatus(BatchInputUiStatus.Draft, "请确认模板、商品目录、输出目录和至少一个页面。");
    }

    public void ApplyProjection(BatchInputProjection projection)
    {
        CurrentBatchId = projection.BatchId;
        InputDirectory = projection.InputDirectory;
        OutputDirectory = projection.OutputDirectory;
        OutputMode = projection.OutputMode;
        AutomaticSelection = projection.SelectionMode == BatchSelectionMode.Automatic;
        IncludeEditablePsd = projection.IncludeEditablePsd;
        RecognitionText = projection.RecognitionText;
        OutputConfirmationText = IsStyleSourceOutput
            ? "按扫描确认的每款实际目录写入“生成详情页”，不猜父款号、不覆盖已有文件。"
            : string.IsNullOrWhiteSpace(projection.OutputDirectory)
            ? "请选择本批次的正式输出目录。"
            : "已确认为本次正式输出目录；软件不会清理目录内其他文件。";

        if (projection.SelectedPageIds.Count > 0)
        {
            var selected = projection.SelectedPageIds.ToHashSet(StringComparer.Ordinal);
            foreach (var page in BatchPages)
            {
                page.IsSelected = selected.Contains(page.PageId);
            }
        }

        SetStatus(projection.Status, projection.StatusMessage);
    }

    public void OpenPageSelection()
    {
        _pageSelectionBackup.Clear();
        foreach (var page in BatchPages)
        {
            _pageSelectionBackup[page.PageId] = page.IsSelected;
        }

        IsPageSelectionVisible = true;
    }

    public void ApplyPageSelection()
    {
        IsPageSelectionVisible = false;
        _pageSelectionBackup.Clear();
    }

    public void CancelPageSelection()
    {
        foreach (var page in BatchPages)
        {
            if (_pageSelectionBackup.TryGetValue(page.PageId, out var selected))
            {
                page.IsSelected = selected;
            }
        }

        IsPageSelectionVisible = false;
        _pageSelectionBackup.Clear();
    }

    public void ClosePageSelection()
    {
        IsPageSelectionVisible = false;
        _pageSelectionBackup.Clear();
    }

    public void NotifyPageSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBatchPageCount));
        OnPropertyChanged(nameof(BatchPageSelectionSummary));
        OnPropertyChanged(nameof(AreAllBatchPagesSelected));
        OnPropertyChanged(nameof(CanScanBatch));
        OnPropertyChanged(nameof(BatchTemplateHelperText));
        OnPropertyChanged(nameof(BatchOutputPreviewFiles));
        OnPropertyChanged(nameof(BatchOutputPreviewSummary));
    }

    private void BatchPages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (BatchPageOptionViewModel page in e.NewItems)
            {
                page.PropertyChanged += BatchPage_PropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (BatchPageOptionViewModel page in e.OldItems)
            {
                page.PropertyChanged -= BatchPage_PropertyChanged;
            }
        }

        NotifyPageSelectionChanged();
    }

    private void BatchPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchPageOptionViewModel.IsSelected))
        {
            NotifyPageSelectionChanged();
        }
    }

    private bool SetField<T>(ref T field, T value, string propertyName, params string[] dependentProperties)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        foreach (var dependentProperty in dependentProperties)
        {
            OnPropertyChanged(dependentProperty);
        }

        return true;
    }

    private static string DirectoryDisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "本地目录" : name;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
