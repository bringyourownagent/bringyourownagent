using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
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
    AgentPipeClient? _wallet;
    string _clientName = "MCP client";

    public async Task RunAsync()
    {
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException)
            {
                await WriteAsync(Error(null, -32700, "The MCP request was not valid JSON.")).ConfigureAwait(false);
                continue;
            }

            if (request is null) continue;
            var response = await HandleAsync(request).ConfigureAwait(false);
            if (response is not null) await WriteAsync(response).ConfigureAwait(false);
        }

        _wallet?.Dispose();
    }

    async Task<JsonObject?> HandleAsync(JsonObject request)
    {
        var method = String(request, "method");
        var id = request["id"]?.DeepClone();
        var parameters = request["params"] as JsonObject ?? new JsonObject();
        if (method is "notifications/initialized" or "notifications/cancelled") return null;

        switch (method)
        {
            case "initialize":
                _clientName = String(parameters["clientInfo"] as JsonObject, "name") ?? "MCP client";
                await ConnectWalletAsync().ConfigureAwait(false);
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
                return await ToolCallAsync(id, parameters).ConfigureAwait(false);
            case "shutdown":
                return Result(id, JsonValue.Create((string?)null));
            default:
                return Error(id, -32601, "The MCP method '" + method + "' is not supported.");
        }
    }

    async Task<JsonObject?> ToolCallAsync(JsonNode? id, JsonObject parameters)
    {
        var name = String(parameters, "name");
        if (name is null) return Error(id, -32602, "tools/call needs a tool name.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        JsonObject result;
        if (_wallet is null && name == "help")
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
        else if (_wallet is null)
        {
            result = AgentPipeProtocol.Error("The wallet is not running. Open BYOA Agent wallet and try again; this bridge will not start it.");
        }
        else
        {
            result = await _wallet.CallAsync(name, arguments).ConfigureAwait(false);
        }

        var ok = result["ok"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
        var text = ToolText(result, ok);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        var toolResult = new JsonObject { ["content"] = content, ["isError"] = !ok, ["structuredContent"] = result.DeepClone() };
        return Result(id, toolResult);
    }

    async Task ConnectWalletAsync()
    {
        _wallet?.Dispose();
        _wallet = await AgentPipeClient.ConnectAsync(_clientName).ConfigureAwait(false);
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

    static async Task WriteAsync(JsonObject message)
    {
        // MCP stdio is one JSON-RPC object per line. Never write diagnostics to stdout.
        await Console.Out.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    static JsonObject Result(JsonNode? id, JsonNode? value) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = value?.DeepClone() };
    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    static string? String(JsonObject? obj, string name) => obj?[name] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
}

sealed class AgentPipeClient : IDisposable
{
    readonly NamedPipeClientStream _pipe;

    AgentPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<AgentPipeClient?> ConnectAsync(string clientName)
    {
        var pipe = new NamedPipeClientStream(".", WalletPaths.McpPipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(1000).ConfigureAwait(false);
            var hello = new JsonObject
            {
                ["type"] = AgentPipeProtocol.Hello,
                ["name"] = clientName,
                ["executablePath"] = ParentExecutablePath(),
            };
            await Framing.WriteAsync(pipe, AgentPipeProtocol.Serialize(hello)).ConfigureAwait(false);
            var bytes = await Framing.ReadAsync(pipe, 256 * 1024).ConfigureAwait(false);
            var answer = bytes is null ? null : AgentPipeProtocol.Parse(bytes);
            if (answer is null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            // Keep the connection even when the visitor said Not now: tool calls receive the wallet's plain refusal.
            return new AgentPipeClient(pipe);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async Task<JsonObject> CallAsync(string operation, JsonObject arguments)
    {
        try
        {
            await Framing.WriteAsync(_pipe, AgentPipeProtocol.Serialize(new JsonObject
            {
                ["type"] = AgentPipeProtocol.Request,
                ["id"] = Guid.NewGuid().ToString("N"),
                ["operation"] = operation,
                ["arguments"] = arguments.DeepClone(),
            })).ConfigureAwait(false);
            var bytes = await Framing.ReadAsync(_pipe).ConfigureAwait(false);
            return bytes is null ? AgentPipeProtocol.Error("The wallet connection closed. Open the wallet and try again.")
                : AgentPipeProtocol.Parse(bytes) ?? AgentPipeProtocol.Error("The wallet returned an invalid response.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException)
        {
            return AgentPipeProtocol.Error("The wallet connection closed. Open BYOA Agent wallet and try again.");
        }
    }

    public void Dispose() => _pipe.Dispose();

    static string ParentExecutablePath()
    {
        try
        {
            var basic = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(GetCurrentProcess(), 0, ref basic,
                (uint)Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status == 0 && basic.InheritedFromUniqueProcessId != IntPtr.Zero)
                return QueryPath((uint)basic.InheritedFromUniqueProcessId.ToInt64()) ?? "(path unavailable)";

            var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return "(path unavailable)";
            var current = Process.GetCurrentProcess().Id;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            try
            {
                if (!Process32First(snapshot, ref entry)) return "(path unavailable)";
                do
                {
                    if (entry.ProcessId == current)
                    {
                        var path = QueryPath(entry.ParentProcessId);
                        if (path is not null) return path;
                        var parent = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                        if (Process32First(snapshot, ref parent))
                        {
                            do
                            {
                                if (parent.ProcessId == entry.ParentProcessId)
                                    return parent.ExeFile;
                            } while (Process32Next(snapshot, ref parent));
                        }

                        return "process " + entry.ParentProcessId;
                    }
                } while (Process32Next(snapshot, ref entry));
            }
            finally { CloseProcessHandle(snapshot); }
        }
        catch
        {
            return "(path unavailable)";
        }
        return "(path unavailable)";
    }

    static string? QueryPath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseProcessHandle(handle); }
    }

    [Flags] enum SnapshotFlags : uint { Process = 0x00000002 }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
    }
    const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseProcessHandle(IntPtr handle);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, uint informationLength, out uint returnLength);

}

static class ToolCatalog
{
    public static JsonArray All() => new(JsonArrayTools());

    static JsonNode[] JsonArrayTools() =>
    [
        Tool("help", "Explain the BYOA wallet and how to manage it. Call this first when you do not know the wallet. Optional topic returns one embedded article.",
            [Prop("topic", "string", "Optional help topic. Without it, return a short overview and the topic list.")]),
        Tool("status", "Show skills, sources and their state, source order, connected site names and states, today's spend, and whether this agent has Full access.", []),
        Tool("list_runtimes", "Scan this computer for LM Studio, Ollama, and configured OpenAI-compatible runtimes. Shows running or not running and each model offered.", []),
        Tool("list_downloadable_models", "List speech models the wallet knows how to download, their size, languages, hardware needs, and download state.", []),
        Tool("add_source", "Add a model source to the wallet. Prefer a local runtime. After adding, call test_source and set_order.",
            [Prop("kind", "string", "Source kind: lmstudio, ollama, openai-compatible, or openrouter."), Prop("model", "string", "The model id offered by the runtime or provider."), Prop("endpoint", "string", "Optional base endpoint, for example http://127.0.0.1:1234/v1."), ArrayProp("skills", "Optional skills this source serves, usually text.generate."), ArrayProp("languages", "Optional primary language tags; an empty list means any language."), Prop("label", "string", "Optional friendly name shown in the wallet.")], ["kind", "model"]),
        Tool("update_source", "Edit an added source. Omitted optional parameters stay unchanged; built-in sources cannot be edited.",
            [Prop("source_id", "string", "The source id returned by add_source or shown by status."), Prop("kind", "string", "Optional replacement source kind."), Prop("model", "string", "Optional replacement model id."), Prop("endpoint", "string", "Optional replacement endpoint."), ArrayProp("skills", "Optional replacement skill list."), ArrayProp("languages", "Optional replacement language list."), Prop("label", "string", "Optional replacement friendly label.")], ["source_id"]),
        Tool("remove_source", "Remove an added source from wallet settings and its route stops. Built-in sources must be disabled instead.", [Prop("source_id", "string", "The added source id to remove.")], ["source_id"]),
        Tool("enable_source", "Enable or disable a source without removing it.", [Prop("source_id", "string", "The source id."), Prop("enabled", "boolean", "True to enable the source; false to skip it in routing.")], ["source_id", "enabled"]),
        Tool("test_source", "Make one tiny test call through the wallet's source test path and return a plain pass or failure reason.", [Prop("source_id", "string", "The source id to test.")], ["source_id"]),
        Tool("download_model", "Download one speech model into the wallet's own models folder. The wallet shows progress; this tool returns when it finishes.", [Prop("model_id", "string", "The downloadable model id from list_downloadable_models.")], ["model_id"]),
        Tool("remove_model", "Remove a model that this wallet downloaded into its own models folder. This acts immediately on the visitor's request without another confirmation; the wallet refuses files outside its own active models folder or files it did not download.", [Prop("model_id", "string", "The model id from list_downloadable_models. Use that list first and choose a model whose downloaded value is true.")], ["model_id"]),
        Tool("set_order", "Set the fallback order for one skill and optional language. The first source is tried first.", [Prop("skill", "string", "Skill name, such as audio.transcribe or text.generate."), Prop("language", "string", "Optional language tag; omit for the default route."), ArrayProp("source_ids", "The complete ordered list of source ids.")], ["skill", "source_ids"]),
        Tool("request_api_key_entry", "Open a key field in the wallet so the visitor types the provider key. Prefer this: the agent never sees the key.", [Prop("source_id", "string", "The API-key source id.")], ["source_id"]),
        Tool("set_api_key", "Write an API key supplied by the visitor into the wallet. Prefer request_api_key_entry; this value is never returned or read back.", [Prop("source_id", "string", "The API-key source id."), Prop("key", "string", "The key handed to you by the visitor; do not guess or print it.")], ["source_id", "key"]),
        Tool("activity", "Show recent wallet activity newest first. It never includes audio, prompts, transcripts, or keys.", [Prop("limit", "integer", "Optional number of entries, 1 to 500; default 50."), Prop("since", "string", "Optional ISO date/time; return entries at or after it.")]),
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
