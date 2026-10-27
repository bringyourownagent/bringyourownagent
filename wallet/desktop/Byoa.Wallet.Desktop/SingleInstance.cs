using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Only one wallet runs per user and data folder. Windows: a named mutex (<see cref="WalletPaths.MutexName"/>). macOS: an exclusive lock on
/// <c>instance.lock</c> in the data folder, held for as long as the wallet runs (the system drops it when the process ends, however it ends).
/// </summary>
static class SingleInstance
{
    /// <summary>The held lock (dispose it at exit), or null when another wallet already holds it.</summary>
    public static IDisposable? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(true, WalletPaths.MutexName(), out var first);
            if (first) return mutex;
            mutex.Dispose();
            return null;
        }

        return TryLockFile(WalletPaths.InstanceLockPath);
    }

    /// <summary>An exclusive lock on a file (created when missing); null when somebody else holds it.</summary>
    internal static IDisposable? TryLockFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // FileShare.None is an exclusive advisory lock (flock) on macOS and Linux; the descriptor is not inherited by the programs the wallet starts
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
