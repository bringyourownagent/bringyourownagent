using System.IO.Pipes;
using System.Text;
using Avalonia;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;

namespace Byoa.Wallet.Desktop;

static class Program
{
    /// <summary>Started by the native host (<c>--background</c>): no window, only the tray icon.</summary>
    public static bool Background { get; private set; }

    /// <summary>
    /// <c>Byoa.Wallet.exe</c> opens the wallet (or brings the running one forward);
    /// <c>--background</c> starts it in the tray only;
    /// <c>--import-key openrouter &lt;file&gt;</c> stores a key from a text file in the DPAPI store and exits.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
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

        using var instance = new Mutex(true, WalletPaths.MutexName(), out var first);
        Background = args.Contains("--background");
        if (!first)
        {
            if (!Background) AskRunningWalletToOpen();
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

    /// <summary>Crashes go to errors.log in the data folder (types and stack only; no content, no secrets).</summary>
    static void LogError(Exception? e)
    {
        if (e is null) return;
        try
        {
            Directory.CreateDirectory(WalletPaths.Home);
            File.AppendAllText(Path.Combine(WalletPaths.Home, "errors.log"), DateTimeOffset.Now.ToString("O") + " " + e.GetType().FullName + "\n" + e.StackTrace + "\n\n");
        }
        catch (IOException)
        {
            // nothing more to do
        }
    }
}
