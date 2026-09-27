using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Application.Psd;
using Desktop.Infrastructure.Psd;
using System.IO.Pipes;
using Desktop.Infrastructure.Persistence;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;

// Real-client mismatch check with an ephemeral server value. Neither endpoint's saved pairing is changed.
if (args.Length == 2 && args[0] == "--probe-pairing-mismatch")
{
    if (!Path.IsPathFullyQualified(args[1])) return 2;
    var mismatchPaths = new LocalAppPathPolicy(args[1]);
    if (File.Exists(mismatchPaths.ResolveOwnedPath("pairing-mismatch.json"))) return 2;
    var savedPairing = new WindowsBridgePairingStore();
    var original = savedPairing.Read();
    var mismatchSecret = RandomNumberGenerator.GetBytes(32);
    try
    {
        if (original is not { Length: 32 } || CryptographicOperations.FixedTimeEquals(original, mismatchSecret)) return 2;
        await using var mismatchOwner = new BridgeSessionOwner(mismatchSecret,
            new("pairing_mismatch_probe", "0.1.0", BridgeSessionOwnerFactory.SupportedPhotoshopBuild, [BridgeAdapterProtocol.Capability]),
            new FileBridgeExecutionJournalStore(mismatchPaths), TimeProvider.System);
        await mismatchOwner.StartAsync(default);
        Console.WriteLine("LISTENING verify only after observing the real plugin rejection");
        if (await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(3)) != "verify") return 2;
        var status = await mismatchOwner.GetStatusAsync(default);
        var executions = await mismatchOwner.ListExecutionsAsync(default);
        var after = savedPairing.Read();
        bool unchanged;
        try { unchanged = after is { Length: 32 } && CryptographicOperations.FixedTimeEquals(original, after); }
        finally { if (after is not null) CryptographicOperations.ZeroMemory(after); }
        var passed = !status.Paired && !status.Connected && status.Heartbeats == 0 && executions.Count == 0 && unchanged;
        await new AtomicFileStore(mismatchPaths).WriteAtomicallyAsync("pairing-mismatch.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            passed, paired = status.Paired, authenticated_heartbeats = status.Heartbeats, execution_count = executions.Count,
            existing_protected_pairing_unchanged = unchanged, saved_or_rotated_pairing = false, created_at = DateTimeOffset.UtcNow
        }), default);
        Console.WriteLine(passed ? "PAIRING_MISMATCH_BLOCKED" : "PAIRING_MISMATCH_PROBE_FAILED");
        return passed ? 0 : 1;
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
    { Console.WriteLine("PAIRING_MISMATCH_PROBE_FAILED"); return 1; }
    finally
    {
        CryptographicOperations.ZeroMemory(mismatchSecret);
        if (original is not null) CryptographicOperations.ZeroMemory(original);
    }
}

