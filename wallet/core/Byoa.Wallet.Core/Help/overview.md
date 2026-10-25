# BYOA wallet

The wallet gives a website or your own AI agent carefully limited access to AI that you choose. The wallet stays on your computer, checks every request, follows your source order, applies your limits, and records a short activity entry. A site receives an answer or “AI unavailable”; it does not receive your API key or the name of the source that answered.

The wallet holds no model: it is a go-between. A site's request goes, after your yes, to a program on your computer that runs a model (the Llama app, LM Studio, Ollama, or any OpenAI-compatible server), or to your own paid key. The wallet serves speech to text and text generation; each needs a model added in My models — and if you have none yet, the Get started card there offers three free ways in. Pictures and video are planned for later.

## For your AI agent

The MCP tools manage sources, routes, and activity. Start with `help`, then `status` or `list_runtimes`. To add a local model, choose a running runtime model, call `add_source`, `test_source`, and `set_order`. For a provider key, use `request_api_key_entry`: the visitor types the key in the wallet, and the wallet never sends it back to the agent. Loopback sources can be added directly; a source on another computer is shown to the visitor and is saved or contacted only after the visitor confirms it. Source changes are allowed by default. Site access, limits, built-in sources and agent-supplied API keys need Full access granted by the visitor in Settings → My MCP. No tool lets an agent approve a site, a paid call or another agent. Without Full access, status reports only connected-site counts and activity uses stable site labels instead of origins.

The wallet must already be running. `byoa-mcp.exe` never starts it and never holds settings or secrets itself.
