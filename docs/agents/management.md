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
| Write a new API key | No | Yes for `request_api_key_entry`; no window for `set_api_key` when the visitor supplied the new key |
| Replace an existing API key | Yes | Yes for `request_api_key_entry`; direct write is allowed only with Full access |
| Remove a source without a saved key | No | No |
| Remove a source with a saved key | Yes | Yes: the visitor chooses Delete or Keep for the key |
| Enable or disable a built-in source | Yes | No |
| `list_sites`, `set_site_access`, `set_limits` | Yes | Site access still follows visitor consent; limits are recorded |
| Approve another agent | Yes | Yes, in the wallet |

The agent cannot read an API key. Without Full access, `status` returns only site counts and `activity` uses stable site hash labels rather than site names or free-text notes. A remote endpoint is always shown with its host and whether it is encrypted before the visitor confirms it. The same user's other programs are not a security boundary: the wallet displays the parent path it observed for the bridge so the visitor can make an informed choice.
