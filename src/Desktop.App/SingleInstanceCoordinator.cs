using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Desktop.App;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly string _instanceGateName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Semaphore? _instanceGate;
    private Task? _listener;
    private int _disposed;

    public SingleInstanceCoordinator(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var userScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        _instanceGateName = $"Local\\{applicationId}.{userScope}";
        _pipeName = $"{applicationId}.{userScope}.activation";
    }

    public async Task<bool> TryAcquireAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var gate = new Semaphore(initialCount: 1, maximumCount: 1, _instanceGateName);
        if (gate.WaitOne(0))
        {
            _instanceGate = gate;
            return true;
        }

        gate.Dispose();
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync("activate"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // A second writer remains forbidden even if activation notification cannot be delivered.
        }

        return false;
    }

    public void StartActivationListener(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (_instanceGate is null || _listener is not null)
        {
            throw new InvalidOperationException("SINGLE_INSTANCE_NOT_PRIMARY");
        }

        _listener = Task.Run(() => ListenAsync(activate, _shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        if (_listener is not null)
        {
            try
            {
                await _listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _instanceGate?.Release();
        _instanceGate?.Dispose();
        _shutdown.Dispose();
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16];
            var read = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0 && Encoding.UTF8.GetString(buffer, 0, read).StartsWith("activate", StringComparison.Ordinal))
            {
                activate();
            }
        }
    }
}
