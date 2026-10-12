# Agent management: what an agent can and cannot do

The visitor first chooses **Allow** in the wallet's agent-consent window. Full access is off unless the visitor checks it in that same window or later in Settings → Agents. The wallet remains the only holder of settings and secrets.

| Tool or operation | Full access needed? | Visitor window shown? |
|---|---:|---:|
| `help`, `status`, `list_runtimes`, `list_downloadable_models`, `activity` | No | No |
| Add or update a loopback source | No | No |
| Add or update a source on another computer | No | Yes, before save or probing |
| `test_source`, `set_order`, edit an added source, enable/disable an added source | No | No, except any endpoint confirmation already required |
| Download a model | No | No; Activity records the start and progress is shown |
| Delete a downloaded model | No | Yes, confirm Delete |
| Write or replace an API key typed by the visitor | No | Yes, through `request_api_key_entry`; the agent never sees the key |
| Supply an API key through `set_api_key` | Yes | No; Full access is required for every agent-supplied key |
| Remove a source without a saved key | No | No |
| Remove a source with a saved key | Yes | Yes: the visitor chooses Delete or Keep for the key |
| Enable or disable a built-in source | Yes | No |
| `list_sites`, `set_site_access`, `set_limits` | Yes | Site access still follows visitor consent; limits are recorded |

The agent cannot read an API key. Without Full access, `status` returns only site counts and `activity` uses stable opaque HMAC labels rather than site names or free-text notes. A remote endpoint is canonicalised to its punycode host and port; the confirmation window shows whether it is encrypted and the addresses seen by the wallet. The wallet contacts standard LM Studio/Ollama loopback ports while listing runtimes, but it never relays model answers from an agent-added endpoint. A different loopback port is confirmed like another computer.

An endpoint on another computer is not saved, scanned, or contacted until the visitor allows it. A confirmed remote endpoint is treated as the visitor's own `home-server` and may serve a `local-only` route; that means the visitor explicitly trusted it, not that the network is private. At use time the wallet resolves a named endpoint again and refuses loopback, link-local, multicast, unspecified, or a drift from the confirmed public/private address class.

The wallet identifies an agent from the parent image path it observed for the real `byoa-mcp.exe`; the hello name is only a display label and its executable path is ignored. If the parent cannot be resolved, approval is for that connection only and the consent says so. A general-purpose interpreter or shell is called out because anything it starts through the bridge shares that path-based approval; the first consent does not offer Full access for such a parent, though the visitor can turn it on later in Settings → Agents. This is an honest residual same-user risk: another program belonging to the same Windows user that starts the bridge through the same parent program inherits that approval and its Full access without another window. It has the same trust as the visitor's own session. Records made before wallet-observed identities were introduced stop matching and are approved once again.
