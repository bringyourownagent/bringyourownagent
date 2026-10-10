using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using BringYourOwnAgent.Internal;

namespace BringYourOwnAgent;

/// <summary>One of the four decisions of the call gate, mildest first.</summary>
public enum Decision
{
    /// <summary>Run the call.</summary>
    Allowed = 0,

    /// <summary>Run the call, tell the visitor, log it.</summary>
    Warn = 1,

    /// <summary>Pause: run the call only if the visitor approves it now. If nobody can be asked, treat it as <see cref="Blocked"/>.</summary>
    Ask = 2,

    /// <summary>Do not run the call; log it.</summary>
    Blocked = 3,
}

/// <summary>Helpers for <see cref="Decision"/>.</summary>
public static class DecisionExtensions
{
    /// <summary>The decision as it is written in JSON: <c>allowed</c>, <c>warn</c>, <c>ask</c> or <c>blocked</c>.</summary>
    public static string ToJsonName(this Decision decision) => decision switch
    {
        Decision.Allowed => "allowed",
        Decision.Warn => "warn",
        Decision.Ask => "ask",
        Decision.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "not a BYOA decision"),
    };

    internal static bool IsDefined(Decision decision) => decision >= Decision.Allowed && decision <= Decision.Blocked;

    internal static bool TryParse(string? name, out Decision decision)
    {
        switch (name)
        {
            case "allowed": decision = Decision.Allowed; return true;
            case "warn": decision = Decision.Warn; return true;
            case "ask": decision = Decision.Ask; return true;
            case "blocked": decision = Decision.Blocked; return true;
            default: decision = Decision.Allowed; return false;
        }
    }
}

/// <summary>One problem found in a manifest.</summary>
public sealed class Issue
{
    /// <summary>Creates an issue.</summary>
    public Issue(string code, string path, string message)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>The stable rule code, such as <c>privacy-unknown</c>. Programs should use this.</summary>
    public string Code { get; }

    /// <summary>A JSON Pointer (RFC 6901) into the manifest; the empty string means the whole document.</summary>
    public string Path { get; }

    /// <summary>An explanation for people. It may change between versions.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() => Code + " at \"" + Path + "\": " + Message;

    internal void WriteJson(StringBuilder json)
    {
        json.Append("{\"code\":");
        JsText.AppendString(json, Code);
        json.Append(",\"path\":");
        JsText.AppendString(json, Path);
        json.Append(",\"message\":");
        JsText.AppendString(json, Message);
        json.Append('}');
    }
}

/// <summary>The result of <see cref="Manifest.Validate(System.Text.Json.JsonElement)"/>.</summary>
public sealed class ValidationResult
{
    /// <summary>Creates a result. <see cref="Ok"/> is true when there are no errors.</summary>
    public ValidationResult(IEnumerable<Issue> errors, IEnumerable<Issue> warnings)
    {
        Errors = Freeze(errors, nameof(errors));
        Warnings = Freeze(warnings, nameof(warnings));
    }

    /// <summary>True when there are no errors. Warnings never make a manifest invalid.</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>Errors, in the order of the rules.</summary>
    public IReadOnlyList<Issue> Errors { get; }

    /// <summary>Warnings, in the order of the rules.</summary>
    public IReadOnlyList<Issue> Warnings { get; }

