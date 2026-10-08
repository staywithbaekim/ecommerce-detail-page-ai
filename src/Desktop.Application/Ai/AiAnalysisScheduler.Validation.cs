using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

public sealed partial class AiAnalysisScheduler
{
    private DateTimeOffset NextPersistenceTimestamp()
    {
        lock (_timestampGate)
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            if (now <= _lastPersistenceTimestamp)
            {
                now = _lastPersistenceTimestamp.AddTicks(1);
            }

            _lastPersistenceTimestamp = now;
            return now;
        }
    }

    private static void ValidateWorkItem(AiAnalysisWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.WorkItemId)
            || item.WorkItemId.Length > 256
            || item.WorkItemId.Any(character => character is '\\' or '/' or '\0'))
        {
            throw new InvalidOperationException("AI_SCHEDULER_WORK_ITEM_INVALID");
        }

        if (item.Attempt < 0 || item.EnqueuedAt == default)
        {
            throw new InvalidOperationException("AI_SCHEDULER_WORK_ITEM_INVALID");
        }

        ValidateStage(item.Request, item.Stage);
        if (item.Request.Assets.Any(asset =>
                string.IsNullOrWhiteSpace(asset.AnalysisCopyReference)
                || Path.IsPathFullyQualified(asset.AnalysisCopyReference)
                || asset.AnalysisCopyReference.Contains('\\')
                || asset.AnalysisCopyReference.Contains(':')))
        {
            throw new InvalidOperationException("AI_SCHEDULER_SOURCE_PATH_INVALID");
        }
    }

    private static void ValidateBudgetSnapshot(AiAnalysisBudgetSnapshot snapshot)
    {
        if (snapshot.RequestCount < 0
            || snapshot.InputTokens < 0
            || snapshot.OutputTokens < 0
            || snapshot.EstimatedCost < 0m)
        {
            throw new InvalidDataException("AI_SCHEDULER_BUDGET_CORRUPT");
        }
    }

    private void SetControlState(AiAnalysisSchedulingControlState state, string? reason)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (reason is not null && (reason.Length > 512 || reason.Contains('\0')))
        {
            throw new ArgumentException("AI 调度控制原因无效。", nameof(reason));
        }

        lock (_pendingGate)
        {
            if (_controlState == AiAnalysisSchedulingControlState.Cancelled
                && state != AiAnalysisSchedulingControlState.Cancelled)
            {
                throw new InvalidOperationException("AI_SCHEDULER_CANCELLED");
            }

            _controlState = state;
            _controlReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }
    }

    private AiAnalysisBudgetSnapshot NormalizeBudgetSnapshot(AiAnalysisBudgetSnapshot snapshot)
    {
        return _budgetLedger.Normalize(snapshot);
    }

    private static string SessionId(AiAnalysisRequestV1 request) => $"session-{request.RequestId}";

    private static void ValidateStage(AiAnalysisRequestV1 request, AiAnalysisWorkStage stage)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedKind = stage switch
        {
            AiAnalysisWorkStage.ProductJointUnderstanding => AiAnalysisKind.ProductJointUnderstanding,
            AiAnalysisWorkStage.SelectedAssetGeometry => AiAnalysisKind.SelectedAssetGeometry,
            AiAnalysisWorkStage.SizeTableExtraction => AiAnalysisKind.SizeTableExtraction,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        if (!string.Equals(request.AnalysisKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_ANALYSIS_STAGE_KIND_MISMATCH");
        }
    }

    public void Dispose()
    {
        if (_queue is IDisposable disposableQueue)
        {
            disposableQueue.Dispose();
        }
        _processingGate.Dispose();
        _copyPreparationGate.Dispose();
        _statePersistenceGate.Dispose();
    }

}
