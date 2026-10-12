using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Help;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Mcp;

static class Program
{
    public static async Task<int> Main()
    {
        var server = new StdioMcpServer();
        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }
}

sealed class StdioMcpServer
{
    readonly TextReader _input;
    readonly TextWriter _output;
    AgentPipeClient? _wallet;
    string _clientName = "MCP client";
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _writeGate = new(1, 1);
    readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Task, byte> _pending = new();
    readonly SemaphoreSlim _connectGate = new(1, 1);

    public StdioMcpServer(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task RunAsync()
    {
        while (await _input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(Error(null, -32700, "The MCP request was not valid JSON.")).ConfigureAwait(false);
                continue;
            }

            if (node is JsonArray)
            {
                await WriteAsync(Error(null, -32600, "Batch messages are not supported; send one JSON object per line.")).ConfigureAwait(false);
                continue;
            }
            if (node is not JsonObject request)
            {
                await WriteAsync(Error(null, -32600, "The MCP request must be a JSON object.")).ConfigureAwait(false);
                continue;
            }

            if (IsCancellation(request))
            {
                CancelRequest(request);
                continue;
            }

            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var requestKey = request.ContainsKey("id") ? RequestKey(request["id"]) : null;
            if (requestKey is not null) _requests[requestKey] = requestCancellation;
            var work = ProcessAsync(request, requestCancellation, requestKey);
            _pending.TryAdd(work, 0);
            _ = work.ContinueWith(completed => _pending.TryRemove(completed, out _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // EOF is a normal end of the MCP input stream. Let already-dispatched requests finish first; cancelling
        // them here turns a valid final tool call into a spurious -32800 response.
        while (_pending.Count > 0)
        {
            try { await Task.WhenAll(_pending.Keys).ConfigureAwait(false); } catch { }
        }
        _stop.Cancel();
        _wallet?.Dispose();
        _writeGate.Dispose();
        _connectGate.Dispose();
        _stop.Dispose();
    }

    async Task ProcessAsync(JsonObject request, CancellationTokenSource requestCancellation, string? requestKey)
    {
        try
        {
            var response = await HandleAsync(request, requestCancellation.Token).ConfigureAwait(false);
            if (request.ContainsKey("id") && response is not null)
                await WriteAsync(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            if (request.ContainsKey("id"))
                await WriteAsync(Error(request["id"], -32800, "The MCP request was cancelled.")).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (request.ContainsKey("id"))
                await WriteAsync(Error(request["id"], -32603, "The bridge could not handle this request. Try the tool again.")).ConfigureAwait(false);
        }
        finally
        {
            if (requestKey is not null) _requests.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestKey, requestCancellation));
            requestCancellation.Dispose();
        }
    }

    async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken ct)
    {
        if (request["jsonrpc"] is not JsonValue version || !version.TryGetValue<string>(out var jsonrpc) || jsonrpc != "2.0")
            return request.ContainsKey("id") ? Error(request["id"], -32600, "The MCP request must use JSON-RPC 2.0.") : null;
        var method = String(request, "method");
        var id = request["id"]?.DeepClone();
        if (request.TryGetPropertyValue("params", out var rawParams) && rawParams is not null && rawParams is not JsonObject)
            return request.ContainsKey("id") ? Error(id, -32602, "The MCP request parameters must be a JSON object.") : null;
        var parameters = request["params"] as JsonObject ?? new JsonObject();
        if (method is "notifications/initialized" or "notifications/cancelled") return null;
        if (method is null) return request.ContainsKey("id") ? Error(id, -32600, "The MCP request needs a method.") : null;

        switch (method)
        {
            case "initialize":
                _clientName = String(parameters["clientInfo"] as JsonObject, "name") ?? "MCP client";
                await ConnectWalletAsync(ct).ConfigureAwait(false);
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "byoa-wallet", ["version"] = WalletPaths.Version },
                    ["instructions"] = "This bridge manages the visitor's running BYOA wallet. Start with the help tool; the bridge never starts the wallet or returns API keys.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = ToolCatalog.All() });
            case "tools/call":
                return await ToolCallAsync(id, parameters, ct).ConfigureAwait(false);
            case "shutdown":
                return Result(id, JsonValue.Create((string?)null));
            default:
                return request.ContainsKey("id") ? Error(id, -32601, "The MCP method '" + method + "' is not supported.") : null;
        }
    }

