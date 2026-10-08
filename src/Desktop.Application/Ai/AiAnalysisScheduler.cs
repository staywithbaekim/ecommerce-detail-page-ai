using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

/// <summary>
/// Bounded S3 producer/consumer orchestration. Provider details remain behind
/// IAiServicePort and all business progress is persisted through IAiAnalysisStore.
/// </summary>
public sealed partial class AiAnalysisScheduler : IDisposable
{
    private readonly IAiServicePort _provider;
    private readonly IAiAnalysisStore _store;
    private readonly IAiAnalysisWorkBuffer _queue;
    private readonly AiAnalysisQueueOptions _queueOptions;
    private readonly AiAnalysisBudgetPolicy _budgetPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IAiAnalysisSchedulingStateStore? _stateStore;
    private readonly IAiAnalysisResultStore? _resultStore;
    private readonly IAiAnalysisCopyPreparer? _copyPreparer;
    private readonly IAiAnalysisCopyLeaseManager? _copyLeaseManager;
    private readonly IAiAnalysisCheckpointPort? _checkpointPort;
    private readonly IConsentPort? _consentPort;
    private readonly bool _strictResultPersistence;
    private readonly string _stateKey;
    private readonly AiAnalysisBudgetLedger _budgetLedger;
    private readonly AiAnalysisSchedulerState _state = new();
    private object _pendingGate => _state.PendingGate;
    private object _timestampGate => _state.TimestampGate;
    private Dictionary<string, AiAnalysisWorkItem> _pendingItems => _state.PendingItems;
    private HashSet<string> _automaticRefillSuppressedItems => _state.AutomaticRefillSuppressedItems;
    private Dictionary<string, AiAnalysisRequestV1> _historicalRequests => _state.HistoricalRequests;
    private Dictionary<string, string> _terminalErrors => _state.TerminalErrors;
    private ImmutableHashSet<string> _recoveredInFlightRequestIds
    {
        get => _state.RecoveredInFlightRequestIds;
        set => _state.RecoveredInFlightRequestIds = value;
    }
    private readonly SemaphoreSlim _statePersistenceGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate;
    private readonly SemaphoreSlim _copyPreparationGate;
    private object _budgetGate => _budgetLedger.Gate;
    private AiAnalysisBudgetSnapshot _budget
    {
        get => _budgetLedger.UnsafeSnapshot;
        set => _budgetLedger.UnsafeSnapshot = value;
    }
    private AiAnalysisSchedulingControlState _controlState
    {
        get => _state.ControlState;
        set => _state.ControlState = value;
    }
    private string? _controlReason
    {
        get => _state.ControlReason;
        set => _state.ControlReason = value;
    }
    private DateTimeOffset _lastPersistenceTimestamp
    {
        get => _state.LastPersistenceTimestamp;
        set => _state.LastPersistenceTimestamp = value;
    }

    public AiAnalysisScheduler(
        IAiServicePort provider,
        IAiAnalysisStore store,
        IAiAnalysisWorkBuffer queue,
        AiAnalysisBudgetPolicy budgetPolicy,
        TimeProvider? timeProvider = null,
        AiAnalysisQueueOptions? queueOptions = null,
        IAiAnalysisSchedulingStateStore? stateStore = null,
        string stateKey = "ai-s3-default",
        IAiAnalysisResultStore? resultStore = null,
        IAiAnalysisCopyPreparer? copyPreparer = null,
        IAiAnalysisCopyLeaseManager? copyLeaseManager = null,
        IAiAnalysisCheckpointPort? checkpointPort = null,
        IConsentPort? consentPort = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _budgetPolicy = budgetPolicy ?? throw new ArgumentNullException(nameof(budgetPolicy));
        _budgetPolicy.Validate();
        _budgetLedger = new AiAnalysisBudgetLedger(_budgetPolicy);
        _queueOptions = queueOptions ?? new AiAnalysisQueueOptions();
        _queueOptions.Validate();
        _processingGate = new SemaphoreSlim(
            _queueOptions.MaxConcurrentRequests,
            _queueOptions.MaxConcurrentRequests);
        _copyPreparationGate = new SemaphoreSlim(
            _queueOptions.MaxConcurrentCopyPreparations,
            _queueOptions.MaxConcurrentCopyPreparations);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stateStore = stateStore;
        _resultStore = resultStore;
        _copyPreparer = copyPreparer;
        _copyLeaseManager = copyLeaseManager ?? copyPreparer as IAiAnalysisCopyLeaseManager;
        _checkpointPort = checkpointPort;
        _consentPort = consentPort;
        _strictResultPersistence = copyPreparer is not null || copyLeaseManager is not null || checkpointPort is not null;
        if (string.IsNullOrWhiteSpace(stateKey)
            || stateKey.Length > 128
            || stateKey.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("AI 调度状态键无效。", nameof(stateKey));
        }

        _stateKey = stateKey;
    }

    public AiAnalysisBudgetSnapshot Budget
    {
        get
        {
            lock (_budgetGate)
            {
                return _budgetLedger.Snapshot;
            }
        }
    }

    public AiAnalysisSchedulingControlState ControlState
    {
        get
        {
            lock (_pendingGate)
            {
                return _controlState;
            }
        }
    }

    public string? ControlReason
    {
        get
        {
            lock (_pendingGate)
            {
                return _controlReason;
            }
        }
    }

    /// <summary>
    /// Durable key for this batch-scoped scheduler.  B-03 uses it only to read
    /// the provider-neutral scheduling snapshot; it is never shown to users.
    /// </summary>
    public string StateKey => _stateKey;

    /// <summary>
    /// Number of durable work items currently pending in this scheduler.
    /// Exposed for application orchestration so a bounded B-03 refresh can
    /// drain work without duplicating the scheduler state machine.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_pendingGate)
            {
                return _pendingItems.Count;
            }
        }
    }

    /// <summary>
    /// Number of items that are safe to execute now. Durable pending items can
    /// also contain Uploading/Analysing recovery evidence, so orchestration
    /// must never use <see cref="PendingCount"/> as a dequeue count.
    /// </summary>
    public int ReadyCount => _queue.Count;

    internal bool IsIdleForRepair => PendingCount == 0 && ReadyCount == 0
        && _processingGate.CurrentCount == _queueOptions.MaxConcurrentRequests
        && _copyPreparationGate.CurrentCount == _queueOptions.MaxConcurrentCopyPreparations;

    /// <summary>Requests retained after restart whose provider outcome cannot be inferred.</summary>
    public ImmutableHashSet<string> RecoveredInFlightRequestIds
    {
        get
        {
            lock (_pendingGate) return _recoveredInFlightRequestIds;
        }
    }

    /// <summary>Durable terminal or unknown provider error codes keyed by request id.</summary>
    public IReadOnlyDictionary<string, string> TerminalErrorsSnapshot
    {
        get
        {
            lock (_pendingGate)
            {
                return new Dictionary<string, string>(_terminalErrors, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Commits a user selection and the matching request-ledger transition as
    /// one full-batch checkpoint.  Presentation must not save either resource
    /// independently because a crash between the two writes would lose the
    /// user's lock or expose an inconsistent recovery graph.
    /// </summary>

}
