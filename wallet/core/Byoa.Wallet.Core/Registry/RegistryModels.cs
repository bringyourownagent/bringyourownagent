using Byoa.Wallet.Sources;
using Byoa.Wallet.Settings;
using Microsoft.Win32.SafeHandles;

namespace Byoa.Wallet.Registry;

/// <summary>The editable part of a source. It deliberately has no API-key field.</summary>
public sealed record SourceDraft(
    string Kind,
    string? Model = null,
    string? Endpoint = null,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? Languages = null,
    string? Label = null);

/// <summary>Fields changed by an edit; null means leave the old value alone.</summary>
public sealed record SourceUpdate(
    string? Kind = null,
    string? Model = null,
    string? Endpoint = null,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? Languages = null,
    string? Label = null);

/// <summary>A safe description of a source. It contains a key-present bit, never a key or a decrypted secret.</summary>
public sealed record RegistrySourceInfo(
    string Id,
    string Name,
    string? Kind,
    string? Model,
    string? Endpoint,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Languages,
    string Runs,
    string ProviderKind,
    bool Paid,
    bool Enabled,
    bool BuiltIn,
    bool HasApiKey,
    string State,
    string StateText);

public sealed record RegistryChainInfo(string Skill, string? Language, IReadOnlyList<string> OrderedSourceIds);

public sealed record RegistryRuntimeInfo(
    string Kind,
    string Endpoint,
    bool Running,
    IReadOnlyList<RuntimeModel> Models,
    long? LatencyMs = null,
    int? HttpStatus = null);

public sealed record RegistryOperationResult(bool Ok, string Message, string? Id = null)
{
    public static RegistryOperationResult Success(string message, string? id = null) => new(true, message, id);
    public static RegistryOperationResult Failure(string message) => new(false, message);
}

    public sealed record RegistryTestResult(bool Ok, string Message, string? Detail = null, long? LatencyMs = null);

public sealed record ApiKeyEntryRequest(string SourceId, string SourceName, string Actor,
    bool AgentInitiated = false, bool FullAccess = false)
{
    /// <summary>The wallet-observed agent id; rate limits must not depend on an agent-controlled display name.</summary>
    public string? AgentId { get; init; }
}

public sealed record SecretDeletionRequest(string SourceId, string SourceName, string Actor);

public sealed record RegistryChangedEvent(string Action, string? SourceId, string Actor);

/// <summary>A wallet prompt could not enter its bounded queue before the visitor-facing wait expired.</summary>
public sealed class RegistryPromptBusyException : InvalidOperationException
{
    public RegistryPromptBusyException() : base("The visitor was busy answering another request; try again later.") { }
}

public sealed record AgentIdentity(string Id, string Name, string ExecutablePath)
{
    /// <summary>The image path of the bridge process observed by the wallet.</summary>
    public string ClientExecutablePath { get; init; } = "(path unavailable)";

    /// <summary>The image path of the process that launched the bridge, when the wallet could resolve it.</summary>
    public string? ObservedParentPath { get; init; }

    /// <summary>An unresolved parent is deliberately approved only for this pipe connection.</summary>
    public bool SessionOnly { get; init; }

    /// <summary>General-purpose launchers can start arbitrary programs under the same approval.</summary>
    public bool ParentIsInterpreter { get; init; }

    public bool FullAccessAllowed => !ParentIsInterpreter;

    public static AgentIdentity CreateObserved(string? displayName, string? clientPath, string? parentPath,
        uint? clientProcessId = null, DateTimeOffset? clientStartTime = null) =>
        AgentIdentityFactory.CreateObserved(displayName, clientPath, parentPath, clientProcessId, clientStartTime);

    public static bool IsInterpreterPath(string? path) => AgentIdentityFactory.IsInterpreterPath(path);
}

