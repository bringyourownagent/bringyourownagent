# Connecting your AI agent

The wallet includes `byoa-mcp.exe`, a small MCP bridge over a per-user named pipe. Add it to Claude Code, Codex, or OpenCode using the command shown on Connect your AI agent. The wallet must be running first; the bridge does not start it.


The first connection shows the visitor the announced agent name and the parent program path observed by the wallet. The announced name is only a label. The wallet identifies the agent from the parent image of the real `byoa-mcp.exe` client (or from the client image when its parent cannot be resolved); the path in the hello message is ignored. A program of the same Windows user can still start the bridge itself, so read the observed path before allowing it.

Allow gives these normal capabilities:

- manage sources and routes (you confirm any source on another computer);
- download models and, after your confirmation, delete them;
- write API keys (the agent cannot read them);
- read the activity log (site names only with Full access).

An endpoint on another computer is not saved, scanned, or contacted until you allow its wallet window. Plain HTTP is shown as not encrypted. `request_api_key_entry` is preferred: you type the key in the wallet and the agent sees only the saved result. Replacing an existing key and removing a keyed source need Full access and your confirmation about whether to keep the key.

Full access is a separate checkbox for site access, limits, paid approvals, and approving other agents. Without it, `status` returns only connected-site counts and `activity` replaces origins with stable `site-xxxx` labels and removes free-text notes. You can switch Full access off or Revoke the agent in Settings → Agents. Every agent change is recorded in Activity. “Not now” pauses that agent for one minute; it is not a block.