    static bool IsCancellation(JsonObject request) =>
        String(request, "method") == "notifications/cancelled";

    void CancelRequest(JsonObject request)
    {
        if (request["params"] is not JsonObject parameters) return;
        var id = parameters["requestId"];
        if (id is null) return;
        _requests.TryGetValue(RequestKey(id), out var cancellation);
        cancellation?.Cancel();
    }

    static string RequestKey(JsonNode? id) => id?.ToJsonString() ?? "null";

    async Task<JsonObject?> ToolCallAsync(JsonNode? id, JsonObject parameters, CancellationToken ct)
    {
        var name = String(parameters, "name");
        if (name is null) return Error(id, -32602, "tools/call needs a tool name.");
        if (parameters.TryGetPropertyValue("arguments", out var rawArguments) && rawArguments is not null && rawArguments is not JsonObject)
            return Error(id, -32602, "tools/call arguments must be a JSON object.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        JsonObject result;
        if (!ToolCatalog.Contains(name))
        {
            result = AgentPipeProtocol.Error("Unknown wallet tool '" + name + "'. Call tools/list and choose one of the listed tools.");
        }
        else if (_wallet is null && name == "help")
        {
            var topic = arguments["topic"]?.GetValue<string>();
            try
            {
                result = new JsonObject { ["ok"] = true, ["message"] = topic is null ? HelpCatalog.OverviewForAgent() : HelpCatalog.Get(topic) };
            }
            catch (KeyNotFoundException e)
            {
                result = AgentPipeProtocol.Error(e.Message);
            }
        }
        else
        {
            var wallet = _wallet;
            if (wallet is null || wallet.IsDisconnected)
                wallet = await ConnectWalletAsync(ct).ConfigureAwait(false);

            if (wallet is null)
                result = AgentPipeProtocol.Error("The wallet is not running. Open BYOA Agent wallet and try again; this bridge will not start it.");
            else
            {
                var call = await wallet.CallDetailedAsync(name, arguments, ct).ConfigureAwait(false);
                if (call.Failure == PipeCallFailure.Closed && !ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine("The wallet closed the idle connection; reconnecting");
                    wallet = await ConnectWalletAsync(ct).ConfigureAwait(false);
                    if (wallet is null)
                        result = AgentPipeProtocol.Error("The wallet is not running. Open BYOA Agent wallet and try again; this bridge will not start it.");
                    else
                        result = (await wallet.CallDetailedAsync(name, arguments, ct).ConfigureAwait(false)).Result;
                }
                else result = call.Result;
            }
        }

        var ok = result["ok"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
        var text = ToolText(result, ok);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        var toolResult = new JsonObject { ["content"] = content, ["isError"] = !ok, ["structuredContent"] = result.DeepClone() };
        return Result(id, toolResult);
    }

    async Task<AgentPipeClient?> ConnectWalletAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_wallet is { IsDisconnected: false }) return _wallet;
            _wallet?.Dispose();
            _wallet = await AgentPipeClient.ConnectAsync(_clientName, ct).ConfigureAwait(false);
            return _wallet;
        }
        finally { _connectGate.Release(); }
    }

    static string ToolText(JsonObject result, bool ok)
    {
        if (!ok) return result["error"]?.GetValue<string>() ?? "The wallet rejected the request. Ask the visitor to check the wallet.";
        if (result["message"] is JsonValue message && message.TryGetValue<string>(out var text) && result.Count <= 3) return text;
        var clone = (JsonObject)result.DeepClone();
        clone.Remove("ok");
        if (clone.Count == 1 && clone["message"] is JsonValue only && only.TryGetValue<string>(out var one)) return one;
        return clone.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    async Task WriteAsync(JsonObject message)
    {
        // MCP stdio is one JSON-RPC object per line. Never write diagnostics to stdout.
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    static JsonObject Result(JsonNode? id, JsonNode? value) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = value?.DeepClone() };
    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    static string? String(JsonObject? obj, string name) => obj?[name] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
}

