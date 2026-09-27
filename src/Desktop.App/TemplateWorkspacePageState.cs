using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Desktop.App;

public enum TemplateWorkspaceNarrowPanel
{
    Pages,
    Canvas,
    Inspector
}

/// <summary>
/// Presentation-only state for the template canvas.
///
/// Template import, calibration persistence, reference learning and dialog
/// decisions remain coordinated by the shell. This object owns only the
/// transient viewport, responsive-layout and modal presentation values used by
/// the page.
/// </summary>
public sealed class TemplateWorkspacePageState : INotifyPropertyChanged
{
    private bool _isCalibrationNarrowLayout;
    private TemplateWorkspaceNarrowPanel _calibrationNarrowPanel = TemplateWorkspaceNarrowPanel.Canvas;
    private double _calibrationViewportZoom = 1;
    private double _templateBoardZoom = 0.72;
    private double _templateBoardPanX = 24;
    private double _templateBoardPanY = 24;
    private bool _isTemplateDialogVisible;
    private string _templateDialogTitle = "";
    private string _templateDialogBody = "";
    private string _templateDialogSafetyNote = "";
    private string _templateDialogPrimaryText = "确定";
    private string _templateDialogSecondaryText = "取消";
    private string _templateDialogTertiaryText = "";
    private bool _templateDialogHasSecondaryAction = true;
    private bool _templateDialogHasTertiaryAction;
    private bool _templateDialogIsDestructive;
    private bool _templateDialogHasTextInput;
    private string _templateDialogInputText = "";
    private bool _isDuplicateReviewVisible;
    private TemplatePageRowViewModel? _selectedTemplatePage;
    private TemplateLibraryCardViewModel? _selectedTemplate;
    private string _templateGroupName = "新模板组";
    private string _templateStatusMessage = "";
    private bool _templateBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCalibrationNarrowLayout => _isCalibrationNarrowLayout;

    public bool IsCalibrationPagePanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == TemplateWorkspaceNarrowPanel.Pages;

    public bool IsCalibrationCanvasPanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == TemplateWorkspaceNarrowPanel.Canvas;

    public bool IsCalibrationInspectorPanelVisible => !_isCalibrationNarrowLayout
        || _calibrationNarrowPanel == TemplateWorkspaceNarrowPanel.Inspector;

    public GridLength CalibrationPageColumnWidth => IsCalibrationPagePanelVisible
        ? _isCalibrationNarrowLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(224)
        : new GridLength(0);

    public GridLength CalibrationCanvasColumnWidth => IsCalibrationCanvasPanelVisible
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(0);

    public GridLength CalibrationInspectorColumnWidth => IsCalibrationInspectorPanelVisible
        ? _isCalibrationNarrowLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(260)
        : new GridLength(0);

    public GridLength CalibrationPanelGapWidth => _isCalibrationNarrowLayout
        ? new GridLength(0)
        : new GridLength(16);

    public double CalibrationViewportZoom => _calibrationViewportZoom;

    public string CalibrationZoomText => $"缩放 {Math.Round(CalibrationViewportZoom * 100):0}%";

    public double TemplateBoardPanX => _templateBoardPanX;

    public double TemplateBoardPanY => _templateBoardPanY;

    public double TemplateBoardZoom => _templateBoardZoom;

    public string TemplateBoardZoomText => $"画布 {Math.Round(TemplateBoardZoom * 100):0}%";

    public bool IsTemplateDialogVisible
    {
        get => _isTemplateDialogVisible;
        set => Set(ref _isTemplateDialogVisible, value);
    }

    public string TemplateDialogTitle
    {
        get => _templateDialogTitle;
        set => Set(ref _templateDialogTitle, value);
    }

    public string TemplateDialogBody
    {
        get => _templateDialogBody;
        set => Set(ref _templateDialogBody, value);
    }

    public string TemplateDialogSafetyNote
    {
        get => _templateDialogSafetyNote;
        set => Set(ref _templateDialogSafetyNote, value);
    }

    public string TemplateDialogPrimaryText
    {
        get => _templateDialogPrimaryText;
        set => Set(ref _templateDialogPrimaryText, value);
    }

    public string TemplateDialogSecondaryText
    {
        get => _templateDialogSecondaryText;
        set => Set(ref _templateDialogSecondaryText, value);
    }

    public string TemplateDialogTertiaryText
    {
        get => _templateDialogTertiaryText;
        set => Set(ref _templateDialogTertiaryText, value);
    }

    public bool TemplateDialogHasSecondaryAction
    {
        get => _templateDialogHasSecondaryAction;
        set => Set(ref _templateDialogHasSecondaryAction, value);
    }

    public bool TemplateDialogHasTertiaryAction
    {
        get => _templateDialogHasTertiaryAction;
        set => Set(ref _templateDialogHasTertiaryAction, value);
    }

    public bool TemplateDialogIsDestructive
    {
        get => _templateDialogIsDestructive;
        set => Set(ref _templateDialogIsDestructive, value);
    }

