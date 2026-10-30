using Tmds.DBus.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The parts of the Linux session the wallet asks about before it uses them. A tray icon is a StatusNotifierItem, and a wallet must not
/// depend on one: many GNOME sessions have no host for it. The lookup is done once and the answer is kept for the run.
/// </summary>
static class LinuxDesktop
{
    static readonly Lazy<bool> TrayHost = new(DetectTrayHost, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the session has a host for tray icons (or this is not Linux, where the platform always has one).</summary>
    public static bool ShowTrayIcon => !OperatingSystem.IsLinux() || TrayHost.Value;

    /// <summary>True when the session bus has an owner for <c>org.kde.StatusNotifierWatcher</c>.</summary>
    internal static bool DetectTrayHost()
    {
        try
        {
            var address = Address.Session;
            if (string.IsNullOrWhiteSpace(address)) return false;
            using var connection = new Connection(address);
            connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s");
            writer.WriteString("org.kde.StatusNotifierWatcher");
            return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadBool(), null)
                .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // no session bus, no D-Bus library, a bus that does not answer: the wallet runs without a tray icon
            return false;
        }
    }
}