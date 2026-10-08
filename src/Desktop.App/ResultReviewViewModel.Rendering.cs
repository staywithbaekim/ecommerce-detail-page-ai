using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Rendering;
using Desktop.Application.Ai;
using System.Text.Json;

namespace Desktop.App;

public sealed partial class ResultReviewViewModel
{
    private readonly IRenderBatchWorkflow? _rendering;
    private readonly IRenderBatchOutputReader? _outputs;
    private readonly IOutputDirectoryOpener _directoryOpener;
    private readonly CancellationTokenSource _renderLifetime = new();
    private readonly SemaphoreSlim _renderRefresh = new(1, 1);
    private readonly Dictionary<string, ImageSource> _renderPreviews = new(StringComparer.Ordinal);
    private bool _renderRunning;
    private string? _activeRenderBatch;
    private string _renderControlMessage = "";
    private string? _outputDirectoryMessageBatch;
    private string? _outputDirectoryMessageStyle;
    private string _outputDirectoryMessage = "";

    public ICommand StartRenderingCommand { get; private set; } = null!;
    public ICommand PauseRenderingCommand { get; private set; } = null!;
    public ICommand CancelRenderingCommand { get; private set; } = null!;
    public ICommand RefreshRenderingCommand { get; private set; } = null!;
    public ICommand RegenerateResultPageCommand { get; private set; } = null!;
    public ICommand OpenOutputDirectoryCommand { get; private set; } = null!;
    public string RenderedEditingMessage => State.RenderJournal is { } journal && !_host.CanEditRenderedBatch(journal.BatchId)
        ? "按款输出暂不支持生成后修改；仅明确失败页允许恢复，已生成的页面和公开目录保持不变。" : "";
    public string OutputDirectoryMessage => _outputDirectoryMessageBatch == State.RenderPreparation?.BatchId
        && _outputDirectoryMessageStyle == SelectedOutputStyleId()
        ? _outputDirectoryMessage : "";
    public string RenderControlMessage => !string.IsNullOrEmpty(_renderControlMessage)
        ? _renderControlMessage
        : _renderRunning && _activeRenderBatch == State.RenderPreparation?.BatchId
            && (State.RenderJournal is null or { Status: RenderBatchStatus.Prepared } || State.IsWaitingForRenderHost)
                ? "正在等待 Photoshop 就绪；若插件尚未连接，请连接已配对设备，无需重新配对。"
                : "";
    public string RenderActionText => State.RenderJournal?.Status switch
    {
        RenderBatchStatus.RecoveryRequired => "查询并恢复",
        RenderBatchStatus.Paused => "继续渲染",
        _ => "开始渲染"
    };

    private void InitializeRenderingCommands()
    {
        StartRenderingCommand = new AsyncRelayCommand(StartRenderingAsync, () => _rendering is not null && !_renderRunning
            && State.RenderPreparation is { ReadyPageCount: > 0 }
            && State.RenderJournal?.Status is not (RenderBatchStatus.Completed or RenderBatchStatus.CompletedWithIssues or RenderBatchStatus.Cancelled));
        PauseRenderingCommand = new AsyncRelayCommand(() => ControlRenderingAsync(cancel: false), () => _renderRunning);
        CancelRenderingCommand = new AsyncRelayCommand(() => ControlRenderingAsync(cancel: true), () => _renderRunning);
        RefreshRenderingCommand = new AsyncRelayCommand(RefreshRenderingAsync, () => _rendering is not null && State.RenderPreparation is not null);
        RegenerateResultPageCommand = new AsyncRelayCommand(RegenerateSelectedPageAsync,
            () => _rendering is not null && !_renderRunning && SelectedRegenerationPage() is not null);
        OpenOutputDirectoryCommand = new AsyncRelayCommand(OpenOutputDirectoryAsync);
    }

