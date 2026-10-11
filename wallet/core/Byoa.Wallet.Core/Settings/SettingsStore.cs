using System.Text.Json;

namespace Byoa.Wallet.Settings;

/// <summary>
/// Keeps <see cref="WalletSettings"/> in memory and in <c>settings.json</c> in the data folder. All access goes
/// through <see cref="Read{T}"/> and <see cref="Update"/>, under one lock; every update is written to disk at once
/// (to a temporary file next to it, then swapped in).
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly object _gate = new();
    readonly string? _path;
    readonly WalletSettings _settings;

    /// <summary>Raised after every update (outside the lock).</summary>
    public event Action? Changed;

    /// <param name="path">The settings file; null keeps the settings in memory only (tests).</param>
    public SettingsStore(string? path, Action<WalletSettings>? defaults = null)
    {
        _path = path;
        WalletSettings? loaded = null;
        if (path is not null && File.Exists(path))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<WalletSettings>(File.ReadAllText(path), Json);
            }
            catch (JsonException)
            {
                // A damaged file must not stop the wallet; it starts clean and keeps the damaged copy aside.
                File.Copy(path, path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
            }
        }

        _settings = loaded ?? new WalletSettings();
        defaults?.Invoke(_settings);
    }

    public T Read<T>(Func<WalletSettings, T> read)
    {
        lock (_gate) return read(_settings);
    }

    public void Update(Action<WalletSettings> change)
    {
        lock (_gate)
        {
            change(_settings);
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>A deep copy, for showing in the UI without holding the lock.</summary>
    public WalletSettings Snapshot()
    {
        lock (_gate) return JsonSerializer.Deserialize<WalletSettings>(JsonSerializer.Serialize(_settings, Json), Json)!;
    }

    void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