// Observe a real host restart at an idle boundary; no document or pairing mutation is issued here.
if (args.Length == 2 && args[0] == "--probe-photoshop-restart")
{
    if (!Path.IsPathFullyQualified(args[1])) return 2;
    var restartPaths = new LocalAppPathPolicy(args[1]);
    if (File.Exists(restartPaths.ResolveOwnedPath("photoshop-restart.json"))) return 2;
    var pairing = new WindowsBridgePairingStore();
    var before = pairing.Read();
    try
    {
        if (before is not { Length: 32 }) return 2;
        await using var factory = new BridgeSessionOwnerFactory(pairing, restartPaths, TimeProvider.System);
        await using var restartOwner = factory.CreateTaskOwner("photoshop_restart_probe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await restartOwner.StartAsync(timeout.Token);
        Console.WriteLine("LISTENING idle Photoshop restart probe");
        DateTimeOffset? firstReady = null, disconnected = null;
        long initialHeartbeats = 0;
        while (true)
        {
            var status = await restartOwner.GetStatusAsync(timeout.Token);
            if (firstReady is null && status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3)
            {
                firstReady = DateTimeOffset.UtcNow;
                initialHeartbeats = status.Heartbeats;
                Console.WriteLine("READY_FOR_IDLE_PHOTOSHOP_RESTART");
            }
            else if (firstReady is not null && disconnected is null && !status.Connected && !status.Ready)
            {
                disconnected = DateTimeOffset.UtcNow;
                Console.WriteLine("HOST_DISCONNECTED re-open Photoshop and connect the existing plugin");
            }
            else if (disconnected is not null && status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3)
            {
                var executions = await restartOwner.ListExecutionsAsync(timeout.Token);
                var after = pairing.Read();
                bool unchanged;
                try { unchanged = after is { Length: 32 } && CryptographicOperations.FixedTimeEquals(before, after); }
                finally { if (after is not null) CryptographicOperations.ZeroMemory(after); }
                var passed = executions.Count == 0 && unchanged;
                await new AtomicFileStore(restartPaths).WriteAtomicallyAsync("photoshop-restart.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    passed, boundary = "idle_no_documents", initial_ready_at = firstReady, disconnected_at = disconnected,
                    reconnected_at = DateTimeOffset.UtcNow, initial_heartbeats = initialHeartbeats,
                    reconnected_heartbeats = status.Heartbeats, execution_count = executions.Count,
                    existing_protected_pairing_unchanged = unchanged
                }), timeout.Token);
                Console.WriteLine(passed ? "PHOTOSHOP_RESTART_RECONNECTED" : "PHOTOSHOP_RESTART_PROBE_FAILED");
                return passed ? 0 : 1;
            }
            await Task.Delay(250, timeout.Token);
        }
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
    { Console.WriteLine("PHOTOSHOP_RESTART_PROBE_FAILED"); return 1; }
    finally { if (before is not null) CryptographicOperations.ZeroMemory(before); }
}

if (args.Length == 4 && args[0] is "--run-render-batch" or "--cancel-output-batch" or "--cancel-resource-batch" or "--cancel-modal-batch")
    return await BatchExecutionProbe.RunAsync(args[2], args[3], args[1],
        args[0] == "--cancel-output-batch" ? "output" : args[0] == "--cancel-resource-batch" ? "resource"
            : args[0] == "--cancel-modal-batch" ? "modal" : null);
if (args.Length == 3 && args[0] == "--recover-render-batch")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null);
if (args.Length == 3 && args[0] == "--run-paired-render-batch-performance")
{
    if (!Path.IsPathFullyQualified(args[1])) return 2;
    RenderPerformanceCapture capture;
    try { capture = RenderPerformanceCapture.Start(args[1]); }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("PERFORMANCE_CAPTURE_ROOT_INVALID");
        return 2;
    }
    using (capture)
        return await BatchExecutionProbe.RunAsync(args[1], args[2], null, pairedDevice: true);
}
if (args.Length == 3 && args[0] == "--run-paired-render-batch")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null, pairedDevice: true);
if (args.Length == 3 && args[0] is "--lock-second-output-commit" or "--recover-output-locked-batch")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null,
        pairedDevice: args[0] == "--lock-second-output-commit",
        outputLockMode: args[0] == "--lock-second-output-commit" ? "lock" : "recover");
if (args.Length == 3 && args[0] is "--crash-after-bridge-commit" or "--crash-during-second-output" or "--crash-during-second-modal")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null, pairedDevice: true,
        crashAt: args[0] == "--crash-after-bridge-commit" ? "commit" : args[0] == "--crash-during-second-output" ? "output" : "modal");
if (args.Length == 3 && args[0] == "--recover-interrupted-batch")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null, recoverInterrupted: true);
if (args.Length == 6 && args[0] == "--terminate-photoshop-during-second-modal")
{
    PhotoshopTerminationTarget? photoshopTarget = null;
    string? rejection = null;
    if (!int.TryParse(args[3], out var photoshopProcessId)
        || !long.TryParse(args[5], out var photoshopStartTimeUtcTicks)
        || !BatchExecutionProbe.TryCreatePhotoshopTerminationTarget(photoshopProcessId, args[4],
            photoshopStartTimeUtcTicks, out photoshopTarget, out rejection))
    {
        Console.WriteLine("PHOTOSHOP_TARGET_REJECTED reason=" + (rejection ?? "invalid_parameters"));
        return 2;
    }
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null, pairedDevice: true,
        terminatePhotoshop: photoshopTarget);
}
if (args.Length == 3 && args[0] == "--recover-host-interrupted-batch")
    return await BatchExecutionProbe.RunAsync(args[1], args[2], null, recoverHostInterrupted: true);
