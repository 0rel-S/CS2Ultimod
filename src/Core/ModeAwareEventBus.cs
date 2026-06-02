namespace CS2Ultimod.Core;

public interface IModeAwareEventBus
{
    // Handler is invoked only when current mode is in the provided list.
    // Pass no modes to receive event in all modes.
    void Subscribe<TEvent>(Action<TEvent> handler, params GameMode[] modes)
        where TEvent : UltimodEvent;

    // Foundation calls this to dispatch events.
    void Publish<TEvent>(TEvent evt) where TEvent : UltimodEvent;
}

public sealed class ModeAwareEventBus : IModeAwareEventBus
{
    private readonly IModeManager _modeManager;
    private readonly Dictionary<Type, List<(GameMode[] Modes, Delegate Handler)>> _handlers = [];

    public ModeAwareEventBus(IModeManager modeManager) => _modeManager = modeManager;

    public void Subscribe<TEvent>(Action<TEvent> handler, params GameMode[] modes)
        where TEvent : UltimodEvent
    {
        var key = typeof(TEvent);
        if (!_handlers.TryGetValue(key, out var list))
            _handlers[key] = list = [];
        list.Add((modes, handler));
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : UltimodEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            return;

        var current = _modeManager.Current;
        foreach (var (modes, handler) in list)
        {
            if (modes.Length == 0 || modes.Contains(current))
            {
                try { ((Action<TEvent>)handler)(evt); }
                catch { /* individual handler failures are isolated */ }
            }
        }
    }
}
