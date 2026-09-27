using System.ComponentModel;
using Desktop.Application.Rendering;

namespace Desktop.App;

public sealed partial class SizeTableEditorViewModel
{
    private readonly ISizeTablePageRepairWorkflow? _repairs;
    private SizeTableRepairBaseline? _repairBaseline;
    private SizeTablePageRepairCommand? _pendingRepair;
    private bool _isOpening;
    public bool IsOpening
    {
        get => _isOpening;
        private set
        {
            _isOpening = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }
    public bool IsBusy => IsOpening || SizeTableEditor.IsSaving;

    private async Task SaveRepairAsync()
    {
        if (_repairs is null || _repairBaseline is null || SizeTableEditor.IsSaving) return;
        if (_host.BatchId != _repairBaseline.Target.BatchId)
        {
            SizeTableEditor.StatusMessage = "当前批次已切换，请返回原任务再保存；未提交修改。";
            return;
        }
        if (_pendingRepair is null)
        {
            if (!SizeTableEditor.TryBuild(out var draft)) return;
            _pendingRepair = new("size-repair-" + Guid.NewGuid().ToString("N"), _repairBaseline.Target, draft);
        }
        var command = _pendingRepair;
        SizeTableEditor.IsRepairPending = true;
        SizeTableEditor.IsSaving = true;
        try
        {
            // Freeze the exact command across lost responses. Never fall back to pre-generation current.json.
            var journal = await _repairs.PrepareSizeTableRepairAsync(command, CancellationToken.None).ConfigureAwait(true);
            var page = journal.Pages.SingleOrDefault(item => item.SizeTableRepair?.RequestId == command.RequestId);
            if (journal.BatchId != command.Target.BatchId || page?.SizeTableRepair is not { } repair
                || repair.RequestDigest != SizeTableRepairData.RequestDigest(command))
                throw new InvalidOperationException("SIZE_REPAIR_RESPONSE_MISMATCH");
            var saved = SizeTableRepairData.ReadEditorDocument(repair.TableJson);
            SizeTableEditor.AcceptSaved(saved);
            IsSizeTableEditorVisible = false;
            _pendingRepair = null;
            SizeTableEditor.IsRepairPending = false;
            // This callback applies the registered single-page attempt, not whole-batch AI preparation.
            await _host.ContinueSizeTableRepairAsync(journal, command).ConfigureAwait(true);
        }
        catch (Exception error) when (_pendingRepair is not null && error is InvalidOperationException && IsDefiniteRejection(error.Message))
        {
            _pendingRepair = null;
            SizeTableEditor.IsRepairPending = false;
            SizeTableEditor.StatusMessage = "修复未登记：基线已变化、任务忙碌或内容不适用。请关闭后重新打开当前页；旧输出保持不变。";
        }
        catch (Exception)
        {
            // An I/O/DB/response failure is not proof of rollback. Retry only this immutable request.
            SizeTableEditor.StatusMessage = _pendingRepair is null
                ? "尺码修复已登记，界面刷新或执行尚未完成。请从任务恢复当前批次；不要再次提交修改。"
                : "尚未确认修复登记结果。草稿已锁定，可再次点击确认同一请求；不会创建额外 attempt。也可关闭后从任务恢复。";
            if (!IsSizeTableEditorVisible) _host.ShowAnalysisStatus(SizeTableEditor.StatusMessage);
        }
        finally { SizeTableEditor.IsSaving = false; }
    }

    private static bool IsDefiniteRejection(string reason) => reason is "RENDER_BATCH_BUSY"
        or "SIZE_REPAIR_NOT_ALLOWED" or "SIZE_REPAIR_REVISION_CONFLICT"
        or "SIZE_REPAIR_BASELINE_MISMATCH" or "SIZE_REPAIR_NO_CHANGES" or "SIZE_REPAIR_AI_NOT_SETTLED";
}
