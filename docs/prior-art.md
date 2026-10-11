# Related work

As of 11 October 2026. **This survey is approximate and may miss things; the field changes fast. Corrections welcome (open an issue).**

Several efforts cover parts of what BYOA proposes. Each of them solves one piece well; none of them combines the pieces into one consent, policy and accounting layer that works across subscriptions, API keys, connected accounts, local models and built-in model providers.

| Work | What it does | How BYOA differs |
|---|---|---|
| [Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) (OpenAI; [announcement coverage](https://flaviocopes.com/sign-in-with-chatgpt/)) | Third-party apps can spend a ChatGPT Plus or Pro plan allowance after the user signs in; the user sets a weekly cap per app | One vendor. On this route the [documented limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) list text, image and file inputs as supported, and audio and video input and the transcription API as not supported. The [terms](https://openai.com/policies/sign-in-with-chatgpt-terms/) limit use of the plan to the connected app. Hosted and commercial apps by application to OpenAI |
| Claude plans (Anthropic; [legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)) | Users sign in to Anthropic's own apps and Claude Code with their plan | Anthropic's page says it does not permit third-party developers to offer Claude.ai login in their own applications, or to route requests through Free, Pro or Max plan credentials on behalf of their users; developers should use API keys. A BYOA wallet would use an Anthropic API key, and a plan only if the terms change |
| [Gemini API OAuth](https://ai.google.dev/gemini-api/docs/oauth) (Google) | OAuth authorization for Gemini API access, set up through a Google Cloud project | OAuth exists, but it does not share a consumer Gemini subscription with other apps. One vendor |
| [OpenRouter OAuth PKCE](https://openrouter.ai/docs/guides/overview/auth/oauth) | After the user authorizes, the app receives a user-controlled API key | One aggregator. The app receives the key, and where it is stored is an integration choice; limits live on the key, not in a protocol. BYOA proposes that credentials stay with the wallet |
| [window.ai](https://github.com/alexanderatallah/window.ai) (2023) | A browser extension that lets sites call the user's chosen model with the user's own configuration | Closest in spirit; mainly text generation, without budgets or a declared purpose. Its current maintenance status is not assessed here |
| [Puter.js "user pays"](https://docs.puter.com/user-pays-model/) | Users cover their own AI use through a Puter account | One platform with its own account, not the user's subscriptions or local models |
| [MCP sampling](https://modelcontextprotocol.io/specification/2025-11-25/client/sampling) | A server asks the client to run a model request on the client's model. Messages can carry text, image and audio content, servers can state model preferences (cost, speed and capability priorities, plus hints), and the specification says there should be a human in the loop who can review and deny requests | The same direction of control, inside MCP. It is defined for MCP clients rather than websites, and the specification defines no budgets, declared purpose or receipts, and nothing about which kind of source (subscription, key, local model) runs a request. BYOA proposes that layer for the web |
| [WebMCP](https://www.openhermit.com/blog/navigator-modelcontext-2026) | A page exposes tools to the browser's agent | The reverse direction; complementary to BYOA |
| [Chrome Prompt API](https://developer.chrome.com/docs/ai/prompt-api) | Pages and extensions use the browser's built-in on-device model | A related approach and a possible future wallet provider: one more source a BYOA wallet could offer, under the same budgets and purpose checks. On its own it has one built-in model, with no subscriptions, keys or other providers |
| [Apple Foundation Models](https://developer.apple.com/foundation-models-framework/) | A native Swift API for apps to use Apple's on-device model, with Private Cloud Compute for larger models | A related approach and a possible future wallet provider on Apple platforms |
| [Windows AI APIs](https://learn.microsoft.com/en-us/windows/ai/apis/) | Windows APIs for apps to use models that run locally on supported Windows 11 devices | A related approach and a possible future wallet provider on Windows |
| [A2A](https://a2a-protocol.org/latest/specification/) | An agent-to-agent protocol in which an Agent Card declares an agent's skills and its authentication requirements | Relevant to BYOA's later agent-to-agent transport; BYOA aims to reuse its skills and authentication model rather than replace them |
| [Brave Leo BYOM](https://en.wikipedia.org/wiki/Brave_Leo) | The browser's assistant can use the user's own model | For the browser's own assistant, not for sites |
| [Hugging Face OAuth](https://huggingface.co/docs/hub/spaces-oauth) | Spaces can use the signed-in user's token | One platform |
| Agent payments: x402, AP2, ACP, [Cloudflare Wallets](https://www.helpnetsecurity.com/2026/08/05/cloudflare-wallets-for-ai-agents/) | Agents pay merchants under spending limits | About paying for goods, not lending a site the user's models |

## Subscriptions today

As of 11 October 2026, most providers do not allow third-party apps to use consumer subscriptions: OpenAI allows Sign in with ChatGPT for the connected app only, Anthropic does not allow third-party apps to route requests through Claude plans, and Gemini API OAuth does not share a consumer subscription. Provider terms change often, so check the sources above.

BYOA stays neutral. It is designed so that subscriptions plug in the moment a provider allows it, and we hope to work with the providers on that. API keys, connected accounts and local models work in every case.

## The term "BYOA"

"Bring your own agent" is already used in the industry for bringing an agent you chose into someone else's platform — coding agents in IDEs ([Android Studio](https://android-developers.googleblog.com/2026/09/build-your-way-use-any-ai-agent-in-android-studio.html)), support agents in help desks ([Plain](https://www.plain.com/blog/connect-any-ai-agent-to-plain)), enterprise policies. This project extends the same idea to the whole web: every visitor brings their agents to every site, under rules the visitor controls.

## What BYOA proposes

No single piece above is new. BYOA's distinct contribution is the combined **consent, policy and accounting layer** across all of these sources:

- one request format: the site asks for **skills**, with a declared purpose, not for a vendor;
- one place where the visitor consents and sets limits, for every source: subscriptions where providers allow them, API keys, OAuth accounts, **local models and home servers**, and built-in browser or operating system models;
- one policy check on every call: **budgets** per site and per time window, hard limits, and an optional purpose check;
- one account of what each call cost and where its data went, whichever source ran it;
- **privacy rules** inside the protocol ("local only");
- **all modalities** — text, audio, images, video;
- **one core for browsers, desktop apps and agents**.

These are proposals. What exists today is a set of draft libraries that validate manifests and evaluate individual calls (see [../sdk/](../sdk/)); the reference wallet and the browser binding are planned.
