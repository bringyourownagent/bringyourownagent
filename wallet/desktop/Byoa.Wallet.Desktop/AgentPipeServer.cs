using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Help;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Serves the visitor's own MCP bridge. It owns no state: every operation is translated to WalletRegistry or the
/// existing engine, and every connection is identified before it can call a tool.
/// </summary>
sealed class AgentPipeServer : IDisposable
{
    static readonly HashSet<string> FullAccessTools = new(StringComparer.Ordinal)
    {
        "list_sites", "set_site_access", "set_limits",
    };

    readonly WalletServices _w;
    readonly IAgentConsentUi _ui;
    readonly CancellationTokenSource _stop = new();

    public AgentPipeServer(WalletServices services, IAgentConsentUi ui)
    {
        _w = services;
        _ui = ui;
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    async Task AcceptLoopAsync()
    {
        var name = WalletPaths.McpPipeName();
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        AgentSession? session = null;
        try
        {
            var helloBytes = await Framing.ReadAsync(pipe, 256 * 1024, _stop.Token).ConfigureAwait(false);
            var hello = helloBytes is null ? null : AgentPipeProtocol.Parse(helloBytes);
            if (hello is null || !string.Equals(String(hello, "type"), AgentPipeProtocol.Hello, StringComparison.Ordinal))
            {
                await SendAsync(pipe, AgentPipeProtocol.Error("The MCP bridge did not identify itself."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var identity = AgentIdentity.Create(String(hello, "name"), String(hello, "executablePath"));
            var access = await AuthorizeAsync(identity, _stop.Token).ConfigureAwait(false);
            session = new AgentSession(identity, access);
            await SendAsync(pipe, new JsonObject
            {
                ["type"] = AgentPipeProtocol.Response,
                ["id"] = "hello",
                ["ok"] = access.Allowed,
                ["fullAccess"] = access.FullAccess,
                ["error"] = access.Allowed ? null : access.Error,
            }, CancellationToken.None).ConfigureAwait(false);

            while (!_stop.IsCancellationRequested)
            {
                var bytes = await Framing.ReadAsync(pipe, Framing.MaxIncoming, _stop.Token).ConfigureAwait(false);
                if (bytes is null) break;
                var message = AgentPipeProtocol.Parse(bytes);
                if (message is null) continue;
                var requestId = message["id"]?.DeepClone();
                var response = await HandleAsync(session, message, _stop.Token).ConfigureAwait(false);
                response["type"] = AgentPipeProtocol.Response;
                response["requestId"] = requestId?.DeepClone();
                if (!response.ContainsKey("id")) response["id"] = requestId?.DeepClone();
                await SendAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // The MCP process went away or the wallet is shutting down.
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
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

        AgentConsentAnswer answer;
        try
        {
            answer = await _ui.AskAgentAsync(new AgentConsentModel(identity, existing?.FullAccess == true), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AgentAccess(false, false, "The wallet did not receive the visitor's answer. Open the wallet and try again.");
        }

        if (answer.Decision == AgentConsentDecision.NotNow)
            return new AgentAccess(false, false, "The visitor chose Not now. Ask the visitor to allow this agent in the wallet.");

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
                FullAccess = answer.Decision == AgentConsentDecision.Allow && answer.FullAccess,
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
            ? new AgentAccess(true, answer.FullAccess, null)
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
                "add_source" => AddSource(args, actor),
                "update_source" => UpdateSource(args, actor),
                "remove_source" => RemoveSource(args, actor),
                "enable_source" => EnableSource(args, actor),
                "test_source" => await TestSourceAsync(args, ct).ConfigureAwait(false),
                "download_model" => await DownloadModelAsync(args, actor, ct).ConfigureAwait(false),
                "remove_model" => await RemoveModelAsync(args, actor, ct).ConfigureAwait(false),
                "set_order" => SetOrder(args, actor),
                "request_api_key_entry" => await RequestKeyAsync(args, actor, ct).ConfigureAwait(false),
                "set_api_key" => await SetKeyAsync(args, actor).ConfigureAwait(false),
                "activity" => Activity(args),
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
    }

    AgentAccess CurrentAccess(AgentSession session)
    {
        var record = _w.Store.Snapshot().Agents.FirstOrDefault(a => a.Id == session.Identity.Id);
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
        var sites = settings.Sites.Select(site => SiteSummary(site)).ToArray();
        var result = Success();
        result["skills"] = new JsonArray(_w.Registry.ServedSkills.Select(skill => (JsonNode?)JsonValue.Create(skill)).ToArray());
        result["sources"] = SourcesJson(_w.Registry.ListSources());
        result["chains"] = ChainsJson(_w.Registry.ListChains());
        result["connectedSites"] = new JsonArray(sites.Select(x => (JsonNode)x).ToArray());
        result["today"] = new JsonObject { ["spent"] = Money.Show(spent), ["calls"] = calls };
        result["fullAccess"] = access.FullAccess;
        return result;
    }

    async Task<JsonObject> RuntimesAsync(CancellationToken ct)
    {
        var runtimes = await _w.Registry.ListRuntimesAsync(ct).ConfigureAwait(false);
        var result = Success();
        result["runtimes"] = new JsonArray(runtimes.Select(r => (JsonNode)new JsonObject
        {
            ["kind"] = r.Kind,
            ["endpoint"] = r.Endpoint,
            ["running"] = r.Running,
            ["state"] = r.Running ? "running" : "not running — start it or download it",
            ["models"] = new JsonArray(r.Models.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["name"] = m.Name, ["sizeBytes"] = m.SizeBytes, ["loaded"] = m.Loaded,
            }).ToArray()),
        }).ToArray());
        return result;
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

    JsonObject AddSource(JsonObject args, string actor)
    {
        var draft = new SourceDraft(String(args, "kind"), StringOrNull(args, "model"), StringOrNull(args, "endpoint"),
            StringsOrNull(args, "skills"), StringsOrNull(args, "languages"), StringOrNull(args, "label"));
        return Operation(_w.Registry.AddSource(draft, actor));
    }

    JsonObject UpdateSource(JsonObject args, string actor)
    {
        var update = new SourceUpdate(StringOrNull(args, "kind"), StringOrNull(args, "model"), StringOrNull(args, "endpoint"),
            StringsOrNull(args, "skills"), StringsOrNull(args, "languages"), StringOrNull(args, "label"));
        return Operation(_w.Registry.UpdateSource(String(args, "source_id"), update, actor));
    }

    JsonObject RemoveSource(JsonObject args, string actor) => Operation(_w.Registry.RemoveSource(String(args, "source_id"), actor));

    JsonObject EnableSource(JsonObject args, string actor) => Operation(_w.Registry.EnableSource(String(args, "source_id"), Bool(args, "enabled"), actor));

    async Task<JsonObject> TestSourceAsync(JsonObject args, CancellationToken ct)
    {
        var result = await _w.Registry.TestSourceAsync(String(args, "source_id"), ct).ConfigureAwait(false);
        return Test(result);
    }

    async Task<JsonObject> DownloadModelAsync(JsonObject args, string actor, CancellationToken ct)
    {
        var result = await _w.Registry.DownloadModelAsync(String(args, "model_id"), null, actor, ct).ConfigureAwait(false);
        return Operation(result);
    }

    async Task<JsonObject> RemoveModelAsync(JsonObject args, string actor, CancellationToken ct)
    {
        var result = await _w.Registry.RemoveDownloadedModelAsync(String(args, "model_id"), actor, ct).ConfigureAwait(false);
        return Operation(result);
    }

    JsonObject SetOrder(JsonObject args, string actor) => Operation(_w.Registry.SetOrder(String(args, "skill"), StringOrNull(args, "language"),
        Strings(args, "source_ids"), actor));

    async Task<JsonObject> RequestKeyAsync(JsonObject args, string actor, CancellationToken ct) =>
        Operation(await _w.Registry.RequestApiKeyEntryAsync(String(args, "source_id"), actor, ct).ConfigureAwait(false));

    async Task<JsonObject> SetKeyAsync(JsonObject args, string actor) =>
        Operation(await _w.Registry.SetApiKeyAsync(String(args, "source_id"), String(args, "key"), actor).ConfigureAwait(false));

    JsonObject Activity(JsonObject args)
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
        result["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
        {
            ["at"] = e.At, ["origin"] = e.Origin, ["kind"] = e.Kind, ["skill"] = e.Skill,
            ["outcome"] = e.Outcome, ["source"] = e.Source, ["fallback"] = e.Fallback, ["cost"] = e.Cost,
            ["actor"] = e.Actor, ["change"] = e.Change, ["note"] = e.Note,
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

    JsonObject Operation(RegistryOperationResult result) => result.Ok ? Success(result.Message, result.Id) : Failure(result.Message);
    static JsonObject Test(RegistryTestResult result) => result.Ok ? Success(result.Message, result.Detail) : Failure(result.Message);

    static JsonObject Success(string? text = null, string? id = null)
    {
        var result = new JsonObject { ["ok"] = true };
        if (text is not null) result["message"] = text;
        if (id is not null) result["id"] = id;
        return result;
    }

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
