# My MCP: letting an agent manage the wallet

The wallet includes `byoa-mcp.exe`, a small MCP bridge over a per-user named pipe. Add it to Claude Code, Codex, or OpenCode using the command shown on My MCP. The wallet must be running first; the bridge does not start it.


The first connection shows the visitor the announced agent name and the parent program path observed by the wallet. The announced name is only a label. The wallet identifies the agent from the parent image of the real `byoa-mcp.exe` client; the path in the hello message is ignored. If the parent cannot be resolved, approval is for that connection only and the wallet says so. A program of the same Windows user can still start the bridge itself, so read the observed path before allowing it.

Approval is by the program that started the bridge. Another program of your Windows user that starts the bridge through the same program inherits that approval, with its Full access, and no window appears. This is the same trust as your own session. If the observed parent is a general-purpose interpreter or shell, the wallet says that anything started through it shares the approval and does not offer Full access on the first connection; you can switch it on later in Settings → My MCP.

Allow gives these normal capabilities:

- manage sources and routes (you confirm any source on another computer);
- ask you to type an API key into the wallet; the wallet never sends a key back to the agent;
- read the activity log (site names only with Full access).

An endpoint on another computer is not saved, scanned, or contacted until you allow its wallet window. The window shows the canonical punycode host and port, the addresses seen during confirmation, and whether the connection is encrypted. Plain HTTP is shown as not encrypted. The wallet contacts standard LM Studio/Ollama loopback ports while listing runtimes; a different loopback port needs the same confirmation, and model names and answers from any agent-added endpoint stay out of MCP results. `request_api_key_entry` is preferred: you type the key in the wallet and the agent sees only the saved result, even when replacing one. `set_api_key` always needs Full access because the value came from the agent. Removing a keyed source needs Full access and your confirmation about whether to keep the key.

Full access is a separate checkbox. It unlocks `list_sites` and `set_site_access` (site names and site access), `set_limits` (budgets and call limits), `set_api_key` (a key the agent hands to the wallet), and `remove_source` for a source with a saved key; it also lets `enable_source` change a built-in source. Without it, `status` returns only connected-site counts and `activity` replaces origins with stable `site-xxxx` labels and removes free-text notes. No tool lets an agent allow a site, approve a paid call or approve another agent, with or without Full access. You can switch Full access off or Revoke the agent in Settings → My MCP. Every agent change is recorded in Activity. “Not now” pauses that agent for one minute; it is not a block.
