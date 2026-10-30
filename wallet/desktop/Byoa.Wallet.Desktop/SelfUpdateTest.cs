using System.Diagnostics;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The test-only <c>--self-update-test</c> (honoured only with a data folder of the wallet's own, like <c>--crash-test</c>): it runs the
/// same updater the settings window runs, without a window, printing every state. Instead of a detached restart it starts the new
/// <c>current/Byoa.Wallet --version</c> and prints what that says, so a machine without a screen can prove the whole update end to end.
/// </summary>
static class SelfUpdateTest
{
    public static int Run()
    {
        var restarted = new List<string>();
        var failures = new List<string>();
        var options = LinuxUpdateOptions.ForThisUser() with
        {
            StartRestarted = (installRoot, _) => RestartedVersion(Path.Combine(installRoot, "current", "Byoa.Wallet"), restarted, failures),
        };
        var engine = new LinuxUpdateEngine(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            Console.WriteLine("can update itself: " + (engine.CanUpdateItself ? "yes" : "no"));
            if (!engine.CanUpdateItself)
            {
                Console.Error.WriteLine("This copy is not installed under AgentWallet/versions, so there is nothing to update.");
                return 1;
            }

            Console.WriteLine("check: looking at " + options.FeedBase + "/latest.json");
            var found = engine.CheckAsync(cts.Token).GetAwaiter().GetResult();
            if (found is null)
            {
                Console.WriteLine("check: this is the newest version");
                return 0;
            }

            Console.WriteLine("check: version " + found + " is available");
            var last = -1;
            engine.DownloadAsync(percent =>
            {
                if (percent == last) return;
                last = percent;
                Console.WriteLine("download: " + percent + "%");
            }, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine("download: checked and unpacked");

            engine.ApplyAndRestart();
            var target = new DirectoryInfo(LinuxInstall.Current(options.DataRoot)).LinkTarget ?? "?";
            Console.WriteLine("apply: current points at " + target);
            foreach (var line in restarted) Console.WriteLine("restarted: " + line);
            foreach (var failure in failures) Console.Error.WriteLine("self-update-test: " + failure);
            if (restarted.Count != 1 || !restarted[0].Contains(found, StringComparison.Ordinal) || failures.Count > 0)
            {
                Console.Error.WriteLine("self-update-test: the restarted program did not report version " + found + ".");
                return 1;
            }

            Console.WriteLine("self-update-test: ok");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine("self-update-test: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }

    static void RestartedVersion(string wallet, List<string> restarted, List<string> failures)
    {
        try
        {
            var info = new ProcessStartInfo(wallet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("--version");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("the restarted wallet did not start");
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30_000);
            if (process.ExitCode != 0) failures.Add("the restarted wallet ended with code " + process.ExitCode);
            restarted.Add(output);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failures.Add("the restarted wallet could not be started: " + e.Message);
        }
    }
}