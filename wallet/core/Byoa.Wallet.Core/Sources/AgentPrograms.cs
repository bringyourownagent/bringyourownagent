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

    /// <summary>The full path of the program: the path itself when it is one, else the first match on PATH (.exe, .cmd, .bat). Null when there is none.</summary>
    public static string? Resolve(string? command)
    {
        command = command?.Trim();
        if (string.IsNullOrEmpty(command) || !IsPlain(command)) return null;
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        if (command.Contains('\\') || command.Contains('/')) return null;
        var extensions = Path.HasExtension(command) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), command + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }

        return null;
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
