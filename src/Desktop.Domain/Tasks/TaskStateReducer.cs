namespace Desktop.Domain.Tasks;

public static class TaskStateReducer
{
    public static TaskState Replay(string taskId, IEnumerable<TaskEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(events);

        var state = TaskState.Empty(taskId);
        foreach (var taskEvent in events)
        {
            state = Apply(state, taskEvent);
        }

        return state;
    }

    public static TaskState Apply(TaskState state, TaskEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(taskEvent);

        if (!string.Equals(state.TaskId, taskEvent.TaskId, StringComparison.Ordinal))
        {
            throw Violation("TASK_EVENT_TASK_MISMATCH", "事件不属于当前任务。");
        }

        if (state.AppliedEventIds.Contains(taskEvent.EventId))
        {
            return state;
        }

        if (taskEvent.Sequence <= state.LastSequence)
        {
            throw Violation("TASK_EVENT_SEQUENCE_REGRESSION", "事件序号不能倒退。");
        }

        if (taskEvent.Sequence != state.LastSequence + 1)
        {
            throw Violation("TASK_EVENT_SEQUENCE_GAP", "事件序号必须连续。");
        }

        var next = ApplyCore(state, taskEvent);
        return next with
        {
            LastSequence = taskEvent.Sequence,
            AppliedEventIds = next.AppliedEventIds.Add(taskEvent.EventId)
        };
    }

    private static TaskState ApplyCore(TaskState state, TaskEvent taskEvent) => taskEvent.Type switch
    {
        TaskEventType.TaskCreated => Transition(state, TaskStatus.Draft, TaskStatus.None),
        TaskEventType.ScanningStarted => Transition(state, TaskStatus.Scanning,
            TaskStatus.Draft, TaskStatus.Paused, TaskStatus.RecoveryRequired),
        TaskEventType.SystemBlocked => Transition(state, TaskStatus.SystemBlocked,
            TaskStatus.Draft, TaskStatus.Scanning, TaskStatus.ReadyToRender,
            TaskStatus.Rendering, TaskStatus.Paused, TaskStatus.RecoveryRequired,
            TaskStatus.UserActionRequired),
        TaskEventType.ReadyToRender => Transition(state, TaskStatus.ReadyToRender,
            TaskStatus.Draft, TaskStatus.Scanning, TaskStatus.SystemBlocked,
            TaskStatus.Paused, TaskStatus.RecoveryRequired, TaskStatus.UserActionRequired),
        TaskEventType.RenderingStarted => Transition(state, TaskStatus.Rendering,
            TaskStatus.ReadyToRender, TaskStatus.Paused, TaskStatus.RecoveryRequired),
        TaskEventType.PauseRequested => Transition(state, TaskStatus.Paused,
            TaskStatus.Scanning, TaskStatus.ReadyToRender, TaskStatus.Rendering,
            TaskStatus.UserActionRequired),
        TaskEventType.RecoveryRequired => Transition(state, TaskStatus.RecoveryRequired,
            TaskStatus.Draft, TaskStatus.Scanning, TaskStatus.SystemBlocked,
            TaskStatus.ReadyToRender, TaskStatus.Rendering, TaskStatus.Paused,
            TaskStatus.UserActionRequired),
        TaskEventType.Failed => TransitionFromNonTerminal(state, TaskStatus.Failed),
        TaskEventType.Cancelled => TransitionFromNonTerminal(state, TaskStatus.Cancelled),
        TaskEventType.UserActionRequired => Transition(state, TaskStatus.UserActionRequired,
            TaskStatus.Draft, TaskStatus.Scanning, TaskStatus.SystemBlocked,
            TaskStatus.ReadyToRender, TaskStatus.Rendering, TaskStatus.Paused,
            TaskStatus.RecoveryRequired),
        TaskEventType.PageCommitted => CommitPage(state, taskEvent.PageId),
        TaskEventType.IssueRecorded => RecordIssue(state),
        TaskEventType.Completed => Complete(state, withIssues: false),
        TaskEventType.CompletedWithIssues => Complete(state, withIssues: true),
        TaskEventType.RenderPageRegenerationRequested or TaskEventType.RenderPageRepairRequested => ReopenForRenderPage(state, taskEvent),
        _ => throw Violation("TASK_EVENT_UNKNOWN", "无法识别任务事件。")
    };

