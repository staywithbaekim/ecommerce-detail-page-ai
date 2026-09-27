using System.Windows;

namespace Desktop.App.UiTests;

public sealed class TemplateWorkspacePageStateTests
{
    [Fact]
    public void DefaultsKeepCanvasVisibleAndUseStableViewportValues()
    {
        var state = new TemplateWorkspacePageState();

        Assert.False(state.IsCalibrationNarrowLayout);
        Assert.True(state.IsCalibrationPagePanelVisible);
        Assert.True(state.IsCalibrationCanvasPanelVisible);
        Assert.True(state.IsCalibrationInspectorPanelVisible);
        Assert.Equal(1, state.CalibrationViewportZoom);
        Assert.Equal(0.72, state.TemplateBoardZoom);
        Assert.Equal(24, state.TemplateBoardPanX);
        Assert.Equal(24, state.TemplateBoardPanY);
        Assert.Equal("新模板组", state.TemplateGroupName);
        Assert.Equal(string.Empty, state.TemplateStatusMessage);
        Assert.False(state.HasTemplateStatusMessage);
        Assert.False(state.TemplateBusy);
        Assert.Null(state.SelectedTemplate);
        Assert.Null(state.SelectedTemplatePage);
    }

    [Fact]
    public void ResponsiveLayoutAndNarrowPanelStayInsidePageState()
    {
        var state = new TemplateWorkspacePageState();

        Assert.True(state.SetWindowWidth(1100));
        Assert.True(state.IsCalibrationNarrowLayout);
        Assert.True(state.SetNarrowPanel(TemplateWorkspaceNarrowPanel.Inspector));
        Assert.False(state.IsCalibrationPagePanelVisible);
        Assert.False(state.IsCalibrationCanvasPanelVisible);
        Assert.True(state.IsCalibrationInspectorPanelVisible);
        Assert.Equal(GridUnitType.Star, state.CalibrationInspectorColumnWidth.GridUnitType);
        Assert.False(state.SetNarrowPanel(TemplateWorkspaceNarrowPanel.Inspector));

        Assert.True(state.SetWindowWidth(1440));
        Assert.False(state.IsCalibrationNarrowLayout);
        Assert.True(state.IsCalibrationPagePanelVisible);
        Assert.True(state.IsCalibrationCanvasPanelVisible);
        Assert.True(state.IsCalibrationInspectorPanelVisible);
        Assert.Equal(16, state.CalibrationPanelGapWidth.Value);
    }

    [Fact]
    public void ZoomIsClampedAndBoardPanCanBeReset()
    {
        var state = new TemplateWorkspacePageState();

        Assert.True(state.SetCalibrationViewportZoom(99));
        Assert.Equal(4, state.CalibrationViewportZoom);
        Assert.True(state.SetCalibrationViewportZoom(-1));
        Assert.Equal(0.02, state.CalibrationViewportZoom);

        Assert.True(state.SetTemplateBoardZoom(99));
        Assert.Equal(2.5, state.TemplateBoardZoom);
        state.PanTemplateBoard(100, -20);
        Assert.Equal(124, state.TemplateBoardPanX);
        Assert.Equal(4, state.TemplateBoardPanY);

        state.ResetTemplateBoardView();
        Assert.Equal(0.72, state.TemplateBoardZoom);
        Assert.Equal(24, state.TemplateBoardPanX);
        Assert.Equal(24, state.TemplateBoardPanY);
    }

    [Fact]
    public void DialogAndDuplicateReviewStateRaisePageNotifications()
    {
        var state = new TemplateWorkspacePageState();
        var changed = new List<string?>();
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        state.TemplateDialogTitle = "确认模板";
        state.TemplateDialogInputText = "新名称";
        state.IsTemplateDialogVisible = true;
        state.IsDuplicateReviewVisible = true;
        state.TemplateStatusMessage = "正在保存";
        state.TemplateBusy = true;

        Assert.Equal("确认模板", state.TemplateDialogTitle);
        Assert.Equal("新名称", state.TemplateDialogInputText);
        Assert.True(state.IsTemplateDialogVisible);
        Assert.True(state.IsDuplicateReviewVisible);
        Assert.True(state.HasTemplateStatusMessage);
        Assert.True(state.TemplateBusy);
        Assert.Contains(nameof(TemplateWorkspacePageState.TemplateDialogTitle), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.TemplateDialogInputText), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.IsTemplateDialogVisible), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.IsDuplicateReviewVisible), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.TemplateStatusMessage), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.HasTemplateStatusMessage), changed);
        Assert.Contains(nameof(TemplateWorkspacePageState.TemplateBusy), changed);
    }
}