if (args.Length == 8 && args[0] == "--regenerate-batch-page" && int.TryParse(args[7], out var regenerateAttempt))
    return await BatchExecutionProbe.RunAsync(args[2], args[3], args[1],
        regenerate: (args[4], args[5], args[6] == "-" ? null : args[6], regenerateAttempt));

if (args.Length == 4 && args[0] is "--prepare-complete-style-batch" or "--prepare-mixed-product-batch" or "--prepare-size-page" or "--prepare-calibrated-size-page" or "--prepare-size-batch" or "--prepare-ordinary-page" or "--prepare-ordinary-batch" or "--prepare-style-boundary-batch")
{
    try
    {
        var preparationPaths = new LocalAppPathPolicy(args[1]);
        if (args[0] == "--prepare-complete-style-batch")
            await CompleteStylePreparation.PrepareAsync(preparationPaths, args[2], CreateWorker(args[3], preparationPaths));
        else if (args[0] == "--prepare-mixed-product-batch")
            await MixedProductPreparation.PrepareAsync(preparationPaths, args[2], CreateWorker(args[3], preparationPaths));
        else if (args[0] is "--prepare-ordinary-page" or "--prepare-ordinary-batch" or "--prepare-style-boundary-batch")
            await OrdinaryPagePreparation.PrepareAsync(preparationPaths, args[2], CreateWorker(args[3], preparationPaths),
                args[0] != "--prepare-ordinary-page", args[0] == "--prepare-style-boundary-batch");
        else await SizePagePreparation.PrepareAsync(preparationPaths, args[2], CreateWorker(args[3], preparationPaths),
            persistCalibration: args[0] is "--prepare-calibrated-size-page" or "--prepare-size-batch",
            batchProbe: args[0] == "--prepare-size-batch");
        Console.WriteLine("PAGE_PREPARED candidate=true photoshop_executed=false");
        return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine(System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"^(SIZE|RENDER|TPL|AI|INPUT|PATH)_[A-Z_]+$")
            ? error.Message : $"PAGE_PREPARATION_FAILED type={error.GetType().Name}");
        return 1;
    }
}

if (args.Length == 3 && args[0] == "--reconnect-device")
{
    if (!Path.IsPathFullyQualified(args[1])
        || !System.Text.RegularExpressions.Regex.IsMatch(args[2], @"^RENDER-01-DEVICE-PROBE-[a-f0-9]{32}$")) return 2;
    try
    {
        var reconnectPaths = new LocalAppPathPolicy(args[1]);
        await using var factory = new BridgeSessionOwnerFactory(new WindowsBridgePairingStore(args[2]), reconnectPaths, TimeProvider.System);
        await using var reconnectOwner = factory.CreateTaskOwner("render_device_reconnect");
        using var reconnectStop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await reconnectOwner.StartAsync(reconnectStop.Token);
        Console.WriteLine("LISTENING");
        while (!reconnectStop.IsCancellationRequested)
        {
            var status = await reconnectOwner.GetStatusAsync(reconnectStop.Token);
            if (status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3)
            {
                await new AtomicFileStore(reconnectPaths).WriteAtomicallyAsync($"device-reconnect-{Guid.NewGuid():N}.json",
                    JsonSerializer.SerializeToUtf8Bytes(new { paired = true, authenticated_ready_heartbeats = status.Heartbeats,
                        persisted_device_reused = true, new_session = true, output_executed = false,
                        created_at = DateTimeOffset.UtcNow }), reconnectStop.Token);
                Console.WriteLine("DEVICE_RECONNECTED protected_storage=true new_session=true");
                return 0;
            }
            await Task.Delay(250, reconnectStop.Token);
        }
    }
    catch { Console.WriteLine("DEVICE_RECONNECT_FAILED"); }
    return 1;
}

