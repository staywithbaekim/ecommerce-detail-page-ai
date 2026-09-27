using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Desktop.Application.Ai;

namespace Desktop.App;

/// <summary>
/// Mutable state owned by the AI analysis page.
///
/// The shell still exposes the existing binding surface for compatibility, but
/// the page's collections, selections, consent dialog state and refresh timer
/// live here instead of being stored on <see cref="MainWindowViewModel"/>.
/// Commands are stored with the page state as well; their handlers and workflow calls belong to the page coordinator.
/// </summary>
internal sealed class AiAnalysisPageState
{
    public AiBatchAnalysisProjection? BatchProjection { get; set; }
    public AiUploadConsentRecord? UploadConsent { get; set; }
    public AiConsentContinuation PendingConsentContinuation { get; set; }
    public bool Busy { get; set; }
    public bool IsUploadConsentDialogVisible { get; set; }
    public bool UploadConsentAcknowledged { get; set; }
    public bool IsCandidatePreviewVisible { get; set; }
    public AiCandidateViewModel? CandidatePreview { get; set; }
    public AiRequirementUsageViewModel? ReplacementTarget { get; set; }
    public AiStyleAssetViewModel? SelectedReplacementAsset { get; set; }
    public AiStyleAnalysisViewModel? SelectedStyle { get; set; }
    public string StatusMessage { get; set; } = "等待分析";
    public AiStyleFilter SelectedFilter { get; set; } = AiStyleFilter.All;
    public HashSet<string> ExpandedStyleIds { get; } = new(StringComparer.Ordinal);
    public DispatcherTimer? RefreshTimer { get; set; }

    public ObservableCollection<AiStyleAnalysisViewModel> Styles { get; } = [];
    public ObservableCollection<AiStyleAnalysisViewModel> VisibleStyles { get; } = [];
    public ObservableCollection<AiStatusFilterViewModel> StatusFilters { get; } = [];

    public ICommand PauseCommand { get; set; } = null!;
    public ICommand ResumeCommand { get; set; } = null!;
    public ICommand CancelCommand { get; set; } = null!;
    public ICommand WaitForNetworkCommand { get; set; } = null!;
    public ICommand RefreshCommand { get; set; } = null!;
    public ICommand RetryCommand { get; set; } = null!;
    public ICommand LockSelectionCommand { get; set; } = null!;
    public ICommand AnalyzeAssetCommand { get; set; } = null!;
    public ICommand SelectSizeTableSourceCommand { get; set; } = null!;
    public ICommand SelectTopNCandidateCommand { get; set; } = null!;
    public ICommand SelectStatusFilterCommand { get; set; } = null!;
    public ICommand GrantUploadConsentCommand { get; set; } = null!;
    public ICommand RevokeUploadConsentCommand { get; set; } = null!;
    public ICommand RefreshConsentCommand { get; set; } = null!;
    public ICommand ConfirmUploadConsentCommand { get; set; } = null!;
    public ICommand CancelUploadConsentCommand { get; set; } = null!;
    public ICommand OpenCandidatePreviewCommand { get; set; } = null!;
    public ICommand CloseCandidatePreviewCommand { get; set; } = null!;
    public ICommand ConfirmCandidateSelectionCommand { get; set; } = null!;
    public ICommand AnalyzePreviewAssetCommand { get; set; } = null!;
    public ICommand OpenRequirementPreviewCommand { get; set; } = null!;
    public ICommand OpenSizeTableSourcePreviewCommand { get; set; } = null!;
    public ICommand BeginReplacementCommand { get; set; } = null!;
    public ICommand SelectReplacementAssetCommand { get; set; } = null!;
    public ICommand ConfirmReplacementCommand { get; set; } = null!;
    public ICommand CancelReplacementCommand { get; set; } = null!;
    public ICommand OpenStyleReviewCommand { get; set; } = null!;
    public ICommand ReturnFromStyleReviewCommand { get; set; } = null!;
}

internal enum AiConsentContinuation
{
    None = 0,
    BatchScan,
    ReferenceLearning
}