    private static TaskState ReopenForRenderPage(TaskState state, TaskEvent taskEvent)
    {
        EnsureStatus(state, TaskStatus.Completed, TaskStatus.CompletedWithIssues, TaskStatus.Cancelled,
            TaskStatus.Failed, TaskStatus.Paused, TaskStatus.UserActionRequired);
        if (string.IsNullOrWhiteSpace(taskEvent.PageId) || taskEvent.PayloadDigest.Length != 71
            || !taskEvent.PayloadDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || taskEvent.PayloadDigest[7..].Any(c => !char.IsAsciiHexDigitLower(c)))
            throw Violation("TASK_REGENERATION_IDENTITY_REQUIRED", "重新生成必须绑定页面和新计划摘要。");
        return state with { Status = TaskStatus.ReadyToRender };
    }

    private static TaskState CommitPage(TaskState state, string? pageId)
    {
        EnsureStatus(state, TaskStatus.Rendering, TaskStatus.RecoveryRequired);
        if (string.IsNullOrWhiteSpace(pageId))
        {
            throw Violation("TASK_PAGE_ID_REQUIRED", "提交页面必须包含页面标识。");
        }

        return state with { CommittedPageIds = state.CommittedPageIds.Add(pageId) };
    }

    private static TaskState RecordIssue(TaskState state)
    {
        EnsureNotTerminal(state);
        return state with { IssueCount = checked(state.IssueCount + 1) };
    }

    private static TaskState Complete(TaskState state, bool withIssues)
    {
        EnsureStatus(state, TaskStatus.Rendering, TaskStatus.ReadyToRender, TaskStatus.RecoveryRequired);
        if (state.OutputCount == 0)
        {
            throw Violation("TASK_ZERO_OUTPUT_CANNOT_COMPLETE", "没有已提交输出的任务不能标记为完成。");
        }

        if (withIssues && state.IssueCount == 0)
        {
            throw Violation("TASK_COMPLETED_WITH_ISSUES_REQUIRES_ISSUE", "完成但有问题必须包含问题记录。");
        }

        if (!withIssues && state.IssueCount > 0)
        {
            throw Violation("TASK_COMPLETED_HAS_UNRESOLVED_ISSUES", "存在问题记录的任务不能标记为普通完成。");
        }

        return state with
        {
            Status = withIssues ? TaskStatus.CompletedWithIssues : TaskStatus.Completed
        };
    }

    private static TaskState Transition(TaskState state, TaskStatus target, params TaskStatus[] allowed)
    {
        EnsureStatus(state, allowed);
        return state with { Status = target };
    }

    private static TaskState TransitionFromNonTerminal(TaskState state, TaskStatus target)
    {
        EnsureNotTerminal(state);
        if (state.Status == TaskStatus.None)
        {
            throw Violation("TASK_NOT_CREATED", "任务尚未创建。");
        }

        return state with { Status = target };
    }

    private static void EnsureNotTerminal(TaskState state)
    {
        if (state.IsTerminal)
        {
            throw Violation("TASK_ALREADY_TERMINAL", "终态任务不能继续推进。");
        }
    }

    private static void EnsureStatus(TaskState state, params TaskStatus[] allowed)
    {
        if (!allowed.Contains(state.Status))
        {
            throw Violation("TASK_INVALID_TRANSITION", $"不能从 {state.Status} 执行当前事件。");
        }
    }

    private static TaskRuleViolationException Violation(string code, string message) => new(code, message);
}