// Exercise the production pairing coordinator with an isolated protected credential.
// Its fresh introduction code travels only through a current-user memory pipe.
if (args.Length == 5 && args[0] == "--pair-device" && args[1] == "--pairing-pipe")
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(args[2], @"^render_probe_[a-f0-9]{32}$")
        || !Path.IsPathFullyQualified(args[3])
        || !System.Text.RegularExpressions.Regex.IsMatch(args[4], @"^RENDER-01-DEVICE-PROBE-[a-f0-9]{32}$")) return 2;
    byte[]? introduced = null;
    byte[]? stored = null;
    try
    {
        using var setupStop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var pipe = new NamedPipeServerStream(args[2], PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
        Console.WriteLine("PAIRING_CHANNEL_READY");
        await pipe.WaitForConnectionAsync(setupStop.Token);
        var setupPaths = new LocalAppPathPolicy(args[3]);
        var setupStore = new WindowsBridgePairingStore(args[4]);
        // This isolated test credential must never replace an earlier run.
        stored = setupStore.Read();
        if (stored is not null) return 2;
        await using var factory = new BridgeSessionOwnerFactory(setupStore, setupPaths, TimeProvider.System);
        await factory.PairAsync(async (bytes, token) =>
        {
            if (bytes.IsEmpty) return;
            introduced = bytes.ToArray();
            await pipe.WriteAsync(bytes, token);
            await pipe.FlushAsync(token);
            var receipt = new byte[1];
            await pipe.ReadExactlyAsync(receipt, token);
            if (receipt[0] != 1) throw new InvalidDataException("BRIDGE_PAIRING_REJECTED");
            pipe.Disconnect();
            Console.WriteLine("LISTENING");
        }, setupStop.Token);
        stored = setupStore.Read();
        if (stored is null || introduced is null || !CryptographicOperations.FixedTimeEquals(stored, introduced)) return 1;
        await new AtomicFileStore(setupPaths).WriteAtomicallyAsync("device-pairing-result.json",
            JsonSerializer.SerializeToUtf8Bytes(new { paired = true, authenticated_ready_heartbeats_minimum = 3,
                windows_protected_readback = true, photoshop_build = BridgeSessionOwnerFactory.SupportedPhotoshopBuild,
                created_at = DateTimeOffset.UtcNow }), default);
        Console.WriteLine("DEVICE_PAIRED protected_storage=true");
        return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine(System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"^BRIDGE_[A-Z_]+$")
            ? error.Message : "DEVICE_PAIRING_FAILED");
        return 1;
    }
    finally
    {
        if (introduced is not null) CryptographicOperations.ZeroMemory(introduced);
        if (stored is not null) CryptographicOperations.ZeroMemory(stored);
    }
}

var commitRequested = args.Length > 0 && args[0] == "--commit-page";
if (commitRequested) args = args[1..];
var failureExpected = args.Length > 0 && args[0] == "--expect-page-failure";
if (failureExpected) args = args[1..];

string? pairingPipe = null;
if (args.Length >= 2 && args[0] == "--pairing-pipe")
{
    pairingPipe = args[1];
    if (!System.Text.RegularExpressions.Regex.IsMatch(pairingPipe, @"^render_probe_[a-f0-9]{32}$")) return 2;
    args = args[2..];
}

if (args.Length == 3 && args[0] == "--recover-commit")
{
    try
    {
        var recoveryPaths = new LocalAppPathPolicy(args[1]);
        var recoveryJournal = new FileBridgeExecutionJournalStore(recoveryPaths);
        var previous = await recoveryJournal.LoadAsync(default);
        var record = previous!.Executions.Single();
        var schemas = new JsonSchemaContractValidator();
        var wire = new BridgeWireValidator(schemas);
        using var broker = new BridgeExecutionBroker(record.TaskId,
            new BridgePlanSubmissionValidator(wire, new RenderPlanValidator(schemas)), wire, recoveryJournal);
        await broker.InitializeAsync(default);
        await using var store = new SqliteDesktopStore(new(recoveryPaths.ResolveOwnedPath("state")));
        await store.InitializeAsync();
        var recovered = await broker.RecoverOutputAsync(record.ExecutionId, args[2], recoveryPaths, store, default);
        var state = await new TaskCoordinator(store).LoadAsync(record.TaskId);
        var result = new { recovered = recovered.Recovered,
            state = await store.ReadRenderCommitAsync(recovered.Checkpoint, default),
            execution_state = (await broker.QueryAsync(record.ExecutionId, default))!.State, output_count = state.OutputCount };
        var evidence = recoveryPaths.ResolveOwnedPath($"formal-recovery-{Guid.NewGuid():N}.json");
        await using var stream = new FileStream(evidence, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, result, BridgeProtocol.JsonOptions); stream.Flush(true);
        Console.WriteLine($"COMMIT_RECOVERED output_count={state.OutputCount} rendered=false");
        return 0;
    }
    catch { Console.WriteLine("COMMIT_RECOVERY_REQUIRED"); return 1; }
}

