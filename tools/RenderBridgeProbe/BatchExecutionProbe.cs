using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Psd;

internal static class BatchExecutionProbe
{
    private const string HostInterruptionEvidenceFile = "controlled-photoshop-exit.json";
    private const string SupportedPhotoshopExecutableVersion = "26.8 (20250624.r.8 5e0d05d)";

    public static bool TryCreatePhotoshopTerminationTarget(int processId, string expectedExecutablePath,
        long expectedStartTimeUtcTicks, out PhotoshopTerminationTarget? target, out string? rejection)
    {
        target = null;
        rejection = null;
        if (processId <= 0 || expectedStartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            rejection = "invalid_parameters";
            return false;
        }
        string normalizedPath;
        try
        {
            if (!Path.IsPathFullyQualified(expectedExecutablePath))
            {
                rejection = "path_not_absolute";
                return false;
            }
            normalizedPath = Path.GetFullPath(expectedExecutablePath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejection = "path_invalid";
            return false;
        }
        if (!string.Equals(Path.GetFileName(normalizedPath), "Photoshop.exe", StringComparison.OrdinalIgnoreCase))
        {
            rejection = "not_photoshop_executable";
            return false;
        }
        var pathFingerprint = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant())));
        var candidate = new PhotoshopTerminationTarget(processId, normalizedPath, expectedStartTimeUtcTicks,
            pathFingerprint, SupportedPhotoshopExecutableVersion);
        if (!TryOpenVerifiedPhotoshopTarget(candidate, out var process, out rejection)) return false;
        process.Dispose();
        target = candidate;
        return true;
    }

    public static async Task<int> RunAsync(string root, string worker, string? pipeName, string? cancelAt = null,
        (string Style, string Page, string? Variant, int Attempt)? regenerate = null, bool pairedDevice = false,
        string? crashAt = null, bool recoverInterrupted = false,
        PhotoshopTerminationTarget? terminatePhotoshop = null, bool recoverHostInterrupted = false,
        string? outputLockMode = null)
    {
        if (!Path.IsPathFullyQualified(root) || pairedDevice && pipeName is not null || pipeName is not null
            && !System.Text.RegularExpressions.Regex.IsMatch(pipeName, "^render_probe_[a-f0-9]{32}$")) return 2;
        var liveExecution = pipeName is not null || pairedDevice;
        var paths = new LocalAppPathPolicy(root);
        using var preparation = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ResolveOwnedPath("batch-preparation.json")));
        var batchId = preparation.RootElement.GetProperty("batch_id").GetString()!;
        var mixedProduct = preparation.RootElement.TryGetProperty("mixed_product_probe", out var mixedFlag) && mixedFlag.GetBoolean();
        var completeStyle = preparation.RootElement.TryGetProperty("complete_style_probe", out var completeFlag) && completeFlag.GetBoolean();
        var sameSourceIdentity = preparation.RootElement.TryGetProperty("same_source_identity_probe", out var sameSourceFlag)
            && sameSourceFlag.GetBoolean();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (pipeName is not null)
            {
                using var introductionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
                Console.WriteLine("PAIRING_CHANNEL_READY");
                await pipe.WaitForConnectionAsync(introductionTimeout.Token);
                await pipe.ReadExactlyAsync(secret, introductionTimeout.Token);
                var extra = new byte[1];
                if (await pipe.ReadAsync(extra, introductionTimeout.Token) != 0) return 2;
            }
            await using var store = new SqliteDesktopStore(new(paths.ResolveOwnedPath("app")));
            await store.InitializeAsync();
            var files = new AtomicFileStore(paths);
            var schemas = new JsonSchemaContractValidator();
            var batchRepository = new RecoveringBatchRepository(store, new AssetManifestFileStore(files, schemas),
                TimeProvider.System, new JsonInputCommitJournalStore(files, paths));
            // Reuse the existing protected device pairing for approved real-machine checks.
            // This path never saves, rotates or exports the credential.
            IBridgePairingSecretStore pairing = pairedDevice ? new WindowsBridgePairingStore() : new MemorySecret(secret);
            await using var owners = new BridgeSessionOwnerFactory(pairing, paths, TimeProvider.System);
            var existing = (await store.LoadRenderBatchAsync(batchId, default))!;
            var recoverOutputLock = outputLockMode == "recover";
            if (outputLockMode is not (null or "lock" or "recover") || outputLockMode is not null
                && (pipeName is not null || pairedDevice != (outputLockMode == "lock") || crashAt is not null
                    || cancelAt is not null || regenerate is not null || terminatePhotoshop is not null
                    || recoverInterrupted || recoverHostInterrupted || !sameSourceIdentity || existing.Pages.Count != 3
                    || existing.Version != 1 || existing.CancelRequested || existing.RegenerationPageIndex is not null)) return 2;
            var lockEvidencePath = paths.ResolveOwnedPath("controlled-output-lock.json");
            if (outputLockMode == "lock" && (existing.Pages.Any(page => page.Status != RenderBatchPageStatus.Planned)
                || File.Exists(lockEvidencePath) || EnumerateFormalOutputFiles(root).Length != 0)) return 2;
            if (recoverOutputLock)
            {
                if (!File.Exists(lockEvidencePath)) return 2;
                using var locked = JsonDocument.Parse(await File.ReadAllTextAsync(lockEvidencePath));
                if (locked.RootElement.GetProperty("batch_id").GetString() != batchId
                    || !locked.RootElement.GetProperty("armed").GetBoolean()
                    || locked.RootElement.GetProperty("completion_reached").GetBoolean()
                    || existing.Pages[0].Status != RenderBatchPageStatus.Committed
                    || existing.Pages[1].Status != RenderBatchPageStatus.Unknown
                    || existing.Pages[2].Status != RenderBatchPageStatus.Planned) return 2;
            }
            using var outputLock = outputLockMode == "lock"
                ? new OutputCommitLockProbe(store, Path.Combine(root, "outputs"), existing.Pages[1].PlanJson!) : null;
            var backend = new BridgeRenderBatchSessionFactory(batchRepository, owners, new RenderBatchResourcePreparer(paths, store),
                new(worker, TimeSpan.FromMinutes(2), 16 * 1024 * 1024), new RenderPlanValidator(schemas),
                (IRenderPageCommitStore?)outputLock ?? store);
            await using var workflow = new RenderBatchCoordinator(store, backend, new RenderPlanValidator(schemas), new(store), TimeProvider.System);
            var processInterruptionPath = paths.ResolveOwnedPath("controlled-process-exit.json");
            var hostInterruptionPath = paths.ResolveOwnedPath(HostInterruptionEvidenceFile);
            var existingFaultMarker = Directory.EnumerateFiles(root, "controlled-*.json", SearchOption.TopDirectoryOnly).Any()
                || File.Exists(paths.ResolveOwnedPath("probe-failure.json"));
            if (crashAt is not (null or "commit" or "output" or "modal") || crashAt is not null
                && (!pairedDevice || cancelAt is not null || regenerate is not null || terminatePhotoshop is not null
                    || recoverHostInterrupted
                    || existing.Pages.Any(page => page.Status != RenderBatchPageStatus.Planned)
                    || !sameSourceIdentity || File.Exists(processInterruptionPath))) return 2;
            if (recoverInterrupted && (liveExecution || crashAt is not null || cancelAt is not null
                || regenerate is not null || terminatePhotoshop is not null || recoverHostInterrupted
                || !File.Exists(processInterruptionPath))) return 2;
            if (terminatePhotoshop is not null && (!pairedDevice || pipeName is not null || crashAt is not null
                || cancelAt is not null || regenerate is not null || recoverInterrupted || recoverHostInterrupted
                || mixedProduct || completeStyle || !sameSourceIdentity || existing.Version != 1
                || existing.RegenerationPageIndex is not null || existing.CancelRequested || existing.PauseRequested
                || existing.Pages.Count != 3 || existing.Pages.Any(page => page.Status != RenderBatchPageStatus.Planned)
                || existingFaultMarker || EnumerateFormalOutputFiles(root).Length != 0)) return 2;
            if (recoverHostInterrupted && (liveExecution || crashAt is not null || cancelAt is not null
                || regenerate is not null || recoverInterrupted || terminatePhotoshop is not null
                || mixedProduct || completeStyle || !sameSourceIdentity || existing.Version != 1
                || existing.RegenerationPageIndex is not null || existing.CancelRequested
                || existing.Pages.Count != 3 || !File.Exists(hostInterruptionPath)
                || File.Exists(processInterruptionPath))) return 2;
            var interruptedDuringModal = false;
            if (recoverInterrupted)
            {
                using var interrupted = JsonDocument.Parse(await File.ReadAllTextAsync(processInterruptionPath));
                interruptedDuringModal = interrupted.RootElement.GetProperty("boundary").GetString() == "modal";
            }
            HostRecoveryEvidence? hostRecovery = null;
            if (recoverHostInterrupted
                && !TryReadHostInterruptionEvidence(hostInterruptionPath, batchId, out hostRecovery)) return 2;
            if (hostRecovery is not null && (existing.Pages[0].Status != RenderBatchPageStatus.Committed
                || existing.Pages[1].Status is not (RenderBatchPageStatus.Running or RenderBatchPageStatus.Unknown)
                || existing.Pages[2].Status != RenderBatchPageStatus.Planned
                || !hostRecovery.PageExecutionIds.SequenceEqual(existing.Pages.Select(page => page.ExecutionId), StringComparer.Ordinal))) return 2;
            if (regenerate is { } requested)
            {
                if (!liveExecution || cancelAt is not null) return 2;
                existing = await workflow.PrepareRegenerationAsync(batchId, requested.Style, requested.Page,
                    requested.Variant, requested.Attempt, default);
            }
            if (cancelAt is not (null or "output" or "resource" or "modal") || cancelAt is not null
                && (!liveExecution || existing.Pages.Any(page => page.Status != RenderBatchPageStatus.Planned))) return 2;
            if (!liveExecution && !recoverInterrupted && !recoverHostInterrupted && !recoverOutputLock
                && existing.Pages.Where((_, index) => existing.RegenerationPageIndex is null
                || existing.RegenerationPageIndex == index).Any(page => page.Status == RenderBatchPageStatus.Planned)) return 2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var running = workflow.RunAsync(batchId, timeout.Token);
            var bridgeKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(batchId)));
            var bridgeJournal = new FileBridgeExecutionJournalStore(paths, $"render/tasks/{bridgeKey}/bridge-executions-v1.json");
            long heartbeats = 0;
            BridgeLoopbackHost.TransportFault? transportFault = null;
            BridgeExecutionBroker.BindingFault? bindingFault = null;
            string? previous = null;
            var listening = false;
            long? cancelAfterBytes = null;
            var cancelDuringResource = false;
            BridgeSessionSnapshot? cancelModalHeartbeat = null;
            DateTimeOffset? modalCancelRequestedAt = null;
            PhotoshopTerminationObservation? photoshopTermination = null;
            var photoshopTerminationAttempted = false;
            string? photoshopTargetRejection = null;
            DateTimeOffset? hostDisconnectedAt = null;
            BridgeSessionSnapshot? hostDisconnectedStatus = null;
            while (!running.IsCompleted)
            {
                var status = await backend.GetStatusAsync(timeout.Token);
                heartbeats = Math.Max(heartbeats, status.Heartbeats);
                transportFault = status.TransportFault ?? transportFault;
                bindingFault = status.BindingFault ?? bindingFault;
                if (status.Listening && !listening) { Console.WriteLine("LISTENING"); listening = true; }
                var current = await workflow.LoadAsync(batchId, timeout.Token);
                if ((recoverInterrupted || recoverOutputLock) && current!.Status == RenderBatchStatus.Running && !current.PauseRequested)
                    await workflow.RequestPauseAsync(batchId, timeout.Token);
                if (crashAt is not null || terminatePhotoshop is not null && !photoshopTerminationAttempted)
                {
                    var bridge = await bridgeJournal.LoadAsync(timeout.Token);
                    var copiesRoot = Path.Combine(root, "outputs", ".app-staging", "resources");
                    var copies = Directory.Exists(copiesRoot) ? Directory.EnumerateFiles(copiesRoot, "*", SearchOption.AllDirectories).Count() : 0;
                    var partsRoot = Path.Combine(root, "outputs", "rendered");
                    var parts = Directory.Exists(partsRoot) ? Directory.EnumerateFiles(partsRoot, "editable_psd.part", SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path).Length).ToArray() : [];
                    var secondExecution = current!.Pages.Count > 1
                        ? bridge?.Executions.SingleOrDefault(item => item.ExecutionId == current.Pages[1].ExecutionId) : null;
                    var secondPageRunning = current.Pages.Count > 1
                        && current.Pages[0].Status == RenderBatchPageStatus.Committed
                        && current.Pages[1].Status == RenderBatchPageStatus.Running;
                    var modalBoundary = secondPageRunning && status.Paired && status.Connected
                        && status.HostState == "busy" && status.ModalState == "running"
                        && status.LastHeartbeatAt is { } crashObservedAt
                        && DateTimeOffset.UtcNow - crashObservedAt < TimeSpan.FromMilliseconds(250)
                        && secondExecution?.State == "running" && secondExecution.EventSeq == status.LastEventSeq
                        && copies > 0 && parts.Length == 0;
                    var photoshopTerminationBoundary = modalBoundary && (terminatePhotoshop is null
                        || EnumerateFormalOutputFiles(root).Length == 4);
                    var reached = crashAt switch
                    {
                        "commit" => current.Pages[0].Status == RenderBatchPageStatus.Running && copies > 0
                            && bridge?.Executions.SingleOrDefault(item => item.ExecutionId == current.Pages[0].ExecutionId)?.State == "completed",
                        "output" => secondPageRunning && parts.Any(length => length > 0),
                        "modal" => modalBoundary,
                        _ => false
                    };
                    if (reached)
                    {
                        await new AtomicFileStore(paths).WriteAtomicallyAsync("controlled-process-exit.json", JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            boundary = crashAt, batch_id = batchId, revision = current.Revision,
                            statuses = current.Pages.Select(page => page.Status.ToString()),
                            execution_ids = bridge!.Executions.Select(item => item.ExecutionId),
                            authenticated_modal_heartbeat = crashAt == "modal" ? new {
                                host_state = status.HostState, modal_state = status.ModalState,
                                event_seq = status.LastEventSeq, heartbeat = status.Heartbeats,
                                observed_at = status.LastHeartbeatAt, execution_id = secondExecution!.ExecutionId
                            } : null,
                            remaining_resource_copies = copies, partial_psd_lengths = parts, created_at = DateTimeOffset.UtcNow
                        }), CancellationToken.None);
                        Console.WriteLine("CONTROLLED_PROCESS_EXIT boundary=" + crashAt);
                        // Deliberately bypasses managed cleanup only in this isolated, explicitly selected probe process.
                        Environment.Exit(86);
                    }
                    if (terminatePhotoshop is not null && !photoshopTerminationAttempted && photoshopTerminationBoundary)
                    {
                        photoshopTerminationAttempted = true;
                        if (!TryOpenVerifiedPhotoshopTarget(terminatePhotoshop, out var targetProcess, out photoshopTargetRejection))
                        {
                            Console.WriteLine("PHOTOSHOP_TARGET_REJECTED_AT_BOUNDARY reason=" + photoshopTargetRejection);
                        }
                        else
                        {
                            var terminationRequestedAt = DateTimeOffset.UtcNow;
                            int? exitCode = null;
                            var killIssued = false;
                            var processExited = false;
                            using (targetProcess)
                            {
                                try
                                {
                                    // The verified process handle identifies one PID. Never terminate by name or recurse into a process tree.
                                    targetProcess.Kill(entireProcessTree: false);
                                    killIssued = true;
                                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                    await targetProcess.WaitForExitAsync(exitTimeout.Token);
                                    processExited = targetProcess.HasExited;
                                    if (processExited) exitCode = targetProcess.ExitCode;
                                }
                                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
                                    or NotSupportedException or OperationCanceledException)
                                {
                                    if (killIssued)
                                    {
                                        try
                                        {
                                            processExited = targetProcess.HasExited;
                                            if (processExited) exitCode = targetProcess.ExitCode;
                                        }
                                        catch { processExited = false; }
                                    }
                                    photoshopTargetRejection = processExited ? null : "termination_not_confirmed";
                                }
                            }
                            if (killIssued && processExited)
                            {
                                var exitedAt = DateTimeOffset.UtcNow;
                                var formalOutputs = await CaptureFormalOutputSnapshotAsync(root, timeout.Token);
                                photoshopTermination = new(terminationRequestedAt, exitedAt, exitCode,
                                    current.Revision, status.Heartbeats, status.LastEventSeq, secondExecution!.ExecutionId,
                                    current.Pages.Select(page => page.ExecutionId).ToArray(),
                                    bridge!.Executions.Select(item => item.ExecutionId).ToArray(), copies, parts, formalOutputs);
                                await files.WriteAtomicallyAsync(HostInterruptionEvidenceFile, JsonSerializer.SerializeToUtf8Bytes(new
                                {
                                    boundary = "modal", batch_id = batchId, revision = current.Revision,
                                    statuses = current.Pages.Select(page => page.Status.ToString()),
                                    page_execution_ids = photoshopTermination.PageExecutionIds,
                                    bridge_execution_ids = photoshopTermination.BridgeExecutionIds,
                                    authenticated_modal_heartbeat = new {
                                        host_state = status.HostState, modal_state = status.ModalState,
                                        event_seq = status.LastEventSeq, heartbeat = status.Heartbeats,
                                        observed_at = status.LastHeartbeatAt, execution_id = secondExecution.ExecutionId,
                                        supported_photoshop_build = BridgeSessionOwnerFactory.SupportedPhotoshopBuild
                                    },
                                    process_identity = new {
                                        process_id = terminatePhotoshop.ProcessId, executable_name = "Photoshop.exe",
                                        executable_path_sha256 = terminatePhotoshop.ExecutablePathSha256,
                                        start_time_utc_ticks = terminatePhotoshop.ExpectedStartTimeUtcTicks,
                                        executable_file_version = terminatePhotoshop.ExecutableVersion,
                                        exact_path_match = true, exact_start_time_match = true, supported_build_match = true
                                    },
                                    termination = new {
                                        target_only = true, process_tree = false, kill_issued = true,
                                        process_exited = true, exit_code = exitCode,
                                        requested_at = terminationRequestedAt, exited_at = exitedAt
                                    },
                                    remaining_resource_copies = copies, partial_psd_lengths = parts,
                                    formal_outputs = new {
                                        file_count = formalOutputs.FileCount, total_bytes = formalOutputs.TotalBytes,
                                        aggregate_sha256 = formalOutputs.AggregateSha256, captured_at = formalOutputs.CapturedAt
                                    },
                                    created_at = DateTimeOffset.UtcNow
                                }), CancellationToken.None);
                                Console.WriteLine("CONTROLLED_PHOTOSHOP_EXIT boundary=modal target_only=true process_tree=false");
                            }
                            else
                            {
                                Console.WriteLine("PHOTOSHOP_TERMINATION_NOT_CONFIRMED reason=" + photoshopTargetRejection);
                            }
                        }
                    }
                }
                if (terminatePhotoshop is not null && photoshopTerminationAttempted && hostDisconnectedAt is null
                    && heartbeats > 0 && !status.Connected)
                {
                    hostDisconnectedAt = DateTimeOffset.UtcNow;
                    hostDisconnectedStatus = status;
                    Console.WriteLine("PHOTOSHOP_HOST_DISCONNECTED");
                }
                if (cancelAt == "modal" && cancelModalHeartbeat is null && current!.Pages[0].Status == RenderBatchPageStatus.Running
                    && status.Connected && status.HostState == "busy" && status.ModalState == "running"
                    && status.LastHeartbeatAt is { } observedAt && DateTimeOffset.UtcNow - observedAt < TimeSpan.FromMilliseconds(250))
                {
                    var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(batchId)));
                    var journal = await new FileBridgeExecutionJournalStore(paths, $"render/tasks/{key}/bridge-executions-v1.json").LoadAsync(timeout.Token);
                    var record = journal?.Executions.SingleOrDefault(item => item.ExecutionId == current.Pages[0].ExecutionId);
                    if (record?.State == "running" && record.EventSeq == status.LastEventSeq)
                    {
                        cancelModalHeartbeat = status;
                        modalCancelRequestedAt = DateTimeOffset.UtcNow;
                        await workflow.RequestCancelAsync(batchId, timeout.Token);
                        Console.WriteLine("CANCEL_REQUESTED_AFTER_AUTHENTICATED_MODAL_HEARTBEAT");
                    }
                }
                if (cancelAt == "resource" && !cancelDuringResource && current!.Pages[0].Status == RenderBatchPageStatus.Running)
                {
                    var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(batchId)));
                    var journal = await new FileBridgeExecutionJournalStore(paths, $"render/tasks/{key}/bridge-executions-v1.json").LoadAsync(timeout.Token);
                    if (journal?.Executions.SingleOrDefault(item => item.ExecutionId == current.Pages[0].ExecutionId)?.State == "authorized")
                    {
                        cancelDuringResource = true;
                        await workflow.RequestCancelAsync(batchId, timeout.Token);
                        Console.WriteLine("CANCEL_REQUESTED_DURING_RESOURCE");
                    }
                }
                if (cancelAt == "output" && cancelAfterBytes is null && current!.Pages[0].Status == RenderBatchPageStatus.Running)
                {
                    // Observe only this new plan's exact staging files, then use the production control port.
                    using var plan = JsonDocument.Parse(current.Pages[0].PlanJson!);
                    static string Component(string value) => "id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
                    var stage = Path.Combine(root, "outputs", "rendered", Component(batchId), ".app-staging", Component(batchId),
                        Component(current.Pages[0].StyleId + "/" + current.Pages[0].PageId),
                        plan.RootElement.GetProperty("plan_digest").GetString()![7..]);
                    var partial = new FileInfo(Path.Combine(stage, "editable_psd.part"));
                    if (partial.Exists && partial.Length > 0)
                    {
                        cancelAfterBytes = partial.Length;
                        await workflow.RequestCancelAsync(batchId, timeout.Token);
                        Console.WriteLine("CANCEL_REQUESTED_DURING_OUTPUT");
                    }
                }
                var state = string.Join(',', current!.Pages.Select(page => page.Status.ToString()));
                if (state != previous) { Console.WriteLine("BATCH_PAGES " + state); previous = state; }
                await Task.Delay(cancelAt is not null || crashAt is not null || recoverInterrupted
                    || terminatePhotoshop is not null || recoverHostInterrupted ? 25 : 500, timeout.Token);
            }
            var result = await running;
            var terminalBridgeStatus = await backend.GetStatusAsync(CancellationToken.None);
            transportFault = terminalBridgeStatus.TransportFault ?? transportFault;
            bindingFault = terminalBridgeStatus.BindingFault ?? bindingFault;
            if (terminatePhotoshop is not null && photoshopTermination is not null && hostDisconnectedAt is null
                && !terminalBridgeStatus.Connected && terminalBridgeStatus.TransportFault?.Stage == "heartbeat_timeout")
            {
                hostDisconnectedAt = DateTimeOffset.UtcNow;
                hostDisconnectedStatus = terminalBridgeStatus;
            }
            var projection = await new TaskCoordinator(store).GetProjectionAsync(batchId);
            var resourceDirectory = Path.Combine(root, "outputs", ".app-staging", "resources");
            var remainingResourceCopies = Directory.Exists(resourceDirectory)
                ? Directory.EnumerateFiles(resourceDirectory, "*", SearchOption.AllDirectories).Count() : 0;
            var renderedDirectory = Path.Combine(root, "outputs", "rendered");
            var remainingOutputParts = Directory.Exists(renderedDirectory)
                ? Directory.EnumerateFiles(renderedDirectory, "*.part", SearchOption.AllDirectories).Count() : 0;
            if (outputLock is not null)
                await files.WriteAtomicallyAsync("controlled-output-lock.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    batch_id = batchId, boundary = "verified_before_directory_publish", armed = outputLock.WasArmed,
                    completion_reached = outputLock.CompletionReached, lock_held_at_observation = outputLock.IsLockHeld,
                    publication_failure_hresult = outputLock.PublicationFailureHResult,
                    staged_manifest_relative_path = outputLock.StagedManifestRelativePath,
                    checkpoint = outputLock.TargetCheckpoint, status = result.Status.ToString(),
                    statuses = result.Pages.Select(page => page.Status.ToString()),
                    output_count = projection.OutputCount, authenticated_heartbeats = heartbeats,
                    remaining_resource_copies = remainingResourceCopies, remaining_output_parts = remainingOutputParts,
                    created_at = DateTimeOffset.UtcNow
                }), CancellationToken.None);
            var expectedFormalOutputs = photoshopTermination?.FormalOutputs ?? hostRecovery?.FormalOutputs;
            FormalOutputSnapshot? finalFormalOutputs = null;
            bool? formalOutputsUnchanged = null;
            if (expectedFormalOutputs is not null)
            {
                var finalSnapshot = await CaptureFormalOutputSnapshotAsync(root, CancellationToken.None);
                finalFormalOutputs = finalSnapshot;
                formalOutputsUnchanged = expectedFormalOutputs.Matches(finalSnapshot);
            }
            var expectedBridgeExecutionIds = photoshopTermination?.BridgeExecutionIds ?? hostRecovery?.BridgeExecutionIds;
            var finalBridge = expectedBridgeExecutionIds is null ? null : await bridgeJournal.LoadAsync(CancellationToken.None);
            bool? bridgeExecutionIdsUnchanged = expectedBridgeExecutionIds is null ? null
                : finalBridge is not null && expectedBridgeExecutionIds.SequenceEqual(
                    finalBridge.Executions.Select(item => item.ExecutionId), StringComparer.Ordinal);
            var expectedPageExecutionIds = photoshopTermination?.PageExecutionIds ?? hostRecovery?.PageExecutionIds;
            bool? pageExecutionIdsUnchanged = expectedPageExecutionIds is null ? null
                : expectedPageExecutionIds.SequenceEqual(result.Pages.Select(page => page.ExecutionId), StringComparer.Ordinal);
            var modalExecutionId = photoshopTermination?.ModalExecutionId ?? hostRecovery?.ModalExecutionId;
            bool? bridgeExecutionUnknown = modalExecutionId is null ? null
                : finalBridge?.Executions.SingleOrDefault(item => item.ExecutionId == modalExecutionId)?.State == "unknown";
            var hostIdentity = terminatePhotoshop is not null
                ? new ProcessIdentityEvidence(terminatePhotoshop.ProcessId, "Photoshop.exe",
                    terminatePhotoshop.ExecutablePathSha256, terminatePhotoshop.ExpectedStartTimeUtcTicks,
                    terminatePhotoshop.ExecutableVersion)
                : hostRecovery?.Identity;
            var evidence = new { status = result.Status.ToString(), authenticated_heartbeats = heartbeats, transport_fault = transportFault,
                binding_fault = bindingFault,
                cancel_after_output_bytes = cancelAfterBytes,
                cancel_during_resource = cancelDuringResource,
                cancel_modal_heartbeat = cancelModalHeartbeat is null ? null : new {
                    host_state = cancelModalHeartbeat.HostState, modal_state = cancelModalHeartbeat.ModalState,
                    heartbeat = cancelModalHeartbeat.Heartbeats, event_seq = cancelModalHeartbeat.LastEventSeq,
                    observed_at = cancelModalHeartbeat.LastHeartbeatAt, requested_at = modalCancelRequestedAt },
                remaining_resource_copies = remainingResourceCopies,
                remaining_output_parts = remainingOutputParts,
                output_count = projection.OutputCount, recovering = !liveExecution,
                pages = result.Pages.Select(page => new { page.PageId, page.OutputVariantId, page.Attempt,
                    status = page.Status.ToString(), page.ReasonCode, page.ExecutionId, page.Commit }),
                photoshop_host_interruption = hostIdentity is null ? null : new {
                    mode = terminatePhotoshop is not null ? "live_termination" : "independent_recovery",
                    boundary = "modal", termination_confirmed = photoshopTermination is not null || hostRecovery is not null,
                    target_rejection = photoshopTargetRejection,
                    process_identity = new {
                        process_id = hostIdentity.ProcessId, executable_name = hostIdentity.ExecutableName,
                        executable_path_sha256 = hostIdentity.ExecutablePathSha256,
                        start_time_utc_ticks = hostIdentity.StartTimeUtcTicks,
                        executable_file_version = hostIdentity.ExecutableVersion,
                        full_path_recorded = false
                    },
                    target_only = true, process_tree = false,
                    live_disconnect_observed = hostDisconnectedAt is not null,
                    disconnected_at = hostDisconnectedAt,
                    disconnected_heartbeats = hostDisconnectedStatus?.Heartbeats,
                    production_disconnect_monitor_used = terminatePhotoshop is not null,
                    probe_timeout = timeout.IsCancellationRequested,
                    durable_cancel_requested = result.CancelRequested,
                    formal_outputs_unchanged = formalOutputsUnchanged,
                    bridge_execution_ids_unchanged = bridgeExecutionIdsUnchanged,
                    page_execution_ids_unchanged = pageExecutionIdsUnchanged,
                    bridge_execution_unknown = bridgeExecutionUnknown,
                    final_formal_outputs = finalFormalOutputs is null ? null : new {
                        file_count = finalFormalOutputs.FileCount, total_bytes = finalFormalOutputs.TotalBytes,
                        aggregate_sha256 = finalFormalOutputs.AggregateSha256, captured_at = finalFormalOutputs.CapturedAt
                    }
                },
                source_identity_probe = !mixedProduct && !completeStyle, mixed_product_probe = mixedProduct, complete_style_probe = completeStyle,
                real_color_accuracy_evidence = false, created_at = DateTimeOffset.UtcNow };
            await using var output = new FileStream(paths.ResolveOwnedPath($"batch-execution-{Guid.NewGuid():N}.json"), FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(output, evidence); output.Flush(true);
            Console.WriteLine($"BATCH_FINISHED status={result.Status} output_count={projection.OutputCount} heartbeats={heartbeats}");
            if (outputLock is not null) return outputLock.WasArmed && !outputLock.CompletionReached
                && outputLock.IsLockHeld && (outputLock.PublicationFailureHResult & 0xffff) is 5 or 32
                && result.Status == RenderBatchStatus.RecoveryRequired && !result.CancelRequested
                && result.Pages[0].Status == RenderBatchPageStatus.Committed && result.Pages[1].Status == RenderBatchPageStatus.Unknown
                && result.Pages[2].Status == RenderBatchPageStatus.Planned && result.Pages[2].ExecutionId is null
                && projection.OutputCount == 1 && EnumerateFormalOutputFiles(root).Length == 4 ? 0 : 1;
            if (recoverOutputLock) return heartbeats == 0 && result.Status == RenderBatchStatus.Paused
                && result.Pages[0].Status == RenderBatchPageStatus.Committed && result.Pages[1].Status == RenderBatchPageStatus.Committed
                && result.Pages[2].Status == RenderBatchPageStatus.Planned && result.Pages[2].ExecutionId is null
                && projection.OutputCount == 2 && remainingResourceCopies == 0 && remainingOutputParts == 0
                && EnumerateFormalOutputFiles(root).Length == 8 ? 0 : 1;
            if (crashAt is not null) return 1; // A missed boundary is not a successful interruption test.
            if (terminatePhotoshop is not null) return photoshopTermination is not null && photoshopTargetRejection is null
                && photoshopTermination.FormalOutputs.FileCount == 4
                && hostDisconnectedAt is not null && hostDisconnectedStatus is { Connected: false }
                && !timeout.IsCancellationRequested && !result.CancelRequested
                && result.Status == RenderBatchStatus.RecoveryRequired && result.Pages.Count == 3
                && result.Pages[0].Status == RenderBatchPageStatus.Committed
                && result.Pages[1].Status == RenderBatchPageStatus.Unknown
                && result.Pages[1].ReasonCode == "RENDER_RECOVERY_REQUIRED"
                && result.Pages[2].Status == RenderBatchPageStatus.Planned && result.Pages[2].ExecutionId is null
                && projection.OutputCount == 1 && remainingResourceCopies > 0 && remainingOutputParts == 0
                && transportFault?.Stage == "heartbeat_timeout" && bridgeExecutionUnknown == true
                && formalOutputsUnchanged == true && bridgeExecutionIdsUnchanged == true
                && pageExecutionIdsUnchanged == true ? 0 : 1;
            if (recoverHostInterrupted) return hostRecovery is not null && heartbeats == 0
                && !timeout.IsCancellationRequested && !result.CancelRequested
                && result.Status == RenderBatchStatus.RecoveryRequired && result.Pages.Count == 3
                && result.Pages[0].Status == RenderBatchPageStatus.Committed
                && result.Pages[1].Status == RenderBatchPageStatus.Unknown
                && result.Pages[1].ReasonCode == "RENDER_RECOVERY_REQUIRED"
                && result.Pages[2].Status == RenderBatchPageStatus.Planned && result.Pages[2].ExecutionId is null
                && projection.OutputCount == 1 && remainingResourceCopies > 0 && remainingOutputParts == 0
                && formalOutputsUnchanged == true && bridgeExecutionIdsUnchanged == true
                && pageExecutionIdsUnchanged == true && bridgeExecutionUnknown == true ? 0 : 1;
            if (mixedProduct || completeStyle) return result.Status == RenderBatchStatus.Completed
                && result.Pages.Count == preparation.RootElement.GetProperty("expected_page_count").GetInt32()
                && result.Pages.All(page => page.Status == RenderBatchPageStatus.Committed)
                && projection.OutputCount == result.Pages.Count && remainingResourceCopies == 0 && remainingOutputParts == 0 ? 0 : 1;
            if (recoverInterrupted) return result.Pages[0].Status == RenderBatchPageStatus.Committed && projection.OutputCount == 1
                && heartbeats == 0 && (result.Status == RenderBatchStatus.Paused && remainingResourceCopies == 0 && remainingOutputParts == 0
                    || result.Status == RenderBatchStatus.RecoveryRequired && result.Pages[1].Status == RenderBatchPageStatus.Unknown
                        && remainingResourceCopies > 0
                        && (interruptedDuringModal ? remainingOutputParts == 0 : remainingOutputParts > 0)) ? 0 : 1;
            if (result.Version == 2) return result.Pages[^1].Status == RenderBatchPageStatus.Committed
                && result.Status is RenderBatchStatus.Completed or RenderBatchStatus.CompletedWithIssues
                && (!liveExecution || remainingResourceCopies == 0 && remainingOutputParts == 0) ? 0 : 1;
            if (cancelAt is not null) return (cancelAfterBytes > 0 || cancelDuringResource || cancelModalHeartbeat is not null)
                && result.Status == RenderBatchStatus.Cancelled
                && result.Pages[0].Status == RenderBatchPageStatus.Cancelled && projection.OutputCount == 0
                && remainingResourceCopies == 0 && remainingOutputParts == 0 ? 0 : 1;
            if (preparation.RootElement.TryGetProperty("dynamic_size_probe", out var sizeProbe) && sizeProbe.GetBoolean())
                return result.Status == RenderBatchStatus.Completed && result.Pages.Count == 1
                    && result.Pages[0].Status == RenderBatchPageStatus.Committed && projection.OutputCount == 1
                    && (!liveExecution || remainingResourceCopies == 0 && remainingOutputParts == 0) ? 0 : 1;
            return result.Status == RenderBatchStatus.CompletedWithIssues && projection.OutputCount == 2
                && result.Pages[2].Status == RenderBatchPageStatus.Failed
                && (!liveExecution || remainingResourceCopies == 0 && remainingOutputParts == 0) ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(System.Text.RegularExpressions.Regex.IsMatch(error.Message, "^(RENDER|BRIDGE|INPUT|TPL)_[A-Z_]+$")
                ? error.Message : "BATCH_PROBE_FAILED type=" + error.GetType().Name);
            return 1;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static bool TryOpenVerifiedPhotoshopTarget(PhotoshopTerminationTarget target,
        out Process process, out string? rejection)
    {
        process = null!;
        rejection = null;
        Process? candidate = null;
        try
        {
            if (!Path.IsPathFullyQualified(target.ExpectedExecutablePath)
                || !File.Exists(target.ExpectedExecutablePath))
            {
                rejection = "executable_unavailable";
                return false;
            }
            candidate = Process.GetProcessById(target.ProcessId);
            if (candidate.HasExited)
            {
                rejection = "process_exited";
                return false;
            }
            // Force one OS process handle to be acquired before checking identity and retain it through Kill(false).
            _ = candidate.Handle;
            var actualStartTimeUtcTicks = candidate.StartTime.ToUniversalTime().Ticks;
            var actualExecutablePath = candidate.MainModule?.FileName;
            if (actualStartTimeUtcTicks != target.ExpectedStartTimeUtcTicks)
            {
                rejection = "start_time_mismatch";
                return false;
            }
            if (string.IsNullOrWhiteSpace(actualExecutablePath)
                || !string.Equals(Path.GetFullPath(actualExecutablePath), target.ExpectedExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                rejection = "path_mismatch";
                return false;
            }
            var actualVersion = FileVersionInfo.GetVersionInfo(actualExecutablePath).FileVersion?.Trim();
            if (!string.Equals(actualVersion, SupportedPhotoshopExecutableVersion, StringComparison.Ordinal)
                || !string.Equals(actualVersion, target.ExecutableVersion, StringComparison.Ordinal))
            {
                rejection = "unsupported_build";
                return false;
            }
            process = candidate;
            candidate = null;
            return true;
        }
        catch (ArgumentException)
        {
            rejection = "process_not_found";
            return false;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            rejection = "identity_unavailable";
            return false;
        }
        finally { candidate?.Dispose(); }
    }

    private static async Task<FormalOutputSnapshot> CaptureFormalOutputSnapshotAsync(string root,
        CancellationToken cancellationToken)
    {
        var renderedRoot = Path.Combine(root, "outputs", "rendered");
        var capturedAt = DateTimeOffset.UtcNow;
        if (!Directory.Exists(renderedRoot))
            return new(0, 0, Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), capturedAt);
        var files = EnumerateFormalOutputFiles(root);
        long totalBytes = 0;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var contentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            totalBytes = checked(totalBytes + length);
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\0" + length + "\0" + contentHash + "\n"));
        }
        return new(files.Length, totalBytes, Convert.ToHexStringLower(aggregate.GetHashAndReset()), capturedAt);
    }

    private static FormalOutputFile[] EnumerateFormalOutputFiles(string root)
    {
        var renderedRoot = Path.Combine(root, "outputs", "rendered");
        if (!Directory.Exists(renderedRoot)) return [];
        return Directory.EnumerateFiles(renderedRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FormalOutputFile(path, Path.GetRelativePath(renderedRoot, path).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Contains(".app-staging", StringComparer.Ordinal)
                && !item.Relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !item.Relative.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadHostInterruptionEvidence(string path, string batchId,
        out HostRecoveryEvidence? evidence)
    {
        evidence = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var statuses = root.GetProperty("statuses").EnumerateArray().Select(item => item.GetString()).ToArray();
            var pageExecutionIds = root.GetProperty("page_execution_ids").EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Null ? null : item.GetString()).ToArray();
            var bridgeExecutionIds = root.GetProperty("bridge_execution_ids").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            var heartbeat = root.GetProperty("authenticated_modal_heartbeat");
            var identity = root.GetProperty("process_identity");
            var termination = root.GetProperty("termination");
            var formal = root.GetProperty("formal_outputs");
            var formalOutputs = new FormalOutputSnapshot(formal.GetProperty("file_count").GetInt32(),
                formal.GetProperty("total_bytes").GetInt64(), formal.GetProperty("aggregate_sha256").GetString()!,
                formal.GetProperty("captured_at").GetDateTimeOffset());
            var processIdentity = new ProcessIdentityEvidence(identity.GetProperty("process_id").GetInt32(),
                identity.GetProperty("executable_name").GetString()!,
                identity.GetProperty("executable_path_sha256").GetString()!,
                identity.GetProperty("start_time_utc_ticks").GetInt64(),
                identity.GetProperty("executable_file_version").GetString()!);
            var modalExecutionId = heartbeat.GetProperty("execution_id").GetString()!;
            if (root.GetProperty("boundary").GetString() != "modal"
                || root.GetProperty("batch_id").GetString() != batchId
                || !statuses.SequenceEqual(new[] { "Committed", "Running", "Planned" }, StringComparer.Ordinal)
                || pageExecutionIds.Length != 3 || string.IsNullOrWhiteSpace(pageExecutionIds[0])
                || string.IsNullOrWhiteSpace(pageExecutionIds[1]) || pageExecutionIds[2] is not null
                || bridgeExecutionIds.Length != 2 || !bridgeExecutionIds.SequenceEqual(pageExecutionIds.Take(2), StringComparer.Ordinal)
                || heartbeat.GetProperty("host_state").GetString() != "busy"
                || heartbeat.GetProperty("modal_state").GetString() != "running"
                || heartbeat.GetProperty("heartbeat").GetInt64() < 1
                || heartbeat.GetProperty("event_seq").GetInt32() < 1
                || heartbeat.GetProperty("supported_photoshop_build").GetString() != BridgeSessionOwnerFactory.SupportedPhotoshopBuild
                || modalExecutionId != pageExecutionIds[1]
                || processIdentity.ProcessId <= 0 || processIdentity.ExecutableName != "Photoshop.exe"
                || !IsSha256(processIdentity.ExecutablePathSha256) || processIdentity.StartTimeUtcTicks <= 0
                || processIdentity.ExecutableVersion != SupportedPhotoshopExecutableVersion
                || !identity.GetProperty("exact_path_match").GetBoolean()
                || !identity.GetProperty("exact_start_time_match").GetBoolean()
                || !identity.GetProperty("supported_build_match").GetBoolean()
                || !termination.GetProperty("target_only").GetBoolean()
                || termination.GetProperty("process_tree").GetBoolean()
                || !termination.GetProperty("kill_issued").GetBoolean()
                || !termination.GetProperty("process_exited").GetBoolean()
                || root.GetProperty("remaining_resource_copies").GetInt32() < 1
                || root.GetProperty("partial_psd_lengths").GetArrayLength() != 0
                || formalOutputs.FileCount != 4 || formalOutputs.TotalBytes < 1 || !IsSha256(formalOutputs.AggregateSha256))
                return false;
            evidence = new(formalOutputs, pageExecutionIds, bridgeExecutionIds, modalExecutionId, processIdentity,
                heartbeat.GetProperty("heartbeat").GetInt64(), heartbeat.GetProperty("event_seq").GetInt32());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private sealed record FormalOutputSnapshot(int FileCount, long TotalBytes, string AggregateSha256,
        DateTimeOffset CapturedAt)
    {
        public bool Matches(FormalOutputSnapshot other) => FileCount == other.FileCount
            && TotalBytes == other.TotalBytes
            && string.Equals(AggregateSha256, other.AggregateSha256, StringComparison.Ordinal);
    }

    private sealed record FormalOutputFile(string Path, string Relative);

    private sealed record ProcessIdentityEvidence(int ProcessId, string ExecutableName,
        string ExecutablePathSha256, long StartTimeUtcTicks, string ExecutableVersion);

    private sealed record PhotoshopTerminationObservation(DateTimeOffset RequestedAt, DateTimeOffset ExitedAt,
        int? ExitCode, long BoundaryRevision, long BoundaryHeartbeats, int BoundaryEventSeq,
        string ModalExecutionId, string?[] PageExecutionIds, string[] BridgeExecutionIds,
        int ResourceCopies, long[] PartialPsdLengths, FormalOutputSnapshot FormalOutputs);

    private sealed record HostRecoveryEvidence(FormalOutputSnapshot FormalOutputs, string?[] PageExecutionIds,
        string[] BridgeExecutionIds, string ModalExecutionId, ProcessIdentityEvidence Identity,
        long BoundaryHeartbeats, int BoundaryEventSeq);

    private sealed class MemorySecret(byte[] value) : IBridgePairingSecretStore
    {
        public byte[]? Read() => value.ToArray();
        public void Save(ReadOnlySpan<byte> secret) => throw new InvalidOperationException("BRIDGE_PROBE_SECRET_IMMUTABLE");
    }
}

internal sealed class PhotoshopTerminationTarget(int processId, string expectedExecutablePath,
    long expectedStartTimeUtcTicks, string executablePathSha256, string executableVersion)
{
    public int ProcessId { get; } = processId;
    internal string ExpectedExecutablePath { get; } = expectedExecutablePath;
    public long ExpectedStartTimeUtcTicks { get; } = expectedStartTimeUtcTicks;
    public string ExecutablePathSha256 { get; } = executablePathSha256;
    public string ExecutableVersion { get; } = executableVersion;
    public override string ToString() => nameof(PhotoshopTerminationTarget);
}
