# BYOA wallet

The wallet gives a website or your own AI agent carefully limited access to AI that you choose. The wallet stays on your computer, checks every request, follows your source order, applies your limits, and records a short activity entry. A site receives an answer or “AI unavailable”; it does not receive your API key or the name of the source that answered.

The wallet serves speech to text now. Text generation is available when a text source is installed. Pictures and video are planned for later.

## For your AI agent

The MCP tools manage sources, models, routes, and activity. Start with `help`, then `status` or `list_runtimes`. To add a local model, choose a running runtime model, call `add_source`, `test_source`, and `set_order`. For a provider key, use `request_api_key_entry`: the visitor types the key in the wallet, and the agent never sees it. Source changes are allowed by default. Site access, limits, paid approvals, and other agent approvals need Full access granted by the visitor in Settings → Agents.

The wallet must already be running. `byoa-mcp.exe` never starts it and never holds settings or secrets itself.