// Reuse the formal independent validator on retained real output without pairing,
// Photoshop input, or publishing a previously cancelled probe attempt.
// Recheck existing artifacts read-only with the current independent validator.
// Explicit input selection avoids accidentally using a superseded probe plan.
if (args.Length == 6 && args[0] == "--verify-artifact")
{
    try
    {
        var artifactPaths = new LocalAppPathPolicy(args[1]);
        var artifactPage = JsonSerializer.Deserialize<ProbePage>(await File.ReadAllTextAsync(
            artifactPaths.ResolveOwnedPath(args[2])), BridgeProtocol.JsonOptions)!;
        using var artifactManifest = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPaths.ResolveOwnedPath(args[3])));
        var manifest = artifactManifest.RootElement;
        var artifactReceipts = manifest.GetProperty("media").GetProperty("files").Deserialize<RenderMediaFile[]>(BridgeProtocol.JsonOptions)!;
        if (manifest.GetProperty("page").GetProperty("plan_digest").GetString()
            != artifactPage.Plan.GetProperty("plan_digest").GetString()) return 2;
        var directory = Path.GetDirectoryName(args[3])!;
        var artifactFiles = manifest.GetProperty("files").EnumerateArray().ToArray();
        var jpegPath = Path.Combine(directory, artifactFiles.Single(item => item.GetProperty("kind").GetString() == "jpg")
            .GetProperty("file_name").GetString()!);
        var psdEntry = artifactFiles.SingleOrDefault(item => item.GetProperty("kind").GetString() == "editable_psd");
        var psdPath = psdEntry.ValueKind == JsonValueKind.Undefined ? args[4]
            : Path.Combine(directory, psdEntry.GetProperty("file_name").GetString()!);
        var verifiedArtifact = await new RenderMediaValidator(artifactPaths, CreateWorker(args[5], artifactPaths)).ValidateAsync(
            artifactPage.Plan.GetRawText(), artifactPage.Sources.Single(item => item.Purpose == "template_copy").OwnedRelativePath,
            jpegPath, psdPath, artifactReceipts, CancellationToken.None);
        await new AtomicFileStore(artifactPaths).WriteAtomicallyAsync($"artifact-reverification-{Guid.NewGuid():N}.json",
            JsonSerializer.SerializeToUtf8Bytes(new { media = verifiedArtifact, text_style_evidence = "text-style/v1",
                rerendered = false, committed_again = false, created_at = DateTimeOffset.UtcNow }, BridgeProtocol.JsonOptions), default);
        Console.WriteLine($"ARTIFACT_REVERIFIED layers={verifiedArtifact.PsdLayerCount} text_layers={verifiedArtifact.EditableTextLayerCount} text_style=true rerendered=false");
        return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine(System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"^RENDER_[A-Z_]+(:TPL_[A-Z_]+)?$")
            ? error.Message : "ARTIFACT_REVERIFICATION_FAILED");
        return 1;
    }
}

