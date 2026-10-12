using Byoa.Wallet.Sources;
using Byoa.Wallet.Settings;

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
    IReadOnlyList<RuntimeModel> Models);

/// <summary>One model the wallet knows how to fetch into its own models folder.</summary>
public sealed record DownloadableModelInfo(
    string Id,
    string Name,
    string Skill,
    long? SizeBytes,
    IReadOnlyList<string> Languages,
    string Hardware,
    bool Downloaded,
    string? OwnedPath,
    bool CanDownload = true);

public sealed record ModelDownloadProgress(string ModelId, double? Fraction, long? DoneBytes, long? TotalBytes);

public sealed record RegistryOperationResult(bool Ok, string Message, string? Id = null)
{
    public static RegistryOperationResult Success(string message, string? id = null) => new(true, message, id);
    public static RegistryOperationResult Failure(string message) => new(false, message);
}

public sealed record RegistryTestResult(bool Ok, string Message, string? Detail = null);

public sealed record ApiKeyEntryRequest(string SourceId, string SourceName, string Actor);

public sealed record RegistryChangedEvent(string Action, string? SourceId, string Actor);

public sealed record AgentIdentity(string Id, string Name, string ExecutablePath)
{
    public static AgentIdentity Create(string? name, string? executablePath) =>
        AgentIdentityFactory.Create(name, executablePath);
}

static class AgentIdentityFactory
{
    public static AgentIdentity Create(string? name, string? executablePath)
    {
        var display = string.IsNullOrWhiteSpace(name) ? "Unnamed AI agent" : name.Trim();
        var path = string.IsNullOrWhiteSpace(executablePath) ? "(path unavailable)" : executablePath.Trim();
        var bytes = System.Text.Encoding.UTF8.GetBytes(display.ToUpperInvariant() + "\n" + path.ToUpperInvariant());
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 12).ToLowerInvariant();
        return new AgentIdentity(id, display, path);
    }
}

public sealed record AgentConsentModel(AgentIdentity Identity, bool FullAccessWasPreviouslyGranted);

public enum AgentConsentDecision
{
    Allow,
    NotNow,
    Block,
}

public sealed record AgentConsentAnswer(AgentConsentDecision Decision, bool FullAccess);

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

/// <summary>Downloads and inspects only models owned by the active wallet.</summary>
public interface IWalletModelManager
{
    IReadOnlyList<DownloadableModelInfo> Models { get; }
    Task<RegistryOperationResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken ct);

    /// <summary>The manager must refuse paths outside the active wallet models directory.</summary>
    Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct);
}

public sealed class EmptyModelManager : IWalletModelManager
{
    public IReadOnlyList<DownloadableModelInfo> Models { get; } = [];
    public Task<RegistryOperationResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken ct) =>
        Task.FromResult(RegistryOperationResult.Failure("This wallet has no downloadable model catalog."));
    public Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct) =>
        Task.FromResult(RegistryOperationResult.Failure("The wallet will not remove model files automatically."));
}
