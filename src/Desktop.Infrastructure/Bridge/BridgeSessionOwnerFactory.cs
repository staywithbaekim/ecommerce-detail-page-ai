using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Creates the existing task owner from protected storage; setup never submits Photoshop work.</summary>
public sealed class BridgeSessionOwnerFactory(IBridgePairingSecretStore secrets, IPathPolicy paths,
    TimeProvider timeProvider, PhotoshopHostSelectionService? hostSelection = null)
    : IPhotoshopPairingPort, IPhotoshopConnectionCheckPort, IAsyncDisposable
{
    public const string SupportedPhotoshopBuild = "26.8.1 (20250624.r.8 5e0d05d)";
    public const string ProtectedPairingCapability = "pairing/protected-storage-v1";
    public static readonly TimeSpan ConnectionCheckWaitLimit = TimeSpan.FromSeconds(60);
    private static readonly string[] Capabilities = [BridgeAdapterProtocol.Capability, "psd-layer-path/psd-bottom-to-top",
        "smart-object/replace", "output/jpeg-srgb-icc", RenderPlanPlacementMigration.Capability,
        RenderOutputEvidenceValidator.VerificationPsdCapability, RenderPlanPlacementMigration.SizeTableCapability,
        RenderPlanPlacementMigration.SizeInsertionCapability, RenderPlanVariantIdentity.Capability];
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly object _operationSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeConnectionCheck;
    private bool _productionStartPending;
    private PhotoshopConnectionCheckResult? _latestConnectionCheck;
    private int _disposed;

    public PhotoshopConnectionCheckResult? Latest => Volatile.Read(ref _latestConnectionCheck);

    public Task<PhotoshopConnectionCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Credential Manager and host construction must not block the WPF dispatcher.
        return Task.Run(() => CheckCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task<PhotoshopConnectionCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (!TryBeginSetup(operation.Token, operation))
            return PublishCheck(new(PhotoshopConnectionCheckStatus.Busy, timeProvider.GetUtcNow()), operation.Token);
        try
        {
            var result = await CheckOwnerAsync(operation.Token).ConfigureAwait(false);
            // CheckOwnerAsync has disposed its listener/session before any result is published.
            return PublishCheck(result, operation.Token);
        }
        finally { EndSetup(); }
    }

    private bool TryBeginSetup(CancellationToken token, CancellationTokenSource? connectionCheck = null)
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            if (_productionStartPending || !_pairingGate.Wait(0, token)) return false;
            _activeConnectionCheck = connectionCheck;
            return true;
        }
    }

    private void EndSetup()
    {
        lock (_operationSync)
        {
            _activeConnectionCheck = null;
            _pairingGate.Release();
        }
    }

    /// <summary>Production startup has priority over a diagnostic check, never over another render or pairing.</summary>
    public async Task<BridgeSessionOwner> StartTaskOwnerAsync(string taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateTaskId(taskId);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        Task? cancelledCheck = null;
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            operation.Token.ThrowIfCancellationRequested();
            if (_productionStartPending) throw new InvalidOperationException("BRIDGE_BUSY");
            if (_activeConnectionCheck is null)
            {
                acquired = _pairingGate.Wait(0, operation.Token);
                if (!acquired) throw new InvalidOperationException("BRIDGE_BUSY");
            }
            _productionStartPending = true;
            // Mark cancellation while the CTS is protected from disposal. Callbacks run
            // outside this lock; the semaphore is released only after owner disposal.
            cancelledCheck = _activeConnectionCheck?.CancelAsync();
        }
        try
        {
            if (cancelledCheck is not null) await cancelledCheck.ConfigureAwait(false);
            if (!acquired)
            {
                await _pairingGate.WaitAsync(operation.Token).ConfigureAwait(false);
                acquired = true;
            }
            operation.Token.ThrowIfCancellationRequested();
            var owner = await CreateSelectedTaskOwnerAsync(taskId, operation.Token).ConfigureAwait(false);
            try
            {
                await owner.StartAsync(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                return owner;
            }
            catch
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lock (_operationSync)
            {
                _productionStartPending = false;
                if (acquired) _pairingGate.Release();
            }
        }
    }

    private PhotoshopConnectionCheckResult PublishCheck(PhotoshopConnectionCheckResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref _latestConnectionCheck, result);
        return result;
    }

    private async Task<PhotoshopConnectionCheckResult> CheckOwnerAsync(CancellationToken operationToken)
    {
        var expires = timeProvider.GetUtcNow().Add(ConnectionCheckWaitLimit);
        using var timeout = new CancellationTokenSource(ConnectionCheckWaitLimit, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, timeout.Token);
        var token = linked.Token;
        BridgeSessionSnapshot? last = null;
        try
        {
            token.ThrowIfCancellationRequested();
            using var selectedHost = hostSelection is null ? null : await hostSelection.AcquireAsync(token).ConfigureAwait(false);
            var secret = secrets.Read();
            BridgeSessionOwner owner;
            try
            {
                if (secret is not { Length: 32 })
                    return new(PhotoshopConnectionCheckStatus.PairingRequired, timeProvider.GetUtcNow());
                owner = new(secret, new($"connection_check_{Guid.NewGuid():N}", "0.1.0", SupportedPhotoshopBuild,
                    Capabilities), new ConnectionCheckJournal(), timeProvider, selectedHost, selectedHost);
            }
            finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
            await using (owner.ConfigureAwait(false))
            {
                await owner.StartAsync(token).ConfigureAwait(false);
                while (timeProvider.GetUtcNow() < expires)
                {
                    last = await owner.GetStatusAsync(token).ConfigureAwait(false);
                    if (last.Paired && last.Connected && last.Heartbeats >= 3)
                    {
                        token.ThrowIfCancellationRequested();
                        // The existing handshake compares the complete build exactly and authenticates
                        // that transcript. This is a match, not executable discovery or render admission.
                        return new(last.Ready ? PhotoshopConnectionCheckStatus.Ready : PhotoshopConnectionCheckStatus.HostNotReady,
                            timeProvider.GetUtcNow(), last.Heartbeats, SupportedPhotoshopBuild);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, token).ConfigureAwait(false);
                }
                return TimedOut();
            }
        }
        catch (Exception) when (operationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(operationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested) { return TimedOut(); }
        catch (InvalidOperationException error) when (error.Message == "PHOTOSHOP_HOST_SELECTION_REQUIRED")
        { return new(PhotoshopConnectionCheckStatus.HostSelectionRequired, timeProvider.GetUtcNow()); }
        catch (InvalidOperationException error) when (error.Message == "PHOTOSHOP_HOST_SELECTION_INVALID")
        { return new(PhotoshopConnectionCheckStatus.HostSelectionInvalid, timeProvider.GetUtcNow()); }
        catch (InvalidOperationException error) when (error.Message == "BRIDGE_BUSY")
        { return new(PhotoshopConnectionCheckStatus.Busy, timeProvider.GetUtcNow()); }
        catch (IOException error) when (error.Message == "BRIDGE_PORT_UNAVAILABLE")
        { return new(PhotoshopConnectionCheckStatus.Busy, timeProvider.GetUtcNow()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        { return new(PhotoshopConnectionCheckStatus.Failed, timeProvider.GetUtcNow()); }

        PhotoshopConnectionCheckResult TimedOut() => new(PhotoshopConnectionCheckStatus.TimedOut,
            timeProvider.GetUtcNow(), last?.Heartbeats ?? 0);
    }

    // The check has no execution work and must not create/recover a durable task journal.
    // Even disconnect/timeout may save an empty broker journal; keep that operation in memory.
    private sealed class ConnectionCheckJournal : IBridgeExecutionJournalStore
    {
        public Task<BridgeExecutionJournal?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<BridgeExecutionJournal?>(null);
        }

        public Task SaveAsync(BridgeExecutionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journal.Executions.Count != 0) throw new InvalidOperationException("BRIDGE_CHECK_EXECUTION_FORBIDDEN");
            return Task.CompletedTask;
        }
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var secret = secrets.Read();
        try { return Task.FromResult(secret is { Length: 32 }); }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Creates an unstarted owner for isolated tooling. Production must use StartTaskOwnerAsync.</summary>
    public BridgeSessionOwner CreateTaskOwner(string taskId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (hostSelection is not null) throw new InvalidOperationException("PHOTOSHOP_HOST_SELECTION_USE_ASYNC_OWNER");
        ValidateTaskId(taskId);
        var secret = secrets.Read() ?? throw new InvalidOperationException("BRIDGE_PAIRING_REQUIRED");
        try { return Create(secret, taskId, null); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private async Task<BridgeSessionOwner> CreateSelectedTaskOwnerAsync(string taskId, CancellationToken token)
    {
        var selectedHost = hostSelection is null ? null : await hostSelection.AcquireAsync(token).ConfigureAwait(false);
        try
        {
            var secret = secrets.Read() ?? throw new InvalidOperationException("BRIDGE_PAIRING_REQUIRED");
            try { return Create(secret, taskId, null, selectedHost); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        catch { selectedHost?.Dispose(); throw; }
    }

    private static void ValidateTaskId(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw new ArgumentException("BRIDGE_SESSION_CONFIGURATION_INVALID", nameof(taskId));
    }

    public async Task PairAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> presentCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentCode);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!TryBeginSetup(cancellationToken))
            throw new InvalidOperationException("BRIDGE_BUSY");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var selectedHost = hostSelection is null ? null : await hostSelection.AcquireAsync(cancellationToken).ConfigureAwait(false);
            // Five-minute introduction code; the setup owner consumes it once.
            // The installation value is persisted only after authenticated readiness.
            var expires = timeProvider.GetUtcNow().AddMinutes(5);
            await using var owner = Create(secret, "render_pairing_setup", expires, selectedHost);
            await owner.StartAsync(cancellationToken).ConfigureAwait(false);
            await presentCode(secret, cancellationToken).ConfigureAwait(false);
            while (timeProvider.GetUtcNow() < expires)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await owner.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status.Paired && status.Connected && status.Ready && status.Heartbeats >= 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    secrets.Save(secret);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("BRIDGE_PAIRING_EXPIRED");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            try { await presentCode(ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false); }
            finally { EndSetup(); }
        }
    }

    private BridgeSessionOwner Create(byte[] secret, string taskId, DateTimeOffset? expires, PhotoshopSelectedHostLease? selectedHost = null)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)));
        return new(secret, new(taskId, "0.1.0", SupportedPhotoshopBuild,
            expires is null ? Capabilities : Capabilities.Append(ProtectedPairingCapability).ToArray(), expires),
            new FileBridgeExecutionJournalStore(paths, $"render/tasks/{key}/bridge-executions-v1.json"), timeProvider,
            selectedHost, selectedHost);
    }

    internal void RecordSessionFaults(string taskId, BridgeLoopbackHost.TransportFault? transport,
        BridgeExecutionBroker.BindingFault? binding)
    {
        if (transport is null && binding is null) return;
        // Task-local diagnostic only: fixed stages/types, no request, session, media or exception message.
        // A diagnostic failure must never change an execution or its recovery result.
        try
        {
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)));
            var directory = paths.ResolveOwnedPath($"render/tasks/{key}");
            BridgeOutputReservation.ValidatePath(directory);
            Directory.CreateDirectory(directory);
            BridgeOutputReservation.ValidatePath(directory);
            using var file = new FileStream(Path.Combine(directory, $"bridge-fault-{Guid.NewGuid():N}.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new { recorded_at = timeProvider.GetUtcNow(), transport_fault = transport, binding_fault = binding });
            file.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _pairingGate.WaitAsync().ConfigureAwait(false);
        _pairingGate.Release();
        _pairingGate.Dispose();
        _lifetime.Dispose();
    }
}
