using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace BringYourOwnAgent.Tests;

/// <summary>
/// Runs the shared test vectors and the .NET unit tests. Exit code 0 when everything passes.
/// Options: --dump FILE writes every vector result as one JSON line, for comparing with the other libraries.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var dumpAt = Array.IndexOf(args, "--dump");
        var dumpPath = dumpAt >= 0 && dumpAt + 1 < args.Length ? args[dumpAt + 1] : null;

        var library = typeof(Gate).Assembly;
        var libraryTarget = library.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "?";
        Console.WriteLine(
            "BringYourOwnAgent " + LibraryInfo.Version + " (" + libraryTarget + ") on "
            + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        var directory = VectorTests.FindDirectory();
        var results = new List<string>();
        var vectors = VectorTests.Run(directory, results);
        var afterVectors = (Check.Passed, Check.Failed);
        Console.WriteLine("shared vectors: " + afterVectors.Passed + " passed, " + afterVectors.Failed + " failed (" + vectors + " cases)");

        UnitTests.Run();
        Console.WriteLine(
            ".NET unit tests: " + (Check.Passed - afterVectors.Passed) + " passed, " + (Check.Failed - afterVectors.Failed) + " failed");

        if (dumpPath is not null)
        {
            File.WriteAllText(dumpPath, string.Join("\n", results) + "\n", new UTF8Encoding(false));
        }

        return Check.Failed == 0 && vectors > 0 ? 0 : 1;
    }
}
