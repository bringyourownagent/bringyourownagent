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
    /// <c>--import-key openrouter &lt;file&gt;</c> stores a key from a text file in the DPAPI store and exits.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        if (WalletPaths.IsCustomHome) Native.SetErrorMode(0x8003);
        if (args.Length >= 3 && args[0] == "--import-key")
        {
            var secrets = new SecretStore(Path.Combine(WalletPaths.Home, "secrets.dat"));
            return secrets.ImportFromFile(args[1], args[2]) ? 0 : 2;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };

        if (WalletPaths.IsCustomHome && Environment.GetEnvironmentVariable("BYOA_WALLET_TEST_CRASH") == "1"
            && args.Contains("--crash-test"))
            throw new InvalidOperationException("deliberate isolated-wallet crash");

        var url = Array.IndexOf(args, "--url");
        if (url >= 0) LinkArgument = url + 1 < args.Length ? args[url + 1] : "";
        using var instance = new Mutex(true, WalletPaths.MutexName(), out var first);
        Background = args.Contains("--background") || LinkArgument is not null;
        var userActivation = !args.Contains("--no-user-activation", StringComparer.Ordinal);
        if (!first)
        {
            if (LinkArgument is not null) HandOffLink(LinkArgument, userActivation);
            else if (!Background) AskRunningWalletToOpen();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

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

    /// <summary>Hands a link to the running wallet over its pipe; every step is time-limited (LinkHandOff).</summary>
    static void HandOffLink(string link, bool userActivation)
    {
        if (link.Length > DirectLink.MaxLength) link = link[..(DirectLink.MaxLength + 1)];   // the wallet refuses it as too long
        LinkHandOff.Send(WalletPaths.PipeName(), link, userActivation: userActivation);
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
    }
}
