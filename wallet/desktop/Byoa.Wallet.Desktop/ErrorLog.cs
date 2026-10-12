using System.Text;

namespace Byoa.Wallet.Desktop;

/// <summary>Best-effort crash logging: the file contains exception types and stacks, never exception messages.</summary>
internal static class ErrorLog
{
    internal static void Write(string home, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(home);
            var type = exception.GetType().FullName ?? exception.GetType().Name;
            var stack = exception.StackTrace ?? "(no stack)";
            File.AppendAllText(Path.Combine(home, "errors.log"), type + Environment.NewLine + stack + Environment.NewLine + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // An exception handler must not throw while trying to report another exception.
        }
    }
}