enum PipeCallFailure
{
    None,
    Closed,
    TimedOut,
    Cancelled,
}

sealed class AgentPipeClient : IDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly SemaphoreSlim _roundTrip = new(1, 1);
    int _disconnected;

    AgentPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    public static async Task<AgentPipeClient?> ConnectAsync(string clientName, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", WalletPaths.McpPipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            var hello = new JsonObject
            {
                ["type"] = AgentPipeProtocol.Hello,
                ["name"] = clientName,
                ["executablePath"] = Environment.ProcessPath,
            };
            await Framing.WriteAsync(pipe, AgentPipeProtocol.Serialize(hello), ct).ConfigureAwait(false);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var bytes = await Framing.ReadAsync(pipe, 256 * 1024, helloTimeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), helloTimeout.Token).ConfigureAwait(false);
            var answer = bytes is null ? null : AgentPipeProtocol.Parse(bytes);
            if (answer is null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            // Keep the connection even when the visitor said Not now: tool calls receive the wallet's plain refusal.
            return new AgentPipeClient(pipe);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        catch (OperationCanceledException) { await pipe.DisposeAsync().ConfigureAwait(false); return null; }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async Task<JsonObject> CallAsync(string operation, JsonObject arguments, CancellationToken ct = default)
        => (await CallDetailedAsync(operation, arguments, ct).ConfigureAwait(false)).Result;

    public async Task<(JsonObject Result, PipeCallFailure Failure)> CallDetailedAsync(string operation, JsonObject arguments,
        CancellationToken ct = default)
    {
        var acquired = false;
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            // The stdio server may dispatch several MCP calls concurrently, but this pipe has one ordered byte
            // stream. A timeout/cancellation disposes it so the wallet closes any window owned by this connection;
            // the bridge reconnects lazily before the next call.
            await _roundTrip.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            if (IsDisconnected) return (AgentPipeProtocol.Error("The wallet connection is closed; reconnecting."), PipeCallFailure.Closed);
            await Framing.WriteAsync(_pipe, AgentPipeProtocol.Serialize(new JsonObject
            {
                ["type"] = AgentPipeProtocol.Request,
                ["id"] = requestId,
                ["operation"] = operation,
                ["arguments"] = arguments.DeepClone(),
            }), ct).ConfigureAwait(false);
            for (var frame = 0; frame < 8; frame++)
            {
                var bytes = await Framing.ReadAsync(_pipe, Framing.MaxIncoming, ct)
                    .WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
                if (bytes is null) throw new EndOfStreamException();
                var response = AgentPipeProtocol.Parse(bytes) ?? throw new InvalidDataException("The wallet returned an invalid response.");
                if (response["id"] is JsonValue id && id.TryGetValue<string>(out var value) && value == requestId)
                {
                    return (response, PipeCallFailure.None);
                }
                // A response left over from a cancelled call is harmless only when it is bounded and discarded.
            }

            throw new InvalidDataException("The wallet returned too many responses for other requests.");
        }
        catch (TimeoutException)
        {
            Disconnect();
            return (AgentPipeProtocol.Error("The wallet took too long to answer. The connection was closed; try the tool again."), PipeCallFailure.TimedOut);
        }
        catch (OperationCanceledException)
        {
            // Even cancellation while waiting for the round-trip lock invalidates the caller's expectation of
            // ordered replies. Close the pipe so no later frame can belong to this cancelled request.
            Disconnect();
            return (AgentPipeProtocol.Error("The wallet call was cancelled."), PipeCallFailure.Cancelled);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException)
        {
            Disconnect();
            return (AgentPipeProtocol.Error("The wallet closed the idle connection; reconnecting."), PipeCallFailure.Closed);
        }
        finally { if (acquired) _roundTrip.Release(); }
    }

    void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        // Closing the connection also closes any wallet consent/key/model window that this connection opened.
        try { _pipe.Dispose(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Disconnect();
        _roundTrip.Dispose();
    }

}

