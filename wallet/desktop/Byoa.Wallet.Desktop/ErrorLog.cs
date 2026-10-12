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
            var text = new StringBuilder();
            Append(text, exception, 0);
            File.AppendAllText(Path.Combine(home, "errors.log"), text.ToString() + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // An exception handler must not throw while trying to report another exception.
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
