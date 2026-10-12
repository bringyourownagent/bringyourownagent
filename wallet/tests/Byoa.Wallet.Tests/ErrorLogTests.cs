using Byoa.Wallet.Desktop;

namespace Byoa.Wallet.Tests;

public sealed class ErrorLogTests
{
    [Fact]
    public void WriterStoresOnlyExceptionTypeAndStack()
    {
        var home = TestFolder.New();
        try
        {
            ThrowForLogTest();
        }
        catch (Exception exception)
        {
            ErrorLog.Write(home, exception);
        }

        var text = File.ReadAllText(Path.Combine(home, "errors.log"));
        Assert.Contains(typeof(InvalidOperationException).FullName!, text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowForLogTest), text, StringComparison.Ordinal);
        Assert.DoesNotContain("private message", text, StringComparison.Ordinal);
    }

    static void ThrowForLogTest() => throw new InvalidOperationException("private message");
}
