namespace Desktop.Application.Events;

public interface IApplicationEvent;

public interface IApplicationEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IApplicationEvent;

    void Publish<TEvent>(TEvent applicationEvent) where TEvent : IApplicationEvent;
}

public sealed class ApplicationEventBus : IApplicationEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            {
                handlers = [];
                _handlers[typeof(TEvent)] = handlers;
            }

            handlers.Add(handler);
        }

        return new Subscription(() => Remove(handler));
    }

    public void Publish<TEvent>(TEvent applicationEvent) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        Delegate[] handlers;
        lock (_gate)
        {
            handlers = _handlers.TryGetValue(typeof(TEvent), out var found)
                ? [.. found]
                : [];
        }

        foreach (var handler in handlers.Cast<Action<TEvent>>())
        {
            handler(applicationEvent);
        }
    }

    private void Remove<TEvent>(Action<TEvent> handler) where TEvent : IApplicationEvent
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
            {
                handlers.Remove(handler);
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
