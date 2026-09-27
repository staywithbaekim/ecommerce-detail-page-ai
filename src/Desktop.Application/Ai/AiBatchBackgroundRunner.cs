using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Desktop.Application.Ai;

/// <summary>
/// Owns admission and consumer tasks for one batch, borrowing the scheduler.
/// The workflow supplies shutdown cancellation and drains tasks before disposal.
/// </summary>
public sealed class AiBatchBackgroundRunner(
    BatchDraft batch,
    TemplateRevision templateRevision,
    AiAnalysisScheduler scheduler,
    CancellationToken shutdownToken) : IDisposable
{
    private readonly BatchDraft _batch = batch;
    private readonly TemplateRevision _templateRevision = templateRevision;
    private readonly AiAnalysisScheduler _scheduler = scheduler;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private readonly ConcurrentDictionary<string, string> _admissionFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _runtimeFailures = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _admissionCancellation = new();
    private readonly object _backgroundGate = new();
    private Task? _backgroundTask;
    private Task? _admissionTask;
    private int _admissionStarted;
    private int _admissionCompleted = 1;

    public string? AdmissionError { get; private set; }
    public ImmutableDictionary<string, string> AdmissionFailures =>
        _admissionFailures.ToImmutableDictionary(StringComparer.Ordinal);
    public ImmutableDictionary<string, string> RuntimeFailures =>
        _runtimeFailures.ToImmutableDictionary(StringComparer.Ordinal);
    private bool AdmissionCompleted => Volatile.Read(ref _admissionCompleted) != 0;
    internal bool IsAdmissionSettled => AdmissionCompleted;

    public void StartAdmission(AiAnalysisProjectionOptions projectionOptions)
    {
        if (!TryStartAdmission())
        {
            return;
        }

        _admissionTask = Task.Run(
            async () =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdownToken,
                    _admissionCancellation.Token);
                try
                {
                    var result = await _scheduler.EnqueueBatchAsync(
                        _batch,
                        _templateRevision,
                        projectionOptions,
                        linkedCancellation.Token).ConfigureAwait(false);
                    foreach (var styleResult in result.StyleResults.Where(item =>
                                 item.Disposition == AiAnalysisWorkDisposition.TerminalError
                                 && !string.IsNullOrWhiteSpace(item.ErrorCode)))
                    {
                        _admissionFailures[styleResult.RequestId] = styleResult.ErrorCode!;
                    }
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    if (!_shutdownToken.IsCancellationRequested
                        && !_admissionCancellation.IsCancellationRequested)
                    {
                        AdmissionError = "AI_ANALYSIS_ADMISSION_CANCELLED";
                    }
                }
                catch (Exception exception)
                {
                    // Keep the UI actionable without exposing exception text or
                    // local paths. The durable scheduler remains the source of
                    // truth for any requests admitted before the failure.
                    AdmissionError = NormalizeAdmissionError(exception);
                }
                finally
                {
                    MarkAdmissionCompleted();
                }
            });
    }

    private static string NormalizeAdmissionError(Exception exception) => exception switch
    {
        InvalidOperationException invalid when !string.IsNullOrWhiteSpace(invalid.Message)
            && invalid.Message.StartsWith("AI_", StringComparison.Ordinal)
            => invalid.Message,
        InvalidDataException invalid when !string.IsNullOrWhiteSpace(invalid.Message)
            && invalid.Message.StartsWith("AI_", StringComparison.Ordinal)
            => invalid.Message,
        IOException io when !string.IsNullOrWhiteSpace(io.Message)
            && io.Message.StartsWith("AI_", StringComparison.Ordinal)
            => io.Message,
        _ => "AI_ANALYSIS_ADMISSION_FAILED"
    };

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A producer can be preparing the next款 while the ready buffer is
            // temporarily empty. Do not exit early; otherwise StartAsync would
            // regress to "prepare everything, then consume".
            if (AdmissionCompleted
                && _scheduler.PendingCount == 0
                && _scheduler.ReadyCount == 0)
            {
                // Keep the consumer alive while the batch remains open. A user
                // can enqueue a selected-asset refinement immediately after
                // the first layer becomes ready; returning here creates a
                // narrow enqueue/exit race in which the refinement is durable
                // but no consumer remains to process it.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_scheduler.ControlState is AiAnalysisSchedulingControlState.Paused
                or AiAnalysisSchedulingControlState.PauseRequested
                or AiAnalysisSchedulingControlState.WaitingForNetwork
                or AiAnalysisSchedulingControlState.CancelRequested
                or AiAnalysisSchedulingControlState.Cancelled)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var readyCount = _scheduler.ReadyCount;
            if (readyCount == 0)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var workerCount = Math.Min(
                AiAnalysisProductionPolicy.QueueV1.MaxConcurrentRequests,
                readyCount);
            var results = await Task.WhenAll(Enumerable.Range(0, workerCount)
                .Select(_ => _scheduler.ProcessNextAsync(cancellationToken))).ConfigureAwait(false);
            foreach (var result in results.Where(result =>
                         result.Disposition is (AiAnalysisWorkDisposition.TerminalError
                             or AiAnalysisWorkDisposition.ResultUnknown)
                         && !string.IsNullOrWhiteSpace(result.ErrorCode)))
            {
                _runtimeFailures[result.RequestId] = result.ErrorCode!;
            }
        }
    }

    public void StartProcessing()
    {
        lock (_backgroundGate)
        {
            if (_backgroundTask is { IsCompleted: false })
            {
                return;
            }

            _backgroundTask = Task.Run(
                () => ProcessBatchInBackgroundAsync(_shutdownToken),
                _shutdownToken);
        }
    }

    private async Task ProcessBatchInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessBatchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown cancels background work; durable scheduler
            // state remains the source of truth for the next restore.
        }
        catch (InvalidOperationException)
        {
            // The scheduler has already persisted a terminal/recovery state.
            // The next explicit refresh projects the actionable Chinese state.
        }
    }

    private bool TryStartAdmission()
    {
        if (Interlocked.CompareExchange(ref _admissionStarted, 1, 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _admissionCompleted, 0);
        return true;
    }

    private void MarkAdmissionCompleted() => Volatile.Write(ref _admissionCompleted, 1);

    public void StopAdmission() => _admissionCancellation.Cancel();

    public void WaitForCompletion()
    {
        // StartAsync now returns while admission/consumer work continues. Do
        // not dispose the scheduler or its file store underneath those tasks;
        // otherwise shutdown can race an atomic staging write and leave a
        // temporary file locked. The bounded wait only applies to shutdown;
        // Provider calls retain the adapter's bounded total/stage deadlines
        // defined by the production policy. Shutdown must
        // not dispose a request while its deferred observer is still draining.
        foreach (var task in new[] { _admissionTask, _backgroundTask }
                     .Where(task => task is not null)
                     .Select(task => task!))
        {
            try
            {
                _ = task.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The durable scheduler state is the recovery source; an
                // exception during shutdown must not mask application exit.
            }
        }
    }

    public void Dispose()
    {
        _admissionCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