static class AgentIdentityFactory
{
    static readonly HashSet<string> InterpreterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "bun", "deno", "python", "pythonw", "py", "cmd", "powershell", "pwsh", "wscript", "cscript",
        "java", "bash", "sh", "wsl",
    };

    public static AgentIdentity CreateObserved(string? name, string? clientPath, string? parentPath,
        uint? clientProcessId, DateTimeOffset? clientStartTime)
    {
        var display = Clean(name, "Unnamed AI agent");
        var client = CleanPath(clientPath);
        var parent = CleanPath(parentPath);
        var sessionOnly = parent is null;
        var identityPath = parent ?? client ?? "(unknown parent)";
        // A missing parent must never collapse all bridges into one stored identity. The client PID and start
        // time make this value connection-specific; a missing start time gets a random nonce instead.
        var identityMaterial = parent is null
            ? identityPath + "|" + (clientProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
                + "|" + (clientStartTime?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
                + "|connection:" + Guid.NewGuid().ToString("N")
            : identityPath;
        var bytes = System.Text.Encoding.UTF8.GetBytes(identityMaterial.ToUpperInvariant());
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 12).ToLowerInvariant();
        return new AgentIdentity(id, display, identityPath)
        {
            ClientExecutablePath = client ?? "(path unavailable)",
            ObservedParentPath = parent,
            SessionOnly = sessionOnly,
            ParentIsInterpreter = IsInterpreterPath(parent),
        };
    }

    static string Clean(string? value, string fallback)
    {
        return SourceLabels.Clean(value) ?? fallback;
    }

    static string? CleanPath(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    public static bool IsInterpreterPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileNameWithoutExtension(path.Trim());
        return InterpreterNames.Contains(name);
    }
}

public sealed record ObservedAgentProcess(string? ClientImagePath, string? ParentImagePath,
    uint? ClientProcessId = null, DateTimeOffset? ClientStartTime = null);

/// <summary>Reads process facts from the wallet side of the management pipe. Tests inject a fake implementation.</summary>
public interface IAgentProcessInfo
{
    ObservedAgentProcess? Read(SafePipeHandle pipe);
}

public sealed record EndpointConfirmationRequest(string AgentName, string Endpoint, string Model, string? Label)
{
    public string CanonicalEndpoint { get; init; } = Endpoint;
    public string? Warning { get; init; }
    public IReadOnlyList<string> ResolvedAddresses { get; init; } = [];
    public string? AddressClass { get; init; }
    public bool ContainsNonAsciiHost { get; init; }
    public bool IsLoopback { get; init; }
}

public interface IEndpointConfirmationUi
{
    Task<bool> ConfirmEndpointAsync(EndpointConfirmationRequest request, CancellationToken ct);
}

public interface ISecretDeletionUi
{
    Task<bool> ConfirmSecretDeletionAsync(SecretDeletionRequest request, CancellationToken ct);
}

public sealed record AgentConsentModel(AgentIdentity Identity, bool FullAccessWasPreviouslyGranted,
    bool FullAccessAllowed = true);

public enum AgentConsentDecision
{
    Allow,
    NotNow,
    Block,
}

public sealed record AgentConsentAnswer(AgentConsentDecision Decision, bool FullAccess)
{
    public string? Error { get; init; }
}

/// <summary>Lets Core request an agent consent window without taking a dependency on Avalonia.</summary>
public interface IAgentConsentUi
{
    Task<AgentConsentAnswer> AskAgentAsync(AgentConsentModel model, CancellationToken ct);
}

/// <summary>The source test is supplied by the running wallet, so it can use the same source gate as its engine.</summary>
public interface IRegistrySourceTester
{
    Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct);
    Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, CancellationToken ct);
}

/// <summary>A source tester may distinguish the visitor pressing Test from a remote agent asking to test.</summary>
public interface IVisitorInitiatedSourceTester
{
    Task<RegistryTestResult> TestSourceAsync(string sourceId, bool visitorInitiated, CancellationToken ct);
    Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, bool visitorInitiated, CancellationToken ct);
}
