namespace Byoa.Wallet.Everywhere;

/// <summary>
/// Turns the stream of keys from the keyboard hook into "the hotkey of dictation (or of the check) went down" and "it was let go". It keeps track of
/// which modifiers are held, accepts a chord only with exactly its modifiers, ignores the wallet's own keystrokes, and tells the hook to keep the
/// chord's key from the program that has the focus (the way a registered hotkey does), its key-up included. A key that is not part of a chord is
/// never touched.
/// </summary>
public sealed class HotkeyMonitor : IDisposable
{
    readonly IKeyboardHook _hook;
    readonly object _gate = new();
    readonly HashSet<int> _down = [];
    Dictionary<EverywhereFeature, Hotkey> _bindings = [];
    EverywhereFeature? _active;       // the chord held now
    Hotkey _activeKey;
    readonly HashSet<int> _swallowUps = [];   // keys whose "down" was kept from the programs: their "up" is kept too
    bool _started;

    public HotkeyMonitor(IKeyboardHook hook) => _hook = hook;

    /// <summary>The chord went down. Called on the hook's thread: keep the handler short.</summary>
    public event Action<EverywhereFeature>? Pressed;

    /// <summary>The chord's key (or one of its modifiers) went up after <see cref="Pressed"/>.</summary>
    public event Action<EverywhereFeature>? Released;

    /// <summary>Esc went down while <see cref="WantsEscape"/> said yes.</summary>
    public event Action? EscapePressed;

    /// <summary>Asked on every Esc: true while the wallet is doing something that Esc cancels (then the program with the focus does not get that Esc).</summary>
    public Func<bool>? WantsEscape { get; set; }

    public bool Listening
    {
        get { lock (_gate) return _started; }
    }

    /// <summary>
    /// Sets the hotkeys to listen for. The hook runs only while there is at least one: with both features off, the wallet sees no key at all.
    /// </summary>
    public void SetBindings(IReadOnlyDictionary<EverywhereFeature, Hotkey> bindings)
    {
        bool start, stop;
        lock (_gate)
        {
            _bindings = new Dictionary<EverywhereFeature, Hotkey>(bindings);
            _active = null;
            _swallowUps.Clear();
            _down.Clear();
            start = _bindings.Count > 0 && !_started;
            stop = _bindings.Count == 0 && _started;
            if (start) _started = true;
            if (stop) _started = false;
        }

        if (start) _hook.Start(Handle);
        if (stop) _hook.Stop();
    }

    bool Handle(KeyEvent e)
    {
        if (e.Injected) return false;
        Action? raise = null;
        var swallow = false;
        lock (_gate)
        {
            if (e.Down) _down.Add(e.VirtualKey);
            else _down.Remove(e.VirtualKey);
            var modifier = HotkeyKeys.ModifierOfCode(e.VirtualKey);

            if (e.Down)
            {
                if (modifier is not null) return false;
                if (_active is not null && e.VirtualKey == _activeKey.VirtualKey) return true;      // the held key repeating
                var held = HeldModifiers();
                if (_active is null)
                {
                    foreach (var (feature, key) in _bindings)
                    {
                        if (key.VirtualKey != e.VirtualKey || key.Modifiers != held) continue;
                        _active = feature;
                        _activeKey = key;
                        _swallowUps.Add(e.VirtualKey);
                        swallow = true;
                        raise = () => Pressed?.Invoke(feature);
                        break;
                    }
                }

                // Esc cancels, also while the chord is still held (hold to talk: Ctrl+Alt are down); with other modifiers it is the program's.
                if (!swallow && e.VirtualKey == HotkeyKeys.Escape && (held == HotkeyModifiers.None || _active is not null) && WantsEscape?.Invoke() == true)
                {
                    _swallowUps.Add(e.VirtualKey);
                    swallow = true;
                    raise = () => EscapePressed?.Invoke();
                }
            }
            else
            {
                if (_active is { } feature && (e.VirtualKey == _activeKey.VirtualKey
                        || (modifier is { } m && _activeKey.Modifiers.HasFlag(m) && !HeldModifiers().HasFlag(m))))
                {
                    _active = null;
                    raise = () => Released?.Invoke(feature);
                }

                if (_swallowUps.Remove(e.VirtualKey)) swallow = true;
            }
        }

        raise?.Invoke();
        return swallow;
    }

    HotkeyModifiers HeldModifiers()
    {
        var held = HotkeyModifiers.None;
        foreach (var code in _down)
        {
            if (HotkeyKeys.ModifierOfCode(code) is { } m) held |= m;
        }

        return held;
    }

    public void Dispose()
    {
        bool stop;
        lock (_gate)
        {
            stop = _started;
            _started = false;
            _bindings = [];
        }

        if (stop) _hook.Stop();
        _hook.Dispose();
    }
}
