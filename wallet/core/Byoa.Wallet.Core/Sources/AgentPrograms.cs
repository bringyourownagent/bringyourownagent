namespace Byoa.Wallet.Sources;

/// <summary>Finding and checking the program that runs the visitor's own agent. Only the visitor chooses it, in the wallet's window.</summary>
public static class AgentPrograms
{
    /// <summary>A path or a name the wallet will hand to the system: no quote and no percent sign (the command interpreter reads both inside quotes).</summary>
    public static bool IsPlain(string? command) =>
        !string.IsNullOrWhiteSpace(command) && command.Length <= 260
        && command.All(c => !char.IsControl(c) && c is not ('"' or '%' or '<' or '>' or '|'));

    /// <summary>
    /// A model name the wallet will pass to the agent's program as <c>--model name</c>: letters, digits and <c>. _ : / -</c>, starting with a letter or digit
    /// (so it can never be read as another option), at most 64 characters. It goes into a command line, so nothing else is allowed.
    /// </summary>
    public static bool IsModelName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-');

    /// <summary>
    /// The full path of the program: the path itself when it is one, else the first match on PATH (Windows: .exe, .cmd, .bat; macOS: a file
    /// that can be run). Null when there is none.
    /// </summary>
    public static string? Resolve(string? command)
    {
        command = command?.Trim();
        if (string.IsNullOrEmpty(command) || !IsPlain(command)) return null;
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        if (command.Contains('\\') || command.Contains('/')) return null;
        var extensions = !OperatingSystem.IsWindows() || Path.HasExtension(command) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat" };
        foreach (var directory in SearchDirectories())
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), command + extension);
                    if (File.Exists(candidate) && IsRunnable(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The folders a program name is looked for in: PATH, and on macOS also where agent programs usually live. A program started from the
    /// Dock or by a browser gets a short PATH (the system's), without Homebrew, npm or the folder of Claude Code's own installer.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparison);
        var all = new List<string>();
        void Add(string? directory)
        {
            directory = directory?.Trim();
            if (!string.IsNullOrEmpty(directory) && seen.Add(directory)) all.Add(directory);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(directory);
        if (!OperatingSystem.IsMacOS()) return all;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var directory in new[]
                 {
                     "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
                     Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"), Path.Combine(home, ".npm-global", "bin"),
                     Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".cargo", "bin"),
                 }) Add(directory);
        // nvm and similar tools keep each version of node in a folder of its own
        var nvm = Path.Combine(home, ".nvm", "versions", "node");
        try
        {
            if (Directory.Exists(nvm))
                foreach (var version in Directory.GetDirectories(nvm).OrderDescending(StringComparer.Ordinal)) Add(Path.Combine(version, "bin"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not readable: skip it */ }

        return all;
    }

    /// <summary>The PATH to give a program the wallet starts (<see cref="SearchDirectories"/> joined), so that a script that starts with <c>#!/usr/bin/env node</c> finds node.</summary>
    public static string SearchPath() => string.Join(Path.PathSeparator, SearchDirectories());

    /// <summary>On macOS and Linux a program must have an execute permission; on Windows the extension decides.</summary>
    static bool IsRunnable(string file)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            return (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The id a source made from a name gets: "agent:" and the name as a plain slug.</summary>
    public static string IdOf(string name) => "agent:" + Slug(name);

    /// <summary>Letters and digits of a name, lower case, joined by hyphens: "Claude Code (GLM)" is "claude-code-glm".</summary>
    public static string Slug(string name)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) current.Append(c);
            else if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) parts.Add(current.ToString());
        var slug = string.Join('-', parts);
        return slug.Length == 0 ? "agent" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