static class ToolCatalog
{
    public static JsonArray All() => new(JsonArrayTools());

    public static bool Contains(string name) => JsonArrayTools().Any(tool =>
        tool["name"] is JsonValue value && value.TryGetValue<string>(out var candidate) && candidate == name);

    static JsonNode[] JsonArrayTools() =>
    [
        Tool("help", "Explain the BYOA wallet and how to manage it. Call this first when you do not know the wallet. Optional topic returns one embedded article.",
            [Prop("topic", "string", "Optional help topic. Without it, return a short overview and the topic list.")]),
        Tool("status", "Show skills, sources and their state, source order, connected-site counts by state unless you have Full access, today's spend, and whether this agent has Full access. Without Full access no site names are returned.", []),
        Tool("list_runtimes", "Scan this computer for LM Studio, Ollama, and confirmed OpenAI-compatible runtimes. The wallet contacts each listed address to list models. For endpoints added by an agent, return only running state, whether it answered, model count, latency, and HTTP status; never return remote model names.", []),
        Tool("list_downloadable_models", "List speech models the wallet knows how to download, their size, languages, hardware needs, and download state.", []),
        Tool("add_source", "Add a model source to the wallet. Prefer a local runtime. The wallet contacts a loopback runtime to list models; the standard LM Studio and Ollama ports can be added immediately, while another computer or a different loopback port is shown to the visitor in a wallet window before it is saved, scanned, or contacted. After adding, call test_source and set_order.",
            [Prop("kind", "string", "Source kind: lmstudio, ollama, openai-compatible, or openrouter."), Prop("model", "string", "The model id offered by the runtime or provider."), Prop("endpoint", "string", "Optional complete http or https base endpoint. Loopback is this computer; another host needs visitor confirmation and plain http is not encrypted."), ArrayProp("skills", "Optional skills this source serves, usually text.generate."), ArrayProp("languages", "Optional primary language tags; an empty list means any language."), Prop("label", "string", "Optional friendly name shown in the wallet; the wallet cleans it and adds the endpoint location.")], ["kind", "model"]),
        Tool("update_source", "Edit an added source. Omitted optional parameters stay unchanged; built-in sources cannot be edited. Changing to another computer needs the visitor's confirmation in a wallet window before the setting changes.",
            [Prop("source_id", "string", "The source id returned by add_source or shown by status."), Prop("kind", "string", "Optional replacement source kind."), Prop("model", "string", "Optional replacement model id."), Prop("endpoint", "string", "Optional replacement endpoint."), ArrayProp("skills", "Optional replacement skill list."), ArrayProp("languages", "Optional replacement language list."), Prop("label", "string", "Optional replacement friendly label.")], ["source_id"]),
        Tool("remove_source", "Remove an added source from wallet settings and its route stops. A source with a saved key needs Full access and asks the visitor whether to delete or keep that key. Built-in sources must be disabled instead.", [Prop("source_id", "string", "The added source id to remove.")], ["source_id"]),
        Tool("enable_source", "Enable or disable a source without removing it. Changing a built-in speech or paid source needs Full access; an added model source can be changed after normal agent approval.", [Prop("source_id", "string", "The source id."), Prop("enabled", "boolean", "True to enable the source; false to skip it in routing.")], ["source_id", "enabled"]),
        Tool("test_source", "Make one tiny test call through the wallet's source test path. Return ok or not ok, latency, and the first 80 answer characters only for a loopback or visitor-confirmed source; never return an answer from an unconfirmed remote endpoint.", [Prop("source_id", "string", "The source id to test.")], ["source_id"]),
        Tool("download_model", "Download one speech model into the wallet's own models folder. Only one download runs at a time, at least 5 GB must remain free, and Activity records that the agent started it; the wallet shows progress and this tool returns when it finishes.", [Prop("model_id", "string", "The downloadable model id from list_downloadable_models.")], ["model_id"]),
        Tool("remove_model", "Ask the visitor in a wallet window before removing a model that this wallet downloaded into its own models folder. The wallet refuses files outside its active models folder, redirected folders, or files it did not download; the window warns if this is the only listed source for a skill.", [Prop("model_id", "string", "The model id from list_downloadable_models. Use that list first and choose a model whose downloaded value is true.")], ["model_id"]),
        Tool("set_order", "Set the fallback order for one skill and optional language. The first source is tried first. The wallet refuses unknown ids or an order that leaves the skill with no source able to serve it, and records the change.", [Prop("skill", "string", "Skill name, such as audio.transcribe or text.generate."), Prop("language", "string", "Optional language tag; omit for the default route."), ArrayProp("source_ids", "The complete ordered list of source ids that can serve this skill.")], ["skill", "source_ids"]),
        Tool("request_api_key_entry", "Open a key field in the wallet so the visitor types the provider key. Prefer this: the agent never sees the key. A second request for the same source is refused, and an agent may open at most three such fields per hour.", [Prop("source_id", "string", "The API-key source id.")], ["source_id"]),
        Tool("set_api_key", "Write an API key supplied by the visitor into the wallet. Prefer request_api_key_entry; this value is never returned or read back. Any key supplied through this tool, including a new key, needs Full access.", [Prop("source_id", "string", "The API-key source id."), Prop("key", "string", "The key handed to you by the visitor; do not guess or print it.")], ["source_id", "key"]),
        Tool("activity", "Show recent wallet activity newest first. Without Full access, site origins become stable site hash labels and free-text notes are omitted; with Full access, site names and full entries are returned. It never includes audio, prompts, transcripts, or keys.", [Prop("limit", "integer", "Optional number of entries, 1 to 500; default 50."), Prop("since", "string", "Optional ISO date/time; return entries at or after it.")]),
        Tool("list_sites", "Full access only: list site names and access states.", []),
        Tool("set_site_access", "Full access only: pause, resume, revoke, block, or unblock a site's access. Visitor consent cannot be bypassed.", [Prop("origin", "string", "The site's exact origin, such as https://example.com."), Prop("action", "string", "One of revoke, block, unblock, pause, resume, or allow. Allow still requires the visitor consent window.")], ["origin", "action"]),
        Tool("set_limits", "Full access only: change wallet or site budgets/call limits. Ask the visitor first in normal use.", [Prop("origin", "string", "Optional exact site origin for site-specific limits."), Prop("wallet_per_day", "string", "Optional whole-wallet daily money limit, for example 1.00 USD."), Prop("site_per_day", "string", "Optional default site daily money limit."), Prop("site_calls_per_day", "integer", "Optional default calls per site per day."), Prop("default_session_budget", "string", "Optional default budget per session."), Prop("budget", "string", "Optional site budget per session when origin is supplied."), Prop("per_day", "string", "Optional site daily money limit when origin is supplied."), Prop("calls_per_day", "integer", "Optional site daily call limit when origin is supplied.")]),
    ];

    static JsonObject Tool(string name, string description, (string Name, JsonObject Schema)[] properties, string[]? required = null)
    {
        var props = new JsonObject();
        foreach (var (propertyName, schema) in properties) props[propertyName] = schema;
        var input = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required is { Length: > 0 }) input["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = input };
    }

    static (string Name, JsonObject Schema) Prop(string name, string type, string description) => (name, new JsonObject { ["type"] = type, ["description"] = description });
    static (string Name, JsonObject Schema) ArrayProp(string name, string description) => (name, new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = new JsonObject { ["type"] = "string" } });
}
