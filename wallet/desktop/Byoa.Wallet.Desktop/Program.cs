using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;

namespace Byoa.Wallet.Desktop;

static class Program
{
    /// <summary>Started by the native host (<c>--background</c>) or by a link: no window, only the tray icon.</summary>
    public static bool Background { get; private set; }

    /// <summary>The <c>bringyourownagent://</c> link this process was started with (<c>--url</c>), if any.</summary>
    public static string? LinkArgument { get; private set; }

    /// <summary>
    /// <c>Byoa.Wallet.exe</c> opens the wallet (or brings the running one forward);
    /// <c>--background</c> starts it in the tray only;
    /// <c>--url &lt;link&gt;</c> acts on a bringyourownagent:// link (registered by install.ps1); when a wallet is
    /// already running, the link is handed to it over the pipe and this process exits;
    /// <c>--import-key openrouter &lt;file&gt;</c> stores a key from a text file in the DPAPI store and exits;
    /// <c>--version</c> prints the version and exits;
    /// <c>--register</c> (Linux) writes the browser host manifests, the extension copy, the desktop entry and the icons, prints what it
    /// did and exits (<c>--start-at-login</c> also puts the autostart entry in place);
    /// <c>--self-update-test</c> (Linux, only with a data folder of the wallet's own) runs a whole update without a window.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return 1;

        // The headless switches come before anything of the wallet starts: no window, no single instance, no update hook.
        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine(WalletPaths.Version);
            return 0;
        }

        if (args.Length > 0 && args[0] == "--register")
        {
            if (!OperatingSystem.IsLinux())
            {
                Console.Error.WriteLine("--register is for Linux.");
                return 2;
            }

            var registration = LinuxRegistration.ForThisUser();
            if (registration is null)
            {
                Console.WriteLine("This wallet keeps its data in a folder of its own, so nothing was registered for the desktop.");
                return 0;
            }

            foreach (var line in registration.Apply(AppContext.BaseDirectory)) Console.WriteLine(line);
            if (args.Contains("--start-at-login", StringComparer.Ordinal))
            {
                registration.SetStartAtLogin(AppContext.BaseDirectory, true);
                Console.WriteLine("start at login: on");
            }

            return 0;
        }

        if (WalletPaths.IsCustomHome && args.Contains("--self-update-test", StringComparer.Ordinal)) return SelfUpdateTest.Run();
        // Setup.exe and its updater start this program with a hook argument after an install or an update and before an uninstall:
        // the hook runs and the program ends here, before anything of the wallet starts. After an install or an update the registration
        // (host for the browser extension, links) is put in place for the folder the program now runs from; before an uninstall it is taken
        // back. A copy that Setup.exe did not install (a build from the source) runs through this without any effect. Velopack wants the
        // call here, at the top of Main, and not in another method. A test wallet (its own data folder) leaves the registry alone.
#pragma warning disable CA1416 // Windows only, as the line below says
        if (OperatingSystem.IsWindows())
            Velopack.VelopackApp.Build()
                .OnAfterInstallFastCallback(_ => { if (!WalletPaths.IsCustomHome) new WindowsRegistration(new UserRegistry()).Apply(AppContext.BaseDirectory); })
                .OnAfterUpdateFastCallback(_ => { if (!WalletPaths.IsCustomHome) new WindowsRegistration(new UserRegistry()).Apply(AppContext.BaseDirectory); })
                .OnBeforeUninstallFastCallback(_ => { if (!WalletPaths.IsCustomHome) new WindowsRegistration(new UserRegistry()).Remove(AppContext.BaseDirectory); })
                .Run();
#pragma warning restore CA1416
        if (OperatingSystem.IsWindows() && WalletPaths.IsCustomHome) Native.SetErrorMode(0x8003);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };

        if (args.Length >= 3 && args[0] == "--import-key")
        {
            var secrets = new SecretStore(Path.Combine(WalletPaths.Home, "secrets.dat"));
            return secrets.ImportFromFile(args[1], args[2]) ? 0 : 2;
        }

        if (WalletPaths.IsCustomHome && Environment.GetEnvironmentVariable("BYOA_WALLET_TEST_CRASH") == "1"
            && args.Contains("--crash-test"))
            throw new InvalidOperationException("deliberate isolated-wallet crash");

        var url = Array.IndexOf(args, "--url");
        if (url >= 0) LinkArgument = url + 1 < args.Length ? args[url + 1] : "";
        using var instance = SingleInstance.TryAcquire();
        var first = instance is not null;
        Background = args.Contains("--background") || LinkArgument is not null;
        var userActivation = !args.Contains("--no-user-activation", StringComparer.Ordinal);
        if (!first)
        {
            if (LinkArgument is not null && !HandOffLink(LinkArgument, userActivation) && !WalletPaths.IsCustomHome)
                Say("The wallet is running but did not take this link.\n\nIf it was started as administrator, close it from the tray icon and open it again normally (from the Start menu); a wallet started that way cannot be reached from the browser.");
            else if (!Background) AskRunningWalletToOpen();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // a menu-bar program: no Dock icon and no application menu (the bundle's LSUIElement says the same, but a build run from a folder has no bundle)
        if (OperatingSystem.IsMacOS()) builder = builder.With(new MacOSPlatformOptions { ShowInDock = false, DisableDefaultApplicationMenuItems = true });
        // the class the desktop entry names, so that "start at login" and a running wallet are the same application to the desktop
        if (OperatingSystem.IsLinux()) builder = builder.With(new X11PlatformOptions { WmClass = LinuxRegistration.WindowClass });
        return builder;
    }

    /// <summary>A message for the person before there is any window of the wallet: a message box on Windows, an alert on macOS, the error stream on Linux.</summary>
    static void Say(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            Native.MessageBox(IntPtr.Zero, text, "Agent wallet", 0x30);
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                var script = "display alert \"Agent wallet\" message \"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("osascript") { ArgumentList = { "-e", script }, UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no alert; nothing else to do */ }
        }
        else
        {
            Console.Error.WriteLine("Agent wallet: " + text);
        }
    }

    static void AskRunningWalletToOpen()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", WalletPaths.PipeName(), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            Framing.WriteAsync(pipe, Encoding.UTF8.GetBytes("{\"id\":1,\"type\":\"open\",\"origin\":\"\",\"payload\":{}}")).GetAwaiter().GetResult();
            Framing.ReadAsync(pipe).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // the other instance is still starting
        }
    }

    /// <summary>Hands a link to the running wallet over its pipe; every step is time-limited (LinkHandOff). False when it did not take it.</summary>
    static bool HandOffLink(string link, bool userActivation)
    {
        if (link.Length > DirectLink.MaxLength) link = link[..(DirectLink.MaxLength + 1)];   // the wallet refuses it as too long
        return LinkHandOff.Send(WalletPaths.PipeName(), link, userActivation: userActivation);
    }

    /// <summary>Crashes go to errors.log in the data folder (types and stack only; no content, no secrets).</summary>
    static void LogError(Exception? e)
    {
        if (e is not null) ErrorLog.Write(WalletPaths.Home, e);
    }

    static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern uint SetErrorMode(uint mode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    }
}
