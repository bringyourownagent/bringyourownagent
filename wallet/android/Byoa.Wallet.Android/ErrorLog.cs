using System.Text;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Android;

/// <summary>Best-effort crash logging, as on the desktop: the file holds exception types and stacks, never exception messages (a message may carry a key).</summary>
static class ErrorLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var home = WalletPaths.Home;
            Directory.CreateDirectory(home);
            var text = new StringBuilder();
            Append(text, exception, 0);
            File.AppendAllText(Path.Combine(home, "errors.log"), text.ToString() + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // an exception handler must not throw while trying to report another exception
        }
    }

    static void Append(StringBuilder text, Exception exception, int depth)
    {
        if (depth > 8) return;
        text.AppendLine(exception.GetType().FullName ?? exception.GetType().Name);
        text.AppendLine(exception.StackTrace ?? "(no stack)");
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) Append(text, inner, depth + 1);
        }
        else if (exception.InnerException is { } inner)
        {
            Append(text, inner, depth + 1);
        }

        text.AppendLine();
    }
}
