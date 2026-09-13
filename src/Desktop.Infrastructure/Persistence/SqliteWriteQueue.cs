using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

internal sealed class SqliteWriteQueue : IAsyncDisposable
{
    private readonly object _disposeGate = new();
    private readonly Channel<IWriteCommand> _channel = Channel.CreateBounded<IWriteCommand>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly SqliteConnection _connection;
    private readonly Task _worker;
    private Task? _disposeTask;
    private int _closed;

    public SqliteWriteQueue(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        _worker = Task.Run(ProcessAsync);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _closed) != 0,
            this);

        var command = new WriteCommand<T>(action, cancellationToken);
        try
        {
            await _channel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new ObjectDisposedException(nameof(SqliteWriteQueue), exception);
        }

        return await command.Completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        using var checkpoint = _connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(FULL);";
        checkpoint.ExecuteNonQuery();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        await foreach (var command in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await command.ExecuteAsync(_connection).ConfigureAwait(false);
        }
    }

    private interface IWriteCommand
    {
        Task ExecuteAsync(SqliteConnection connection);
    }

    private sealed class WriteCommand<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) : IWriteCommand
    {
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public async Task ExecuteAsync(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                var result = await action(connection, cancellationToken).ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