    public async Task OpenOutputDirectoryAsync()
    {
        var batchId = State.RenderPreparation?.BatchId;
        var styleId = SelectedOutputStyleId();
        void Report(string message)
        {
            if (_renderLifetime.IsCancellationRequested || State.RenderPreparation?.BatchId != batchId || SelectedOutputStyleId() != styleId) return;
            _outputDirectoryMessageBatch = batchId;
            _outputDirectoryMessageStyle = styleId;
            _outputDirectoryMessage = message;
            OnPropertyChanged(nameof(OutputDirectoryMessage));
        }
        if (batchId is null) { Report("尚未选择结果批次，无法打开输出目录。"); return; }
        if (_outputs is null) { Report("输出目录定位服务不可用，请重启应用后重试。"); return; }
        Report("");
        try
        {
            if (styleId is null) { Report("请先选择需要查看的款式页面，再打开该款输出目录。"); return; }
            var directory = await _outputs.GetStyleOutputDirectoryAsync(batchId, styleId, _renderLifetime.Token).ConfigureAwait(true);
            // A delayed lookup must never open the previous batch after the user switches results.
            if (_renderLifetime.IsCancellationRequested || State.RenderPreparation?.BatchId != batchId || SelectedOutputStyleId() != styleId) return;
            if (directory is null) { Report("该款尚无已登记且完整发布的输出；如页面已生成，请查询并恢复。未打开目录，也未启动生成。"); return; }
            _directoryOpener.Open(directory);
            Report("已请求打开所选款式输出目录；文件保持原位。");
        }
        catch (OperationCanceledException) { }
        catch (DirectoryNotFoundException) { Report("输出目录离线、不存在或不可访问，请连接原输出磁盘后重试。"); }
        catch (UnauthorizedAccessException) { Report("没有权限访问输出目录，请检查原目录权限后重试。"); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
        { Report("无法安全定位本批次已确认的输出目录，请检查批次记录和原输出位置。"); }
        catch { Report("暂时无法打开输出目录，请检查原输出磁盘和系统文件管理器后重试。"); }
    }

    private string? SelectedOutputStyleId() => SelectedResultPage?.StyleId
        ?? (State.ResultStyleGroups.Select(group => group.StyleId).Distinct(StringComparer.Ordinal).Take(2).ToArray() is { Length: 1 } styles ? styles[0] : null);

    private RenderBatchPage? SelectedRegenerationPage()
    {
        if (State.RenderJournal is not { } journal || SelectedResultPage is not { } selected) return null;
        var page = journal.Pages.Where(item => item.StyleId == selected.StyleId && item.PageId == selected.PageId
            && item.OutputVariantId == selected.OutputVariantId && item.Instance?.PageInstanceId == selected.PageInstanceId).MaxBy(item => item.Attempt);
        if (page is null || !RenderBatchJournalValidator.CanRegenerate(journal, page)) return null;
        return _host.CanEditRenderedBatch(journal.BatchId) || _host.CanRecoverFailedPage(journal.BatchId, page)
            ? page : null;
    }

    public async Task RegenerateSelectedPageAsync()
    {
        if (_rendering is null || _renderRunning || State.RenderJournal is not { } journal
            || SelectedRegenerationPage() is not { } page) return;
        _renderRunning = true;
        SetRenderMessage("");
        _host.ShowOverview();
        RaiseRenderingCommandsChanged();
        var prepared = false;
        try
        {
            var next = page.Instance is { } instance
                ? await _rendering.PrepareInstanceRetryAsync(journal.BatchId, page.StyleId, instance.PageInstanceId,
                    page.Attempt, _renderLifetime.Token).ConfigureAwait(true)
                : await _rendering.PrepareRegenerationAsync(journal.BatchId, page.StyleId, page.PageId,
                    page.OutputVariantId, page.Attempt, _renderLifetime.Token).ConfigureAwait(true);
            State.ApplyRenderJournal(next);
            // A retried page leaves the failure filter; keep it visible without losing its selection.
            State.SelectResultFilter(ResultPageFilter.All);
            prepared = true;
        }
        catch (OperationCanceledException) { }
        catch { SetRenderMessage("尚未确认重新生成，请刷新页面记录；旧输出已保留。"); }
        finally { _renderRunning = false; RaiseRenderingCommandsChanged(); }
        if (prepared) await StartRenderingAsync().ConfigureAwait(true);
    }

    public async Task ContinueSizeTableRepairAsync(RenderBatchJournal journal, SizeTablePageRepairCommand command)
    {
        var target = command.Target;
        if (_renderRunning || State.RenderPreparation?.BatchId != journal.BatchId || journal.BatchId != target.BatchId)
            throw new InvalidOperationException("SIZE_REPAIR_RESULT_CONTEXT_CHANGED");
        var page = journal.Pages.SingleOrDefault(item => item.SizeTableRepair?.RequestId == command.RequestId);
        if (page is null || page.StyleId != target.StyleId || page.PageId != target.PageId
            || page.OutputVariantId != target.OutputVariantId
            || page.SizeTableRepair!.RequestDigest != SizeTableRepairData.RequestDigest(command))
            throw new InvalidOperationException("SIZE_REPAIR_RESPONSE_MISMATCH");
        State.ApplyRenderJournal(journal);
        State.SelectResultFilter(ResultPageFilter.All);
        SelectedResultPage = State.ResultStyleGroups.SelectMany(group => group.Pages).SingleOrDefault(item =>
            item.StyleId == target.StyleId && item.PageId == target.PageId && item.OutputVariantId == target.OutputVariantId);
        _host.ShowOverview();
        RaiseRenderingCommandsChanged();
        // Lost-response recovery can return an already-running/completed intent. Do not auto-run another attempt.
        if (journal.Status == RenderBatchStatus.Prepared && journal.RegenerationPageIndex is { } index
            && journal.Pages[index] == page && page.Status == RenderBatchPageStatus.Planned)
            await StartRenderingAsync().ConfigureAwait(true);
        else await RefreshRenderingAsync().ConfigureAwait(true);
    }

    public async Task<bool> TryRestoreRenderingAsync(string batchId, AiBatchAnalysisProjection? projection)
    {
        if (_rendering is null) return false;
        var journal = await _rendering.LoadAsync(batchId, _renderLifetime.Token).ConfigureAwait(true);
        if (journal is null || journal.BatchId != batchId) return false;
        var pages = journal.Pages.GroupBy(page => (page.StyleId, page.PageId, page.OutputVariantId, page.Instance?.PageInstanceId))
            .Select(group => group.MaxBy(page => page.Attempt)!).Select(page =>
            {
                using var plan = page.PlanJson is null ? null : JsonDocument.Parse(page.PlanJson);
                return new AiRenderPagePreparation(page.StyleId, page.PageId,
                    page.PlanJson is null ? AiRenderPagePreparationStatus.BusinessBlocked : AiRenderPagePreparationStatus.ReadyToRender,
                    page.PreparationReasonCode ?? "RENDER_PREPARATION_REVIEW_REQUIRED", page.PlanJson,
                    plan?.RootElement.GetProperty("plan_digest").GetString(), page.OutputVariantId, page.OutputVariantName, page.Instance);
            }).ToArray();
        // Clear only after the requested journal and its preparation have been confirmed, before new reads.
        if (State.RenderPreparation?.BatchId != batchId)
        {
            SetRenderMessage("");
            _outputDirectoryMessage = "";
            _outputDirectoryMessageBatch = null;
        }
        State.ApplyRenderPreparation(new(batchId, AiRenderPreparationStatus.ReadyToRender, "RENDER_RESTORED", pages), projection);
        State.ApplyRenderJournal(journal);
        await RefreshRenderingAsync().ConfigureAwait(true);
        _host.ShowOverview();
        return true;
    }

    public async Task StartRenderingAsync()
    {
        if (_rendering is null || _renderRunning || State.RenderPreparation is not { } preparation) return;
        _renderRunning = true; _activeRenderBatch = preparation.BatchId;
        SetRenderMessage("");
        RaiseRenderingCommandsChanged();
        try
        {
            var token = _renderLifetime.Token;
            var prepared = await _rendering.PrepareAsync(preparation, token).ConfigureAwait(true);
            State.ApplyRenderJournal(prepared);
            // Workflow has no WPF dependencies; page decoding and controls stay on the dispatcher.
            var running = Task.Run(() => _rendering.RunAsync(preparation.BatchId, token), token);
            try
            {
                while (!running.IsCompleted)
                {
                    await RefreshRenderingAsync().ConfigureAwait(true);
                    await Task.WhenAny(running, Task.Delay(500, token)).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                // Observe the task even when this view is disposed; the workflow durably records interruption.
                await running.ConfigureAwait(true);
            }
            await RefreshRenderingAsync().ConfigureAwait(true);
            SetRenderMessage(State.RenderJournal?.Status == RenderBatchStatus.RecoveryRequired
                ? State.RenderJournal.Pages.Any(page => page.Status is RenderBatchPageStatus.Unknown or RenderBatchPageStatus.Submitting or RenderBatchPageStatus.Running)
                    ? "请保持现有数据，使用“查询并恢复”核对执行结果；不会重投 Unknown。"
                    : "页面生成记录已保留，输出发布或本地证据仍待核验。点击“查询并恢复”，不会重投已完成页。" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetRenderMessage(error.Message switch
            {
                "PHOTOSHOP_HOST_SELECTION_REQUIRED" => "尚未选择正式 Photoshop 宿主。请到设置 → Photoshop 明确保存要使用的程序，再返回生成；未提交页面，旧结果保持不变。",
                "PHOTOSHOP_HOST_SELECTION_INVALID" => "已保存的 Photoshop 程序缺失、已更新或无法验证。请到设置 → Photoshop 重新读取并选择有效程序；未提交页面，原任务状态和旧结果保持不变。",
                "RENDER_OUTPUT_PREFLIGHT_REQUIRED" => "输出预检未通过，未提交任何页面。请检查各款目标是否已存在、原磁盘是否在线及写入权限；不会覆盖旧文件。保留当前批次，修正外部冲突后重试。",
                _ => "当前无法继续渲染。请检查设备配对、Photoshop 连接和输出位置，然后查询恢复。"
            });
        }
        finally { _renderRunning = false; _activeRenderBatch = null; RaiseRenderingCommandsChanged(); }
    }

    private async Task ControlRenderingAsync(bool cancel)
    {
        if (_rendering is null || _activeRenderBatch is not { } batchId) return;
        try
        {
            if (cancel) await _rendering.RequestCancelAsync(batchId, _renderLifetime.Token).ConfigureAwait(true);
            else await _rendering.RequestPauseAsync(batchId, _renderLifetime.Token).ConfigureAwait(true);
            SetRenderMessage(cancel ? "已请求取消，等待当前页安全收口。" : "已请求完成当前款式后暂停。");
            await RefreshRenderingAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch { SetRenderMessage("控制请求未确认，请查询页面状态。"); }
    }

    public async Task RefreshRenderingAsync()
    {
        if (_rendering is null || State.RenderPreparation is not { } preparation || _renderLifetime.IsCancellationRequested) return;
        if (!await _renderRefresh.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            var journal = await _rendering.LoadAsync(preparation.BatchId, _renderLifetime.Token).ConfigureAwait(true);
            if (journal is null || State.RenderPreparation?.BatchId != preparation.BatchId) return;
            if (_outputs is not null)
            {
                var selected = SelectedResultPage;
                var previewPages = journal.Pages.Where(page => page.Status == RenderBatchPageStatus.Committed && page.Commit is not null)
                    .GroupBy(page => (page.StyleId, page.PageId, page.OutputVariantId, page.Instance?.PageInstanceId)).Select(group => group.MaxBy(page => page.Attempt)!)
                    .OrderByDescending(page => selected is not null && page.StyleId == selected.StyleId
                        && page.PageId == selected.PageId && page.OutputVariantId == selected.OutputVariantId && page.Instance?.PageInstanceId == selected.PageInstanceId).Take(96).ToArray();
                var retained = previewPages.Select(page => page.Commit!.ManifestSha256).ToHashSet(StringComparer.Ordinal);
                foreach (var key in _renderPreviews.Keys.Where(key => !retained.Contains(key)).ToArray()) _renderPreviews.Remove(key);
                foreach (var page in previewPages)
                {
                    var digest = page.Commit!.ManifestSha256;
                    if (_renderPreviews.ContainsKey(digest)) continue;
                    try
                    {
                        using var stream = await _outputs.OpenJpegAsync(preparation.BatchId, page, _renderLifetime.Token).ConfigureAwait(true);
                        using var plan = JsonDocument.Parse(page.PlanJson!);
                        var canvas = plan.RootElement.GetProperty("document").GetProperty("canvas");
                        var sourceWidth = canvas.GetProperty("width").GetInt32();
                        var sourceHeight = canvas.GetProperty("height").GetInt32();
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                        if (sourceWidth >= sourceHeight) bitmap.DecodePixelWidth = Math.Min(sourceWidth, BoundedPreviewImageCache.DefaultMaxDimension);
                        else bitmap.DecodePixelHeight = Math.Min(sourceHeight, BoundedPreviewImageCache.DefaultMaxDimension);
                        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                        _renderPreviews[digest] = bitmap;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        if (State.RenderPreparation?.BatchId == preparation.BatchId)
                            SetRenderMessage("部分正式输出无法核验或预览，请检查输出位置并查询恢复。");
                    }
                }
            }
            if (State.RenderPreparation?.BatchId != preparation.BatchId) return;
            State.ApplyRenderJournal(journal, _renderPreviews);
            _host.NotifyDetailSelectionChanged();
            RaiseRenderingCommandsChanged();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (State.RenderPreparation?.BatchId == preparation.BatchId)
                SetRenderMessage("页面记录暂不可读取，请保留数据并重新查询。");
        }
        finally { _renderRefresh.Release(); }
    }

    internal void ReportEditorStatus(string message) => SetRenderMessage(message);

    private void SetRenderMessage(string message)
    {
        _renderControlMessage = message;
        OnPropertyChanged(nameof(RenderControlMessage));
    }

    private void RaiseRenderingCommandsChanged()
    {
        (StartRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshRenderingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RegenerateResultPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnToPreflightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(RenderActionText));
        OnPropertyChanged(nameof(RenderedEditingMessage));
        OnPropertyChanged(nameof(RenderControlMessage));
    }
}