    public bool TemplateDialogHasTextInput
    {
        get => _templateDialogHasTextInput;
        set => Set(ref _templateDialogHasTextInput, value);
    }

    public string TemplateDialogInputText
    {
        get => _templateDialogInputText;
        set => Set(ref _templateDialogInputText, value);
    }

    public bool IsDuplicateReviewVisible
    {
        get => _isDuplicateReviewVisible;
        set => Set(ref _isDuplicateReviewVisible, value);
    }

    public TemplatePageRowViewModel? SelectedTemplatePage
    {
        get => _selectedTemplatePage;
        set => Set(ref _selectedTemplatePage, value);
    }

    public TemplateLibraryCardViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set => Set(ref _selectedTemplate, value);
    }

    public string TemplateGroupName
    {
        get => _templateGroupName;
        set => Set(ref _templateGroupName, value);
    }

    public string TemplateStatusMessage
    {
        get => _templateStatusMessage;
        set
        {
            if (Set(ref _templateStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasTemplateStatusMessage));
            }
        }
    }

    public bool HasTemplateStatusMessage => !string.IsNullOrWhiteSpace(TemplateStatusMessage);

    public bool TemplateBusy
    {
        get => _templateBusy;
        set => Set(ref _templateBusy, value);
    }

    public bool SetWindowWidth(double width)
    {
        var narrow = width <= 1180;
        if (_isCalibrationNarrowLayout == narrow)
        {
            return false;
        }

        _isCalibrationNarrowLayout = narrow;
        NotifyCalibrationLayoutChanged();
        return true;
    }

    public bool SetNarrowPanel(TemplateWorkspaceNarrowPanel panel)
    {
        if (_calibrationNarrowPanel == panel)
        {
            return false;
        }

        _calibrationNarrowPanel = panel;
        NotifyCalibrationLayoutChanged();
        return true;
    }

    public bool SetCalibrationViewportZoom(double zoom)
    {
        var normalized = Math.Clamp(zoom, 0.02, 4);
        if (_calibrationViewportZoom.Equals(normalized))
        {
            return false;
        }

        _calibrationViewportZoom = normalized;
        OnPropertyChanged(nameof(CalibrationViewportZoom));
        OnPropertyChanged(nameof(CalibrationZoomText));
        return true;
    }

    public void AdjustCalibrationViewportZoom(int wheelDelta)
    {
        if (wheelDelta != 0)
        {
            SetCalibrationViewportZoom(CalibrationViewportZoom * (wheelDelta > 0 ? 1.1 : 0.9));
        }
    }

    public bool SetTemplateBoardZoom(double zoom)
    {
        var normalized = Math.Clamp(zoom, 0.2, 2.5);
        if (_templateBoardZoom.Equals(normalized))
        {
            return false;
        }

        _templateBoardZoom = normalized;
        OnPropertyChanged(nameof(TemplateBoardZoom));
        OnPropertyChanged(nameof(TemplateBoardZoomText));
        return true;
    }

    public void AdjustTemplateBoardZoom(int wheelDelta)
    {
        if (wheelDelta != 0)
        {
            SetTemplateBoardZoom(TemplateBoardZoom * (wheelDelta > 0 ? 1.1 : 0.9));
        }
    }

    public void PanTemplateBoard(double deltaX, double deltaY)
    {
        if (deltaX != 0)
        {
            _templateBoardPanX += deltaX;
            OnPropertyChanged(nameof(TemplateBoardPanX));
        }

        if (deltaY != 0)
        {
            _templateBoardPanY += deltaY;
            OnPropertyChanged(nameof(TemplateBoardPanY));
        }
    }

    public void ResetTemplateBoardView()
    {
        SetTemplateBoardZoom(0.72);
        PanTo(24, 24);
    }

    private void PanTo(double x, double y)
    {
        if (!_templateBoardPanX.Equals(x))
        {
            _templateBoardPanX = x;
            OnPropertyChanged(nameof(TemplateBoardPanX));
        }

        if (!_templateBoardPanY.Equals(y))
        {
            _templateBoardPanY = y;
            OnPropertyChanged(nameof(TemplateBoardPanY));
        }
    }

    private void NotifyCalibrationLayoutChanged()
    {
        OnPropertyChanged(nameof(IsCalibrationNarrowLayout));
        OnPropertyChanged(nameof(IsCalibrationPagePanelVisible));
        OnPropertyChanged(nameof(IsCalibrationCanvasPanelVisible));
        OnPropertyChanged(nameof(IsCalibrationInspectorPanelVisible));
        OnPropertyChanged(nameof(CalibrationPageColumnWidth));
        OnPropertyChanged(nameof(CalibrationCanvasColumnWidth));
        OnPropertyChanged(nameof(CalibrationInspectorColumnWidth));
        OnPropertyChanged(nameof(CalibrationPanelGapWidth));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