    /// <summary>
    /// The result as compact JSON, in the same shape as the Python and Node.js libraries:
    /// <c>{"ok":...,"errors":[{"code":...,"path":...,"message":...}],"warnings":[...]}</c>.
    /// </summary>
    public string ToJson()
    {
        var json = new StringBuilder();
        json.Append(Ok ? "{\"ok\":true,\"errors\":[" : "{\"ok\":false,\"errors\":[");
        AppendIssues(json, Errors);
        json.Append("],\"warnings\":[");
        AppendIssues(json, Warnings);
        json.Append("]}");
        return json.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToJson();

    private static void AppendIssues(StringBuilder json, IReadOnlyList<Issue> issues)
    {
        for (var i = 0; i < issues.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            issues[i].WriteJson(json);
        }
    }

    private static ReadOnlyCollection<Issue> Freeze(IEnumerable<Issue> issues, string name)
    {
        if (issues is null)
        {
            throw new ArgumentNullException(name);
        }

        var list = issues.ToList();
        if (list.Contains(null!))
        {
            throw new ArgumentException("an issue is null", name);
        }

        return list.AsReadOnly();
    }
}

/// <summary>Why the gate decided as it did.</summary>
public sealed class Reason
{
    /// <summary>Creates a reason. <paramref name="path"/> is null when the reason does not point into a document.</summary>
    public Reason(string code, Decision decision, string message, string? path = null)
    {
        if (!DecisionExtensions.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "not a BYOA decision");
        }

        Code = code ?? throw new ArgumentNullException(nameof(code));
        Decision = decision;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Path = path;
    }

    /// <summary>The stable reason code, such as <c>clip-too-long</c>. Programs should use this.</summary>
    public string Code { get; }

    /// <summary>The decision this reason asks for.</summary>
    public Decision Decision { get; }

    /// <summary>An explanation for people. It may change between versions.</summary>
    public string Message { get; }

    /// <summary>
    /// A JSON Pointer into the document the reason is about. Present only on the input-checking
    /// reasons (<c>manifest-invalid</c>, <c>grant-invalid</c>, <c>call-invalid</c>, <c>state-invalid</c>);
    /// null otherwise.
    /// </summary>
    public string? Path { get; }

    /// <inheritdoc />
    public override string ToString() =>
        Code + " (" + Decision.ToJsonName() + ")" + (Path is null ? "" : " at \"" + Path + "\"") + ": " + Message;

    internal void WriteJson(StringBuilder json)
    {
        json.Append("{\"code\":");
        JsText.AppendString(json, Code);
        json.Append(",\"decision\":");
        JsText.AppendString(json, Decision.ToJsonName());
        json.Append(",\"message\":");
        JsText.AppendString(json, Message);
        if (Path is not null)
        {
            json.Append(",\"path\":");
            JsText.AppendString(json, Path);
        }

        json.Append('}');
    }
}

/// <summary>The result of <see cref="Gate.CheckCall(System.Text.Json.JsonElement, System.Text.Json.JsonElement, System.Text.Json.JsonElement, System.Text.Json.JsonElement?, PurposeJudge?)"/>.</summary>
public sealed class CheckResult
{
    /// <summary>Creates a result.</summary>
    public CheckResult(Decision decision, IEnumerable<Reason> reasons)
    {
        if (!DecisionExtensions.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "not a BYOA decision");
        }

        if (reasons is null)
        {
            throw new ArgumentNullException(nameof(reasons));
        }

        var list = reasons.ToList();
        if (list.Contains(null!))
        {
            throw new ArgumentException("a reason is null", nameof(reasons));
        }

        Decision = decision;
        Reasons = list.AsReadOnly();
    }

    /// <summary>The overall decision: the most severe decision among the reasons, or <see cref="Decision.Allowed"/> when there are none.</summary>
    public Decision Decision { get; }

    /// <summary>Every reason, in the order of the rules.</summary>
    public IReadOnlyList<Reason> Reasons { get; }

    /// <summary>
    /// The result as compact JSON, in the same shape as the Python and Node.js libraries:
    /// <c>{"decision":...,"reasons":[{"code":...,"decision":...,"message":...,"path":...}]}</c>
    /// (<c>path</c> only where the reason has one).
    /// </summary>
    public string ToJson()
    {
        var json = new StringBuilder();
        json.Append("{\"decision\":");
        JsText.AppendString(json, Decision.ToJsonName());
        json.Append(",\"reasons\":[");
        for (var i = 0; i < Reasons.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            Reasons[i].WriteJson(json);
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToJson();
}
