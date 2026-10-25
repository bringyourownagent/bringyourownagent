using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Everywhere;

public enum FeatureState
{
    /// <summary>The visitor has not turned it on: no key is listened for.</summary>
    Off,

    /// <summary>On, and its hotkey is listened for.</summary>
    On,

    /// <summary>On, but its hotkey cannot be used, so nothing is listened for; the message says why and what to do.</summary>
    Problem,
}

public sealed record FeatureStatus(FeatureState State, string? Message = null);

public sealed record EverywhereStatus(FeatureStatus Dictation, FeatureStatus Check)
{
    public static readonly EverywhereStatus Off = new(new FeatureStatus(FeatureState.Off), new FeatureStatus(FeatureState.Off));
}

/// <summary>
/// Ties the settings to the keyboard: when a feature is switched on (or its hotkey changed) the hotkey is checked (understood, allowed, not clashing
/// with the other one, not held by another program) and listened for; a hotkey that fails the check is not listened for, and the status says so in
/// plain words for the page. With both features off the keyboard hook is not even installed. The hook calls in on its own thread, so every
/// hotkey only hands the work on to the state machines, which queue it.
/// </summary>
public sealed class EverywhereService : IDisposable
{
    readonly SettingsStore _store;
    readonly HotkeyMonitor _monitor;
    readonly DictationController _dictation;
    readonly SelectionCheckController _check;
    readonly IHotkeyProbe? _probe;
    readonly IKeyboard? _keyboard;
    readonly object _gate = new();
    string _applied = "";
    EverywhereStatus _status = EverywhereStatus.Off;
    bool _started;

    public EverywhereService(SettingsStore store, HotkeyMonitor monitor, DictationController dictation, SelectionCheckController check,
        IHotkeyProbe? probe = null, IKeyboard? keyboard = null)
    {
        _store = store;
        _monitor = monitor;
        _dictation = dictation;
        _check = check;
        _probe = probe;
        _keyboard = keyboard;
        monitor.Pressed += OnPressed;
        monitor.Released += OnReleased;
        monitor.EscapePressed += () => _ = _dictation.CancelAsync();
        monitor.WantsEscape = () => _dictation.Busy;
    }

    public EverywhereStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>The status changed (a feature was switched, or a hotkey was found unusable).</summary>
    public event Action? StatusChanged;

    /// <summary>Reads the settings, listens for what is on, and follows every later change of the settings.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _store.Changed += Apply;
        Apply();
    }

    /// <summary>Applies the Everywhere settings as they are now. Cheap when nothing in them changed.</summary>
    public void Apply()
    {
        var (dictationOn, dictationKey, checkOn, checkKey) =
            _store.Read(s => (s.Everywhere.DictationOn, s.Everywhere.DictationHotkey, s.Everywhere.CheckOn, s.Everywhere.CheckHotkey));
        var bindings = new Dictionary<EverywhereFeature, Hotkey>();
        EverywhereStatus status;
        lock (_gate)
        {
            var fingerprint = string.Join('|', dictationOn, dictationKey, checkOn, checkKey);
            if (fingerprint == _applied) return;
            _applied = fingerprint;
            var dictation = Decide(EverywhereFeature.Dictation, dictationOn, dictationKey, checkOn ? checkKey : null, bindings);
            var check = Decide(EverywhereFeature.Check, checkOn, checkKey, dictationOn ? dictationKey : null, bindings);
            status = new EverywhereStatus(dictation, check);
            _status = status;
        }

        _monitor.SetBindings(bindings);
        if (!dictationOn) _ = _dictation.CancelAsync();
        if (!checkOn) _ = _check.CloseAsync();
        StatusChanged?.Invoke();
    }

    FeatureStatus Decide(EverywhereFeature feature, bool on, string text, string? otherText, Dictionary<EverywhereFeature, Hotkey> bindings)
    {
        if (!on) return new FeatureStatus(FeatureState.Off);
        var (hotkey, message) = HotkeyRules.Validate(text, feature, otherText, _probe);
        if (hotkey is not { } key) return new FeatureStatus(FeatureState.Problem, message);
        bindings[feature] = key;
        return new FeatureStatus(FeatureState.On);
    }

    void OnPressed(EverywhereFeature feature)
    {
        // The hook's thread: only hand the work on.
        if (feature == EverywhereFeature.Dictation) _ = _dictation.PressAsync();
        else _ = _check.HotkeyAsync();
        if (_keyboard is not null && HotkeyOf(feature) is { } hotkey && hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            ThreadPool.QueueUserWorkItem(_ => _keyboard.MaskMenuKey());
    }

    void OnReleased(EverywhereFeature feature)
    {
        if (feature == EverywhereFeature.Dictation) _ = _dictation.ReleaseAsync();
    }

    Hotkey? HotkeyOf(EverywhereFeature feature)
    {
        var text = _store.Read(s => feature == EverywhereFeature.Dictation ? s.Everywhere.DictationHotkey : s.Everywhere.CheckHotkey);
        return Hotkey.TryParse(text, out var hotkey, out _) ? hotkey : null;
    }

    public void Dispose()
    {
        _store.Changed -= Apply;
        _monitor.Dispose();
    }
}