if (args.Length == 5 && args[0] == "--verify-media")
{
    try
    {
    var verifyPaths = new LocalAppPathPolicy(args[1]);
    var verifyPage = JsonSerializer.Deserialize<ProbePage>(await File.ReadAllTextAsync(
        verifyPaths.ResolveOwnedPath("page-input.json")), BridgeProtocol.JsonOptions)!;
    var receiptBytes = await File.ReadAllTextAsync(verifyPaths.ResolveOwnedPath(args[3]));
    var receipts = JsonSerializer.Deserialize<RenderMediaFile[]>(receiptBytes, BridgeProtocol.JsonOptions)!;
    var verifyWorker = CreateWorker(args[4], verifyPaths);
    var verified = await new RenderMediaValidator(verifyPaths, verifyWorker).ValidateAsync(verifyPage.Plan.GetRawText(),
        verifyPage.Sources.Single(item => item.Purpose == "template_copy").OwnedRelativePath,
        Path.Combine(args[2], "jpg.part"), Path.Combine(args[2], "editable_psd.part"), receipts, CancellationToken.None);
    var evidencePath = verifyPaths.ResolveOwnedPath($"formal-media-verification-{Guid.NewGuid():N}.json");
    await using var evidenceFile = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(evidenceFile, verified, BridgeProtocol.JsonOptions);
    evidenceFile.Flush(true);
    Console.WriteLine($"MEDIA_VERIFIED layers={verified.PsdLayerCount} text_layers={verified.EditableTextLayerCount} published=false");
    return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine(System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"^RENDER_[A-Z_]+(:TPL_[A-Z_]+)?$")
            ? error.Message : "MEDIA_VERIFICATION_FAILED");
        return 1;
    }
}

