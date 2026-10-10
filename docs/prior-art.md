# Related work

As of October 2026. Several efforts cover parts of what BYOA proposes; none of them is a neutral, skill-based standard that also covers local models.

| Work | What it does | How BYOA differs |
|---|---|---|
| [Sign in with ChatGPT](https://flaviocopes.com/sign-in-with-chatgpt/) (OpenAI, expanded at DevDay, 29 September 2026) | Third-party apps can spend a ChatGPT Plus or Pro plan allowance; the user sets a weekly cap per app | One vendor. Text only on this route (no audio or transcription). Hosted and commercial apps by application to OpenAI |
| [OpenRouter OAuth PKCE](https://openrouter.ai/docs/guides/overview/auth/oauth) | A site obtains a key tied to the user's OpenRouter account | One aggregator. The key ends up in the site's code; limits live on the key, not in a protocol |
| [window.ai](https://github.com/alexanderatallah/window.ai) (2023) | A browser extension that let sites call the user's chosen model with the user's keys | Closest in spirit; text generation only, no budgets or purpose, not maintained since 2024 |
| [Puter.js "user pays"](https://docs.puter.com/user-pays-model/) | Users cover their own AI use through a Puter account | One platform with its own account, not the user's subscriptions or local models |
| [MCP sampling](https://modelcontextprotocol.info/docs/concepts/sampling/) | A server asks the client to run a completion on the client's model, with human review | For MCP clients, not websites; text completions only |
| [WebMCP](https://www.openhermit.com/blog/navigator-modelcontext-2026) | A page exposes tools to the browser's agent | The reverse direction; complementary to BYOA |
| [Chrome Prompt API](https://developer.chrome.com/docs/ai/prompt-api) | Pages use the browser's built-in on-device model | One built-in model; no choice of provider, no subscriptions or keys |
| [Brave Leo BYOM](https://en.wikipedia.org/wiki/Brave_Leo) | The browser's assistant can use the user's own model | For the browser's own assistant, not for sites |
| [Hugging Face OAuth](https://huggingface.co/docs/hub/spaces-oauth) | Spaces can use the signed-in user's token | One platform |
| Agent payments: x402, AP2, ACP, [Cloudflare Wallets](https://www.helpnetsecurity.com/2026/08/05/cloudflare-wallets-for-ai-agents/) | Agents pay merchants under spending limits | About paying for goods, not lending a site the user's models |

## The term "BYOA"

"Bring your own agent" is already used in the industry for bringing an agent you chose into someone else's platform — coding agents in IDEs ([Android Studio](https://android-developers.googleblog.com/2026/09/build-your-way-use-any-ai-agent-in-android-studio.html)), support agents in help desks ([Plain](https://www.plain.com/blog/connect-any-ai-agent-to-plain)), enterprise policies. This project extends the same idea to the whole web: every visitor brings their agents to every site, under rules the visitor controls.

## What is new in BYOA

- the site asks for **skills**, not for a vendor;
- **one wallet** for all of the visitor's providers — subscriptions, keys, OAuth accounts, **local models and home servers**;
- **budgets** per site and per time window, with the actual cost shown;
- **declared purpose**, checked on every request;
- **privacy rules** inside the protocol ("local only");
- **all modalities** — text, audio, images, video;
- **one core for browsers, desktop apps and agents**.
