namespace Byoa.Wallet;

/// <summary>
/// What the wallet calls the device it runs on, in the words it says to the visitor: "this PC" and "this computer" on a computer, "this phone" on a
/// phone. The platform sets it once at start (like the key-store sentences in <c>SecretProtectors</c>); the texts that name where a model runs
/// ask for it, so that a phone does not tell its owner that a model is "on this PC".
/// </summary>
public static class ThisDevice
{
    static volatile string? _noun;

    /// <summary>Tests only: the word of the running test and the code it starts, and of no other test (see <see cref="Scoped"/>).</summary>
    static readonly AsyncLocal<string?> Scope = new();

    /// <summary>"this PC" ("this phone" once a platform said so): where a model that runs on the device is, in a short line.</summary>
    public static string Pc => Scope.Value ?? _noun ?? "this PC";

    /// <summary>"this computer" ("this phone" once a platform said so): the same place, in the wording of a source's name.</summary>
    public static string Computer => Scope.Value ?? _noun ?? "this computer";

    /// <summary>The platform names the device in these words, for every text from now on (for example "this phone").</summary>
    public static void Use(string noun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        _noun = noun;
    }

    /// <summary>
    /// Tests only: the device has this name for the code that runs inside the returned scope, and for nothing else: tests run in parallel, and a word
    /// that was changed for the whole process would show in the texts of the others.
    /// </summary>
    internal static IDisposable Scoped(string noun)
    {
        var before = Scope.Value;
        Scope.Value = noun;
        return new Restore(() => Scope.Value = before);
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
