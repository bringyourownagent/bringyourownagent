using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Help;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Serves the visitor's own MCP bridge. It owns no state: every operation is translated to WalletRegistry or the
/// existing engine, and every connection is identified before it can call a tool.
/// </summary>
sealed class AgentPipeServer : IDisposable
{
    const int MaxAllowedServerInstances = 8;
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);
    static readonly HashSet<string> FullAccessTools = new(StringComparer.Ordinal)
    {
        "list_sites", "set_site_access", "set_limits", "set_api_key",
    };
    static readonly HashSet<string> NormalAccessTools = new(StringComparer.Ordinal)
    {
        "help", "status", "list_runtimes", "list_downloadable_models", "add_source", "update_source",
        "remove_source", "enable_source", "test_source", "download_model", "remove_model", "set_order",
        "request_api_key_entry", "activity",
    };

    /// <summary>One explicit classification for every tool. Tests compare it with the public MCP catalog.</summary>
    internal static IReadOnlyCollection<string> ClassifiedTools => NormalAccessTools.Concat(FullAccessTools).ToArray();

    internal static bool IsClassifiedTool(string operation) =>
        NormalAccessTools.Contains(operation) || FullAccessTools.Contains(operation);

    internal static bool RequiresFullAccess(string operation) => FullAccessTools.Contains(operation);

    internal static TimeSpan AuthorizedIdleTimeout => IdleTimeout;

    readonly WalletServices _w;
    readonly IAgentConsentUi _ui;
    readonly IAgentProcessInfo _processInfo;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _connectionSlots = new(MaxAllowedServerInstances, MaxAllowedServerInstances);
    readonly object _authorizationGate = new();
    readonly Dictionary<string, Task<AgentAccess>> _authorizing = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _notNow = new(StringComparer.Ordinal);

    public AgentPipeServer(WalletServices services, IAgentConsentUi ui, IAgentProcessInfo? processInfo = null)
    {
        _w = services;
        _ui = ui;
        _processInfo = processInfo ?? new WindowsAgentProcessInfo();
    }

    public void Start() => _ = Task.Run(async () =>
    {
        try { await AcceptLoopAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorLog.Write(WalletPaths.Home, e); }
    });

    async Task AcceptLoopAsync()
    {
        var name = WalletPaths.McpPipeName();
        while (!_stop.IsCancellationRequested)
        {
            if (!_connectionSlots.Wait(0))
            {
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
                continue;
            }

            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _connectionSlots.Release();
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _connectionSlots.Release();
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await ServeAsync(pipe).ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException) { ErrorLog.Write(WalletPaths.Home, e); }
                finally { _connectionSlots.Release(); }
            });
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        AgentSession? session = null;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var monitor = MonitorConnectionAsync(pipe, connection);
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            helloTimeout.CancelAfter(HelloTimeout);
            var helloBytes = await Framing.ReadAsync(pipe, 256 * 1024, helloTimeout.Token).ConfigureAwait(false);
            var hello = helloBytes is null ? null : AgentPipeProtocol.Parse(helloBytes);
            if (hello is null || !string.Equals(String(hello, "type"), AgentPipeProtocol.Hello, StringComparison.Ordinal))
            {
                await SendAsync(pipe, AgentPipeProtocol.Error("The MCP bridge did not identify itself."), connection.Token).ConfigureAwait(false);
                return;
            }

            ObservedAgentProcess? observed = null;
            for (var attempt = 0; attempt < 3 && observed is null; attempt++)
            {
                observed = _processInfo.Read(pipe.SafePipeHandle);
                if (observed is null && attempt < 2)
                    await Task.Delay(50, connection.Token).ConfigureAwait(false);
            }
            if (observed is null)
            {
                await SendAsync(pipe, AgentPipeProtocol.Error("The wallet could not identify the program that started this bridge. Try again."), connection.Token).ConfigureAwait(false);
                return;
            }
            if (!IsInstalledBridge(observed.ClientImagePath))
            {
                await SendAsync(pipe, AgentPipeProtocol.Error("This program is not the wallet's MCP bridge."), connection.Token).ConfigureAwait(false);
                return;
            }

            var identity = AgentIdentity.CreateObserved(String(hello, "name"), observed.ClientImagePath, observed.ParentImagePath,
                observed.ClientProcessId, observed.ClientStartTime);
            var access = await AuthorizeAsync(identity, connection.Token).ConfigureAwait(false);
            session = new AgentSession(identity, access);
            await SendAsync(pipe, new JsonObject
            {
                ["type"] = AgentPipeProtocol.Response,
                ["id"] = "hello",
                ["version"] = WalletPaths.Version,                  // so that a later bridge can tell an older wallet from a newer one
                ["ok"] = access.Allowed,
                ["fullAccess"] = access.FullAccess,
                ["error"] = access.Allowed ? null : access.Error,
            }, connection.Token).ConfigureAwait(false);

            while (!connection.IsCancellationRequested)
            {
                byte[]? bytes;
                try
                {
                    bytes = await Framing.ReadAsync(pipe, Framing.MaxIncoming, connection.Token)
                        .WaitAsync(IdleTimeout, connection.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    break;
                }
                if (bytes is null) break;
                var hasId = false;
                JsonNode? requestId = null;
                JsonObject? response;
                try
                {
                    var message = AgentPipeProtocol.Parse(bytes);
                    if (message is null)
                    {
                        response = Failure("The wallet expected one JSON object per message.");
                    }
                    else
                    {
                        hasId = message.ContainsKey("id");
                        requestId = hasId ? message["id"]?.DeepClone() : null;
                        response = await HandleAsync(session, message, connection.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    response = Failure("The wallet could not apply that request: " + e.GetType().Name + ". Check the arguments and try again.");
                }

                // A notification has no id property and must never receive a response. Explicit null remains a
                // request id and is echoed as null by the response envelope.
                if (!hasId || response is null) continue;
                response["type"] = AgentPipeProtocol.Response;
                // Keep the request id local. A JSON-RPC id of null is still an id and must remain null in the
                // response; storing it in a marker property would lose that distinction.
                response["id"] = requestId?.DeepClone();
                await SendAsync(pipe, response, connection.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // The MCP process went away or the wallet is shutting down.
        }
        finally
        {
            connection.Cancel();
            try { await monitor.ConfigureAwait(false); } catch { }
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task MonitorConnectionAsync(NamedPipeServerStream pipe, CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                await Task.Delay(250, connection.Token).ConfigureAwait(false);
                if (!pipe.IsConnected)
                {
                    connection.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static bool IsInstalledBridge(string? path)
    {
        var configured = Environment.GetEnvironmentVariable("BYOA_WALLET_MCP_PATH");
        return IsBridgeIn(path, string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured);
    }

    /// <summary>
    /// True when <paramref name="path"/> is <c>byoa-mcp.exe</c> inside <paramref name="walletFolder"/>. The wallet's own
    /// folder comes from <c>AppContext.BaseDirectory</c>, which ends in a backslash, while the folder of the
    /// bridge's image path does not: both are compared without a trailing separator.
    /// </summary>
    internal static bool IsBridgeIn(string? path, string walletFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "byoa-mcp.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var folder = walletFolder;
            if (string.Equals(Path.GetExtension(folder), ".exe", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder)!;
            static string Normal(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return string.Equals(Normal(Path.GetDirectoryName(path)!), Normal(folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    async Task<AgentAccess> AuthorizeAsync(AgentIdentity identity, CancellationToken ct)
    {
        var existing = _w.Store.Snapshot().Agents.FirstOrDefault(a => a.Id == identity.Id);
        if (existing is { Status: "blocked" })
            return new AgentAccess(false, false, "This agent is blocked in Settings → Agents. Ask the visitor to Revoke it or allow it there.");
        if (existing is { Status: "allowed" })
        {
            _w.Store.Update(s =>
            {
                var record = s.Agents.FirstOrDefault(a => a.Id == identity.Id);
                if (record is not null)
                {
                    record.LastUsed = DateTimeOffset.UtcNow;
                    record.Name = identity.Name;
                    record.ExecutablePath = identity.ExecutablePath;
                }
            });
            return new AgentAccess(true, existing.FullAccess, null);
        }

        Task<AgentAccess> task;
        lock (_authorizationGate)
        {
            if (_notNow.TryGetValue(identity.Id, out var until))
            {
                if (until > DateTimeOffset.UtcNow)
                    return new AgentAccess(false, false, "The visitor said Not now for this agent. Try again after a minute or ask the visitor in the wallet.");
                _notNow.Remove(identity.Id);
            }

            if (_authorizing.TryGetValue(identity.Id, out var pending))
                task = pending;
            else
            {
                task = AuthorizeNewAsync(identity, ct);
                _authorizing[identity.Id] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (_authorizationGate) _authorizing.Remove(identity.Id);
                }, TaskScheduler.Default);
            }
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    async Task<AgentAccess> AuthorizeNewAsync(AgentIdentity identity, CancellationToken ct)
    {
        // Recheck after another connection may have completed the approval while this one was queued.
        var existing = _w.Store.Snapshot().Agents.FirstOrDefault(a => a.Id == identity.Id);
        if (existing is { Status: "allowed" }) return new AgentAccess(true, existing.FullAccess, null);

        AgentConsentAnswer answer;
        try
        {
            answer = await _ui.AskAgentAsync(new AgentConsentModel(identity, existing?.FullAccess == true,
                identity.FullAccessAllowed), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AgentAccess(false, false, "The wallet did not receive the visitor's answer. Open the wallet and try again.");
        }

        if (answer.Error is not null)
            return new AgentAccess(false, false, answer.Error);

        if (answer.Decision == AgentConsentDecision.NotNow)
        {
            lock (_authorizationGate) _notNow[identity.Id] = DateTimeOffset.UtcNow.AddSeconds(60);
            return new AgentAccess(false, false, "The visitor chose Not now. Ask the visitor to allow this agent in the wallet.");
        }

        if (identity.SessionOnly)
        {
            _w.Activity.Add(new ActivityEntry
            {
                At = DateTimeOffset.UtcNow,
                Origin = "wallet",
                Kind = "agent",
                Outcome = answer.Decision == AgentConsentDecision.Allow ? "allowed" : "blocked",
                Actor = "you in the wallet",
                Change = (answer.Decision == AgentConsentDecision.Allow ? "Allowed " : "Blocked ") + identity.Name + " for this connection only",
                Note = identity.ObservedParentPath ?? "unknown parent: the wallet could not see which program started the bridge",
            });
            return answer.Decision == AgentConsentDecision.Allow
                ? new AgentAccess(true, answer.FullAccess && identity.FullAccessAllowed, null)
                : new AgentAccess(false, false, answer.Decision == AgentConsentDecision.Block
                    ? "The visitor blocked this agent for this connection."
                    : "The visitor chose Not now. Ask the visitor to allow this agent.");
        }

        _w.Store.Update(s =>
        {
            s.Agents.RemoveAll(a => a.Id == identity.Id);
            s.Agents.Add(new AgentRecord
            {
                Id = identity.Id,
                Name = identity.Name,
                ExecutablePath = identity.ExecutablePath,
                LastUsed = DateTimeOffset.UtcNow,
                ApprovedAt = answer.Decision == AgentConsentDecision.Allow ? DateTimeOffset.UtcNow : null,
                FullAccess = answer.Decision == AgentConsentDecision.Allow && answer.FullAccess && identity.FullAccessAllowed,
                Status = answer.Decision == AgentConsentDecision.Block ? "blocked" : "allowed",
            });
        });
        _w.Activity.Add(new ActivityEntry
        {
            At = DateTimeOffset.UtcNow,
            Origin = "wallet",
            Kind = "agent",
            Outcome = answer.Decision == AgentConsentDecision.Allow ? "allowed" : "blocked",
            Actor = "you in the wallet",
            Change = (answer.Decision == AgentConsentDecision.Allow ? "Allowed " : "Blocked ") + identity.Name,
            Note = identity.ExecutablePath,
        });
        return answer.Decision == AgentConsentDecision.Allow
            ? new AgentAccess(true, answer.FullAccess && identity.FullAccessAllowed, null)
            : new AgentAccess(false, false, "The visitor blocked this agent. Revoke the block in Settings → Agents to ask again.");
    }

    async Task<JsonObject> HandleAsync(AgentSession session, JsonObject message, CancellationToken ct)
    {
        var access = CurrentAccess(session);
        if (!access.Allowed)
            return Failure(session.Access.Allowed
                ? access.Error ?? "This agent is no longer approved to manage the wallet. Ask the visitor to allow it again in Settings → Agents."
                : session.Access.Error ?? "The visitor has not approved this agent.");
        if (!string.Equals(String(message, "type"), AgentPipeProtocol.Request, StringComparison.Ordinal))
            return Failure("The wallet expected an MCP tool request.");

        var operation = String(message, "operation");
        if (FullAccessTools.Contains(operation) && !access.FullAccess)
            return Failure("This action needs Full access. Ask the visitor to give you Full access in Settings → Agents.");

        if (!NormalAccessTools.Contains(operation) && !FullAccessTools.Contains(operation))
            return Failure("This wallet tool is not classified for agent access. Call tools/list and choose a listed tool.");

        if (message.TryGetPropertyValue("arguments", out var rawArguments) &&
            rawArguments is not null && rawArguments is not JsonObject)
            return Failure("The 'arguments' parameter must be a JSON object.");
        var args = message["arguments"] as JsonObject ?? new JsonObject();
        var actor = "agent: " + session.Identity.Name;
        try
        {
            return operation switch
            {
                "help" => Help(args),
                "status" => Status(access),
                "list_runtimes" => await RuntimesAsync(ct).ConfigureAwait(false),
                "list_downloadable_models" => DownloadableModels(),
                "add_source" => await AddSourceAsync(args, actor, session, ct).ConfigureAwait(false),
                "update_source" => await UpdateSourceAsync(args, actor, session, ct).ConfigureAwait(false),
                "remove_source" => await RemoveSourceAsync(args, actor, access, ct).ConfigureAwait(false),
                "enable_source" => EnableSource(args, actor, access),
                "test_source" => await TestSourceAsync(args, ct).ConfigureAwait(false),
                "download_model" => await DownloadModelAsync(args, actor, ct).ConfigureAwait(false),
                "remove_model" => await RemoveModelAsync(args, actor, ct).ConfigureAwait(false),
                "set_order" => SetOrder(args, actor),
                "request_api_key_entry" => await RequestKeyAsync(args, actor, access, session, ct).ConfigureAwait(false),
                "set_api_key" => await SetKeyAsync(args, actor, access).ConfigureAwait(false),
                "activity" => Activity(args, access),
                "list_sites" => ListSites(),
                "set_site_access" => SetSiteAccess(args, actor),
                "set_limits" => SetLimits(args, actor),
                _ => Failure("Unknown wallet tool '" + operation + "'. Call tools/list and choose one of the listed tools."),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            return Failure("The wallet could not apply that request: " + e.Message);
        }
        catch (CryptographicException)
        {
            return Failure("Windows could not protect the wallet data for this user profile. Try again in the wallet.");
        }
        catch (IOException)
        {
            return Failure("The wallet could not write its data folder. Check the wallet home and try again.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failure("The wallet could not apply that request: " + e.GetType().Name + ". Check the arguments and try again.");
        }
    }

    AgentAccess CurrentAccess(AgentSession session)
    {
        var record = _w.Store.Snapshot().Agents.FirstOrDefault(a => a.Id == session.Identity.Id);
        if (session.Identity.SessionOnly) return session.Access;
        return record is { Status: "allowed" }
            ? new AgentAccess(true, record.FullAccess, null)
            : new AgentAccess(false, false, "This agent is no longer approved. Ask the visitor to allow it again in Settings → Agents.");
    }

    JsonObject Help(JsonObject args)
    {
        var topic = StringOrNull(args, "topic");
        try
        {
            var text = topic is null ? HelpCatalog.OverviewForAgent() : HelpCatalog.Get(topic);
            return Success(text);
        }
        catch (KeyNotFoundException e) { return Failure(e.Message); }
    }

    JsonObject Status(AgentAccess access)
    {
        var settings = _w.Store.Snapshot();
        var (spent, calls) = _w.Activity.Today(DateTimeOffset.UtcNow);
        var result = Success();
        result["skills"] = new JsonArray(_w.Registry.ServedSkills.Select(skill => (JsonNode?)JsonValue.Create(skill)).ToArray());
        result["sources"] = SourcesJson(_w.Registry.ListSources());
        result["chains"] = ChainsJson(_w.Registry.ListChains());
        if (access.FullAccess)
        {
            var sites = settings.Sites.Select(site => SiteSummary(site)).ToArray();
            result["connectedSites"] = new JsonArray(sites.Select(x => (JsonNode)x).ToArray());
        }
        else
        {
            result["connectedSites"] = new JsonArray(settings.Sites
                .GroupBy(site => SiteState(site), StringComparer.Ordinal)
                .Select(group => (JsonNode)new JsonObject { ["state"] = group.Key, ["count"] = group.Count() }).ToArray());
        }
        result["today"] = new JsonObject { ["spent"] = Money.Show(spent), ["calls"] = calls };
        result["fullAccess"] = access.FullAccess;
        return result;
    }

    async Task<JsonObject> RuntimesAsync(CancellationToken ct)
    {
        var runtimes = await _w.Registry.ListRuntimesAsync(ct).ConfigureAwait(false);
        var settings = _w.Store.Snapshot();
        var result = Success();
        var runtimeNodes = new List<JsonNode>();
        foreach (var runtime in runtimes)
        {
            var agentAdded = settings.Sources.Any(s =>
                s.AddedByAgent &&
                (s.Kind is SourceKinds.LmStudio or SourceKinds.Ollama or SourceKinds.OpenAiCompatible) &&
                string.Equals(s.Kind, runtime.Kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ProvidersEndpoint(s.Endpoint), ProvidersEndpoint(runtime.Endpoint), StringComparison.OrdinalIgnoreCase));
            var visible = !agentAdded && (EndpointPolicy.Check(runtime.Endpoint).IsLoopback || settings.Sources.Any(s =>
                s.EndpointConfirmed && string.Equals(ProvidersEndpoint(s.Endpoint), ProvidersEndpoint(runtime.Endpoint), StringComparison.OrdinalIgnoreCase)));
            runtimeNodes.Add(new JsonObject
            {
                ["kind"] = runtime.Kind,
                ["endpoint"] = runtime.Endpoint,
                ["running"] = runtime.Running,
                ["state"] = runtime.Running ? "running" : "not running — start it or download it",
                ["answered"] = runtime.Running,
                ["modelCount"] = runtime.Models.Count,
                ["latencyMs"] = runtime.LatencyMs,
                ["httpStatus"] = runtime.HttpStatus,
                ["models"] = visible
                    ? new JsonArray(runtime.Models.Select(m => (JsonNode)new JsonObject
                    {
                        ["id"] = m.Id, ["name"] = m.Name, ["sizeBytes"] = m.SizeBytes, ["loaded"] = m.Loaded,
                    }).ToArray())
                    : new JsonArray(),
            });
        }
        result["runtimes"] = new JsonArray(runtimeNodes.ToArray());
        return result;
    }

    static string ProvidersEndpoint(string? endpoint)
    {
        var clean = endpoint?.Trim().TrimEnd('/') ?? "";
        return clean.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? clean[..^3].TrimEnd('/') : clean;
    }

    JsonObject DownloadableModels()
    {
        var result = Success();
        result["models"] = new JsonArray(_w.Registry.ListDownloadableModels().Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id, ["name"] = m.Name, ["skill"] = m.Skill, ["sizeBytes"] = m.SizeBytes,
            ["languages"] = new JsonArray(m.Languages.Select(language => (JsonNode?)JsonValue.Create(language)).ToArray()), ["hardware"] = m.Hardware,
            ["downloaded"] = m.Downloaded, ["canDownload"] = m.CanDownload,
        }).ToArray());
        return result;
    }

    async Task<JsonObject> AddSourceAsync(JsonObject args, string actor, AgentSession session, CancellationToken ct)
    {
        var draft = new SourceDraft(String(args, "kind"), StringOrNull(args, "model"), StringOrNull(args, "endpoint"),
            StringsOrNull(args, "skills"), StringsOrNull(args, "languages"), StringOrNull(args, "label"));
        return Operation(await _w.Registry.AddSourceAsync(draft, actor, session.Identity.Name, ct).ConfigureAwait(false));
    }

    async Task<JsonObject> UpdateSourceAsync(JsonObject args, string actor, AgentSession session, CancellationToken ct)
    {
        var update = new SourceUpdate(StringOrNull(args, "kind"), StringOrNull(args, "model"), StringOrNull(args, "endpoint"),
            StringsOrNull(args, "skills"), StringsOrNull(args, "languages"), StringOrNull(args, "label"));
        return Operation(await _w.Registry.UpdateSourceAsync(String(args, "source_id"), update, actor, session.Identity.Name, ct).ConfigureAwait(false));
    }

    async Task<JsonObject> RemoveSourceAsync(JsonObject args, string actor, AgentAccess access, CancellationToken ct) =>
        Operation(await _w.Registry.RemoveSourceAsync(String(args, "source_id"), actor, access.FullAccess, ct).ConfigureAwait(false));

    JsonObject EnableSource(JsonObject args, string actor, AgentAccess access)
    {
        var id = String(args, "source_id");
        var source = _w.Registry.ListSources().FirstOrDefault(s => s.Id == id);
        if (source is { BuiltIn: true } && !access.FullAccess)
            return Failure("Enabling or disabling a built-in source needs Full access. Ask the visitor to give you Full access in Settings → Agents.");
        return Operation(_w.Registry.EnableSource(id, Bool(args, "enabled"), actor));
    }

    async Task<JsonObject> TestSourceAsync(JsonObject args, CancellationToken ct)
    {
        var id = String(args, "source_id");
        var source = _w.Registry.ListSources().FirstOrDefault(s => s.Id == id);
        if (source is null) return Failure("There is no source with id '" + id + "'. Add it first.");
        var watch = Stopwatch.StartNew();
        var result = await _w.Registry.TestSourceAsync(id, ct).ConfigureAwait(false);
        result = result with { LatencyMs = watch.ElapsedMilliseconds };
        var setting = _w.Store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting?.AddedByAgent == true)
        {
            // Agent-added endpoints are useful for a health check, but their model output is never relayed over
            // MCP, even when the endpoint is loopback. Settings tests remain allowed to show the answer.
            var safe = Test(result with { Detail = null });
            safe["answered"] = result.Ok;
            safe["httpStatus"] = null;
            return safe;
        }
        var endpoint = EndpointPolicy.Check(source.Endpoint);
        var confirmed = _w.Store.Snapshot().Sources.FirstOrDefault(s => s.Id == id)?.EndpointConfirmed == true;
        if (result.Ok && !endpoint.IsLoopback && !confirmed)
        {
            var answer = Success("Source answered; the wallet did not return its remote response.");
            if (result.LatencyMs is { } remoteLatency) answer["latencyMs"] = remoteLatency;
            return answer;
        }
        return Test(result);
    }

    async Task<JsonObject> DownloadModelAsync(JsonObject args, string actor, CancellationToken ct)
    {
        var model = _w.Registry.ListDownloadableModels().FirstOrDefault(m => m.Id == String(args, "model_id"));
        if (model is null) return Failure("There is no downloadable model with that id. Call list_downloadable_models first.");
        var result = await _w.Registry.DownloadModelAsync(String(args, "model_id"), null, actor, ct).ConfigureAwait(false);
        return Operation(result, "modelId");
    }

    async Task<JsonObject> RemoveModelAsync(JsonObject args, string actor, CancellationToken ct)
    {
        var model = _w.Registry.ListDownloadableModels().FirstOrDefault(m => m.Id == String(args, "model_id"));
        if (model is null) return Failure("There is no downloadable model with that id. Call list_downloadable_models first.");
        if (!model.Downloaded || string.IsNullOrWhiteSpace(model.OwnedPath))
            return Failure("That model is not a downloaded file owned by this wallet, so there is nothing to remove.");
        var note = _w.Registry.ListSources().Count(source => source.BuiltIn && source.Skills.Contains(model.Skill, StringComparer.Ordinal)) == 1
            ? "This is the only listed source for " + model.Skill + ". The skill may be unavailable until you add another source."
            : null;
        bool confirmed;
        try { confirmed = await _w.Ui.ConfirmModelDeleteAsync(model, note, ct, agentInitiated: true).ConfigureAwait(false); }
        catch (RegistryPromptBusyException e) { return Failure(e.Message); }
        if (!confirmed) return Failure("The visitor declined to delete this model.");
        var result = await _w.Registry.RemoveDownloadedModelAsync(String(args, "model_id"), actor, ct).ConfigureAwait(false);
        return Operation(result, "modelId");
    }

    JsonObject SetOrder(JsonObject args, string actor) => Operation(_w.Registry.SetOrder(String(args, "skill"), StringOrNull(args, "language"),
        Strings(args, "source_ids"), actor));

    async Task<JsonObject> RequestKeyAsync(JsonObject args, string actor, AgentAccess access, AgentSession session, CancellationToken ct) =>
        Operation(await _w.Registry.RequestApiKeyEntryAsync(String(args, "source_id"), actor, ct,
            access.FullAccess, visitorInitiated: false, agentId: session.Identity.Id).ConfigureAwait(false));

    async Task<JsonObject> SetKeyAsync(JsonObject args, string actor, AgentAccess access) =>
        Operation(await _w.Registry.SetApiKeyAsync(String(args, "source_id"), String(args, "key"), actor, access.FullAccess, visitorInitiated: false).ConfigureAwait(false));

    JsonObject Activity(JsonObject args, AgentAccess access)
    {
        var limit = Math.Clamp(Int(args, "limit", 50), 1, 500);
        var sinceText = StringOrNull(args, "since");
        DateTimeOffset? since = null;
        DateTimeOffset parsed = default;
        if (sinceText is not null && !DateTimeOffset.TryParse(sinceText, out parsed))
            return Failure("The since value must be an ISO date/time, for example 2026-10-12T00:00:00Z.");
        else if (sinceText is not null) since = parsed;
        var entries = _w.Activity.Recent(500).Where(e => since is null || e.At >= since).Take(limit).ToArray();
        var result = Success();
        result["entries"] = new JsonArray(entries.Select(e => access.FullAccess
            ? (JsonNode)new JsonObject
            {
                ["at"] = e.At, ["origin"] = e.Origin, ["kind"] = e.Kind, ["skill"] = e.Skill,
                ["outcome"] = e.Outcome, ["source"] = e.Source, ["fallback"] = e.Fallback, ["cost"] = e.Cost,
                ["actor"] = e.Actor, ["change"] = e.Change, ["note"] = e.Note,
            }
            : new JsonObject
            {
                ["at"] = e.At, ["origin"] = SiteLabel(e.Origin), ["kind"] = e.Kind, ["skill"] = e.Skill,
                ["outcome"] = e.Outcome, ["cost"] = e.Cost,
            }).ToArray());
        return result;
    }

    JsonObject ListSites()
    {
        var result = Success();
        result["sites"] = new JsonArray(_w.Store.Snapshot().Sites.Select(site => (JsonNode)SiteSummary(site)).ToArray());
        return result;
    }

    JsonObject SetSiteAccess(JsonObject args, string actor)
    {
        var origin = String(args, "origin");
        if (!Origins.TryParse(origin, out _, out _))
            return Failure("The origin must be a clean https://host[:port] origin, or the wallet's allowed loopback http origin.");
        var action = String(args, "action").ToLowerInvariant();
        switch (action)
        {
            case "revoke": _w.Engine.Revoke(origin, "changed by " + actor); break;
            case "block": _w.Engine.Block(origin, true); break;
            case "unblock": _w.Engine.Block(origin, false); break;
            case "pause": _w.Engine.Pause(origin); break;
            case "resume":
                _w.Store.Update(s =>
                {
                    var site = s.Site(origin);
                    if (site is not null) { site.Paused = false; site.PausedAt = null; site.PauseReason = null; }
                });
                _w.Activity.Add(new ActivityEntry { At = DateTimeOffset.UtcNow, Origin = origin, Kind = "change", Outcome = "ok", Actor = actor, Change = "Resumed site" });
                break;
            case "allow": return Failure("A site must be allowed through its visitor consent window; the agent cannot create that consent.");
            default: return Failure("Use one of: allow, revoke, block, unblock, pause, resume.");
        }
        _w.Activity.Add(new ActivityEntry { At = DateTimeOffset.UtcNow, Origin = origin, Kind = "change", Outcome = "ok", Actor = actor, Change = "Site access: " + action });
        return Success("Site access changed: " + action + ".");
    }

    JsonObject SetLimits(JsonObject args, string actor)
    {
        var origin = StringOrNull(args, "origin");
        if (args.ContainsKey("origin") && origin is null)
            return Failure("The origin parameter must be a string.");
        if (origin is not null && !Origins.TryParse(origin, out _, out _))
            return Failure("The origin must be a clean https://host[:port] origin, or the wallet's allowed loopback http origin.");
        var changed = new List<string>();
        _w.Store.Update(s =>
        {
            if (args.ContainsKey("wallet_per_day")) { s.Limits.WalletPerDay = String(args, "wallet_per_day"); changed.Add("wallet daily budget"); }
            if (args.ContainsKey("site_per_day")) { s.Limits.SitePerDay = String(args, "site_per_day"); changed.Add("site daily budget"); }
            if (args.ContainsKey("site_calls_per_day")) { s.Limits.SiteCallsPerDay = Int(args, "site_calls_per_day", s.Limits.SiteCallsPerDay); changed.Add("site daily calls"); }
            if (args.ContainsKey("default_session_budget")) { s.Limits.DefaultSessionBudget = String(args, "default_session_budget"); changed.Add("default session budget"); }
            if (origin is not null)
            {
                var site = s.Site(origin);
                if (site is null) { site = new SiteRecord { Origin = origin }; s.Sites.Add(site); }
                if (args.ContainsKey("budget")) { site.Budget = String(args, "budget"); changed.Add("site session budget"); }
                if (args.ContainsKey("per_day")) { site.PerDay = String(args, "per_day"); changed.Add("site daily budget"); }
                if (args.ContainsKey("calls_per_day")) { site.CallsPerDay = Int(args, "calls_per_day", s.Limits.SiteCallsPerDay); changed.Add("site daily calls"); }
            }
        });
        if (changed.Count == 0) return Failure("Tell the wallet which limit to change.");
        _w.Activity.Add(new ActivityEntry { At = DateTimeOffset.UtcNow, Origin = origin ?? "wallet", Kind = "change", Outcome = "ok", Actor = actor, Change = "Changed " + string.Join(", ", changed) });
        return Success("Changed " + string.Join(", ", changed) + ".");
    }

    JsonObject Operation(RegistryOperationResult result, string? applicationIdName = null)
    {
        if (!result.Ok) return Failure(result.Message);
        var response = Success(result.Message);
        if (result.Id is not null) response[applicationIdName ?? "sourceId"] = result.Id;
        return response;
    }

    static JsonObject Test(RegistryTestResult result)
    {
        if (!result.Ok) return Failure(result.Message);
        var output = result.Detail is null ? null : CleanResult(result.Detail, 80);
        var answer = Success(result.Message);
        if (output is not null) answer["answer"] = output;
        if (result.LatencyMs is { } latency) answer["latencyMs"] = latency;
        return answer;
    }

    static JsonObject Success(string? text = null, string? id = null)
    {
        var result = new JsonObject { ["ok"] = true };
        if (text is not null) result["message"] = text;
        if (id is not null) result["id"] = id;
        return result;
    }

    static string CleanResult(string value, int limit)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= limit ? cleaned : cleaned[..limit];
    }

    static string Size(long? bytes) => bytes is null ? "size unknown" :
        bytes.Value >= 1_000_000_000 ? (bytes.Value / 1_000_000_000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB" :
        Math.Ceiling(bytes.Value / 1_000_000d).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";

    static JsonObject Failure(string text) => new() { ["ok"] = false, ["error"] = text };

    static JsonArray SourcesJson(IReadOnlyList<RegistrySourceInfo> sources) => new(sources.Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id, ["name"] = s.Name, ["kind"] = s.Kind, ["model"] = s.Model, ["endpoint"] = s.Endpoint,
        ["skills"] = new JsonArray(s.Skills.Select(skill => (JsonNode?)JsonValue.Create(skill)).ToArray()), ["languages"] = new JsonArray(s.Languages.Select(language => (JsonNode?)JsonValue.Create(language)).ToArray()),
        ["runs"] = s.Runs, ["providerKind"] = s.ProviderKind, ["paid"] = s.Paid, ["enabled"] = s.Enabled,
        ["builtIn"] = s.BuiltIn, ["hasApiKey"] = s.HasApiKey, ["state"] = s.State, ["stateText"] = s.StateText,
    }).ToArray());

    static JsonArray ChainsJson(IReadOnlyList<RegistryChainInfo> chains) => new(chains.Select(c => (JsonNode)new JsonObject
    {
        ["skill"] = c.Skill, ["language"] = c.Language, ["sourceIds"] = new JsonArray(c.OrderedSourceIds.Select(sourceId => (JsonNode?)JsonValue.Create(sourceId)).ToArray()),
    }).ToArray());

    JsonObject SiteSummary(SiteRecord site)
    {
        var name = site.Origin;
        if (Uri.TryCreate(site.Origin, UriKind.Absolute, out var uri)) name = uri.Host;
        var state = site.Status.ToString().ToLowerInvariant();
        if (site.Paused || _w.Engine.PausedNow(site) is not null) state = "paused";
        if (_w.Engine.ActiveSessions(site.Origin) > 0) state = "connected";
        return new JsonObject { ["name"] = name, ["state"] = state };
    }

    string SiteState(SiteRecord site)
    {
        if (site.Paused || _w.Engine.PausedNow(site) is not null) return "paused";
        if (_w.Engine.ActiveSessions(site.Origin) > 0) return "connected";
        return site.Status.ToString().ToLowerInvariant();
    }

    string SiteLabel(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return "site-unknown";
        var key = _w.Store.Read(s => s.SiteActivityLabelKey);
        if (key is null || !TryDecodeLabelKey(key, out var bytes))
        {
            _w.Store.Update(s =>
            {
                if (s.SiteActivityLabelKey is null || !TryDecodeLabelKey(s.SiteActivityLabelKey, out _))
                    s.SiteActivityLabelKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            });
            key = _w.Store.Read(s => s.SiteActivityLabelKey);
            if (!TryDecodeLabelKey(key, out bytes)) return "site-unknown";
        }

        return HmacSiteLabel(origin, bytes);
    }

    internal static string HmacSiteLabel(string origin, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return "site-" + Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(origin)), 0, 4).ToLowerInvariant();
    }

    static bool TryDecodeLabelKey(string? value, out byte[] bytes)
    {
        try
        {
            bytes = value is null ? [] : Convert.FromBase64String(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    static string String(JsonObject obj, string name) => StringOrNull(obj, name) ?? throw new ArgumentException("Tell the wallet the '" + name + "' parameter.");
    static string? StringOrNull(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
    static bool Bool(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<bool>(out var value) ? value : throw new ArgumentException("The '" + name + "' parameter must be true or false.");
    static int Int(JsonObject obj, string name, int defaultValue = 0) => obj[name] is null ? defaultValue : obj[name] is JsonValue v && v.TryGetValue<int>(out var value) ? value : throw new ArgumentException("The '" + name + "' parameter must be a number.");
    static IReadOnlyList<string> Strings(JsonObject obj, string name) => StringsOrNull(obj, name) ?? throw new ArgumentException("Tell the wallet the '" + name + "' parameter as a list.");
    static IReadOnlyList<string>? StringsOrNull(JsonObject obj, string name) => obj[name] is JsonArray array ? array.OfType<JsonValue>().Select(v => v.GetValue<string>()).ToArray() : null;

    static async Task SendAsync(Stream stream, JsonObject message, CancellationToken ct) => await Framing.WriteAsync(stream, AgentPipeProtocol.Serialize(message), ct).ConfigureAwait(false);

    public void Dispose() => _stop.Cancel();

    sealed record AgentAccess(bool Allowed, bool FullAccess, string? Error);
    sealed record AgentSession(AgentIdentity Identity, AgentAccess Access);
}