// The last argument may explicitly select an existing packaged worker. This
// lets the same independent verifier check deployment bytes without installing
// or replacing the application's current worker. Source runs keep the pinned
// project Python entry point; no runtime fallback is introduced.
static PsdInspectionWorkerClient CreateWorker(string projectRootOrWorker, LocalAppPathPolicy paths)
{
    if (Path.IsPathFullyQualified(projectRootOrWorker)
        && string.Equals(Path.GetFileName(projectRootOrWorker), "psd-inspection-worker.exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(projectRootOrWorker))
        return new(paths, new(projectRootOrWorker, TimeSpan.FromMinutes(2), PsdInspectionWorkerOptions.DefaultMaxResponseBytes));
    return new(paths,
        new((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, "pwsh.exe")).First(File.Exists),
            TimeSpan.FromMinutes(2), PsdInspectionWorkerOptions.DefaultMaxResponseBytes,
            ["-NoProfile", "-File", Path.Combine(projectRootOrWorker, "tools", "project-python.ps1"),
                Path.Combine(projectRootOrWorker, "src", "PsdInspection.Worker", "worker.py")]));
}

if (args.Length is not (2 or 3 or 4) || !Path.IsPathFullyQualified(args[0])
    || commitRequested && args.Length != 4 || failureExpected && (commitRequested || args.Length != 3)) return 2;
var paths = new LocalAppPathPolicy(args[0]);
var files = new AtomicFileStore(paths);
ProbePage? page = null;
if (args.Length >= 3)
{
    // Reject malformed local evidence before receiving any pairing material.
    try
    {
        var path = paths.ResolveOwnedPath(args[2]);
        if (new FileInfo(path).Length > BridgeProtocol.PlanByteLimit) return 2;
        page = JsonSerializer.Deserialize<ProbePage>(await File.ReadAllTextAsync(path), BridgeProtocol.JsonOptions);
        if (page is null) return 2;
    }
    catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.WriteLine("PROBE_PAGE_INPUT_INVALID");
        return 2;
    }
}
// Caller sends fresh bytes over stdin or a current-user-only bounded memory pipe,
// never argv, environment or files. Console remains available for safe cancellation.
byte[] secret;
if (pairingPipe is null)
{
    var input = await Console.In.ReadLineAsync();
    if (input is null) return 2;
    try { secret = BridgeAdapterCryptography.Decode32(input); }
    catch (FormatException) { return 2; }
    input = null;
}
else
{
    using var pipeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await using var pipe = new NamedPipeServerStream(pairingPipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
    Console.WriteLine("PAIRING_CHANNEL_READY");
    await pipe.WaitForConnectionAsync(pipeTimeout.Token);
    secret = new byte[32];
    try
    {
        await pipe.ReadExactlyAsync(secret, pipeTimeout.Token);
        var extra = new byte[1];
        if (await pipe.ReadAsync(extra, pipeTimeout.Token) != 0) { CryptographicOperations.ZeroMemory(secret); return 2; }
    }
    catch { CryptographicOperations.ZeroMemory(secret); throw; }
}
var probeTaskId = page?.Plan.GetProperty("batch_id").GetString() ?? "render_live_probe";
BridgeSessionOwner owner;
try
{
    owner = new BridgeSessionOwner(secret,
        new(probeTaskId, "0.1.0", args[1], page is null ? [BridgeAdapterProtocol.Capability]
            : new[] { BridgeAdapterProtocol.Capability }.Concat(page.Plan.GetProperty("required_capabilities")
                .EnumerateArray().Select(item => item.GetString()!)).Distinct(StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow.AddMinutes(5)),
        new FileBridgeExecutionJournalStore(paths));
}
finally { CryptographicOperations.ZeroMemory(secret); }
await using var ownedSession = owner;
await using var commitStore = commitRequested ? new SqliteDesktopStore(new(paths.ResolveOwnedPath("state"))) : null;
if (commitStore is not null)
{
    await commitStore.InitializeAsync();
    var coordinator = new TaskCoordinator(commitStore);
    var sequence = 0;
    foreach (var type in new[] { TaskEventType.TaskCreated, TaskEventType.ReadyToRender, TaskEventType.RenderingStarted })
        await coordinator.RecordAsync(TaskEvent.Create(probeTaskId, ++sequence, type));
}
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var probeStage = "start";
try
{
    await owner.StartAsync(stop.Token);
    Console.WriteLine("LISTENING");
    var pairedAt = DateTimeOffset.MinValue;
    string? execution = null;
    // Console.In's synchronized reader may complete ReadLineAsync synchronously and block
    // this loop until input arrives. Keep the control reader off the status/submission loop.
    var cancelInput = page is null ? null : Task.Run(() => Console.In.ReadLine(), stop.Token);
    var cancelSent = false;
    var mediaValidated = false;
    var committedAt = DateTimeOffset.MinValue;
    var failedAt = DateTimeOffset.MinValue;
    long heartbeatsAtFailure = 0;
    while (!stop.IsCancellationRequested)
    {
        var status = await owner.GetStatusAsync(stop.Token);
        if (status.Paired && pairedAt == DateTimeOffset.MinValue)
        {
            pairedAt = DateTimeOffset.UtcNow;
            Console.WriteLine("PAIRED");
        }
        if (page is not null && status.Ready && status.Heartbeats >= 3 && execution is null)
        {
            var planJson = page.Plan.GetRawText();
            var reservation = BridgeOutputReservation.Create(planJson, "outputs", paths,
                new RenderPlanValidator(new JsonSchemaContractValidator()));
            var submitted = await owner.SubmitPageAsync(planJson, page.Sources, paths, reservation, page.TemplateEvidence, stop.Token);
            if (!submitted.IsSuccess) { reservation.Dispose(); Console.WriteLine("PROBE_FAILED"); return 1; }
            execution = submitted.Value!.ExecutionId!;
            Console.WriteLine("PLAN_ACCEPTED");
        }
        if (execution is not null && cancelInput!.IsCompleted && !cancelSent)
        {
            if (await cancelInput != "CANCEL") return 2;
            await owner.CancelAsync(execution, stop.Token);
            cancelSent = true;
            Console.WriteLine("CANCEL_REQUESTED");
        }
        var record = execution is null ? null : await owner.QueryAsync(execution, stop.Token);
        // A safe failure must remain authenticated and ready after its terminal ACK.
        // Do not accept unknown/cancelled or an unexpected successful page as evidence.
        var expectedFailure = failureExpected && record?.State == "failed"
            && record.Events.Count > 0
            && record.Events[^1] is { EventType: "page_failed", Error.Code: "BRIDGE_PLAN_INVALID" };
        if (expectedFailure && failedAt == DateTimeOffset.MinValue)
        {
            failedAt = DateTimeOffset.UtcNow;
            heartbeatsAtFailure = status.Heartbeats;
            Console.WriteLine("EXPECTED_PAGE_FAILED");
        }
        if (expectedFailure && (!status.Paired || !status.Connected || !status.Ready))
            heartbeatsAtFailure = status.Heartbeats;
        var postFailureReady = expectedFailure && status.Paired && status.Connected && status.Ready
            && status.Heartbeats - heartbeatsAtFailure >= 3;
        var waitingForFailureHeartbeats = expectedFailure && !postFailureReady
            && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromSeconds(30);
        if (args.Length == 4 && !mediaValidated && record?.State == "running" && !record.CancelRequested
            && record.Events[^1].EventType == "page_completed")
        {
            probeStage = "media_worker_configuration";
            var mediaWorker = CreateWorker(args[3], paths);
            probeStage = "media_validation";
            var media = await owner.ValidateOutputAsync(execution!, mediaWorker, stop.Token);
            probeStage = "media_evidence_write";
            await files.WriteAtomicallyAsync("formal-media-verification.json", JsonSerializer.SerializeToUtf8Bytes(media, BridgeProtocol.JsonOptions), stop.Token);
            mediaValidated = true;
            Console.WriteLine("MEDIA_VERIFIED");
            probeStage = "awaiting_commit_or_cancel";
            if (commitStore is not null)
            {
                probeStage = "page_commit";
                var committed = await owner.CommitOutputAsync(execution!, 1, commitStore, stop.Token);
                await files.WriteAtomicallyAsync("formal-commit-result.json", JsonSerializer.SerializeToUtf8Bytes(committed, BridgeProtocol.JsonOptions), stop.Token);
                committedAt = DateTimeOffset.UtcNow;
                Console.WriteLine("PAGE_COMMITTED");
            }
        }
        if (page is null && pairedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - pairedAt > TimeSpan.FromSeconds(20)
            || page is not null && (record?.State is "cancelled" or "failed" or "unknown") && !waitingForFailureHeartbeats
            || failureExpected && record?.Events.Any(item => item.EventType == "page_completed") == true
            || record?.State == "completed" && DateTimeOffset.UtcNow - committedAt > TimeSpan.FromSeconds(12))
        {
            var succeeded = status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3
                && (failureExpected ? postFailureReady
                    : page is null || record?.State == "cancelled" || record?.State == "completed" && committedAt != DateTimeOffset.MinValue);
            var result = new { schema_version = "render-bridge-probe/v1", paired = status.Paired,
                photoshop_build = args[1], transport = BridgeAdapterProtocol.Version, protocol = BridgeProtocol.Version,
                plugin_version = BridgeAdapterProtocol.PluginVersion, created_at = DateTimeOffset.UtcNow,
                authenticated_heartbeats = status.Heartbeats, connected = status.Connected,
                host_ready = status.Ready, output_executed = record?.Events.Any(item => item.EventType == "page_completed") == true,
                published = committedAt != DateTimeOffset.MinValue, media_validated = mediaValidated, page_state = record?.State,
                event_seq = record?.EventSeq, cancel_requested = cancelSent,
                expected_failure = failureExpected, confirmed_failure = postFailureReady,
                authenticated_ready_heartbeats_after_failure = failedAt == DateTimeOffset.MinValue ? 0 : status.Heartbeats - heartbeatsAtFailure };
            await files.WriteAtomicallyAsync("probe-result.json", JsonSerializer.SerializeToUtf8Bytes(result), stop.Token);
            Console.WriteLine("PROBE_FINISHED");
            return succeeded ? 0 : 1;
        }
        await Task.Delay(1000, stop.Token);
    }
}
catch (OperationCanceledException) { Console.WriteLine("PROBE_TIMEOUT"); }
catch (Exception error)
{
    await files.WriteAtomicallyAsync("probe-failure.json", JsonSerializer.SerializeToUtf8Bytes(new {
        stage = probeStage, exception_type = error.GetType().Name,
        code = System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"^(RENDER|BRIDGE)_[A-Z_]+(:TPL_[A-Z_]+)?$")
            ? error.Message : "PROBE_OPERATION_FAILED" }), CancellationToken.None);
    Console.WriteLine("PROBE_FAILED");
}
return 1;

internal sealed record ProbePage(JsonElement Plan, IReadOnlyList<BridgeResourceSource> Sources, PsdInspectionDocument TemplateEvidence);
