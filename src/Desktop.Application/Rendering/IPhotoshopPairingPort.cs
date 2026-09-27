namespace Desktop.Application.Rendering;

/// <summary>Device pairing is explicit; display material is transient and must never be logged or persisted by the view.</summary>
public interface IPhotoshopPairingPort
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    /// <param name="presentCode">Receives 32 secret bytes, then an empty buffer to clear the view on every exit.</param>
    Task PairAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> presentCode, CancellationToken cancellationToken);
}
