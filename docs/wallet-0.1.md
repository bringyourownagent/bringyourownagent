# Agent wallet 0.1: design

Status: working version for Windows, 11 October 2026. Code and install steps: [wallet/](../wallet/README.md).

The wallet is the visitor's side of BYOA. 0.1 is the first one that runs: a page asks for the visitor's AI, the visitor allows it in a short consent window, and the page's speech clips and text prompts are handled by the visitor's own models. A site reaches it through the browser extension or, with no extension, through a `bringyourownagent://` link and the wallet's own connection to the site's server (section 10). It follows the consent flow of the vision ([3.1](vision.md#31-the-consent-flow-proposed)) and checks every call with the draft rules ([spec/rules-0.1-draft.md](../spec/rules-0.1-draft.md)) through the .NET library before any model runs.

## 1. Architecture

```
web page ── window.byoa ──▶ page.js (MAIN world)
                               │ window.postMessage
                            bridge.js (isolated world): judges the click from navigator.userActivation
                               │ chrome.runtime.sendMessage
                            background.js (service worker): adds the origin from Chrome's sender, top frames only,
                               │ timeouts, passes back only the contract's shapes
                               │ native messaging (stdio: 4-byte little-endian length + UTF-8 JSON)
                            byoa-host.exe: relays frames, starts the wallet for a request
                               │ named pipe, per user (CurrentUserOnly), same framing
                            Byoa.Wallet.exe (tray): engine ─▶ gate (BringYourOwnAgent) ─▶ route ─▶ source
                                                      │                         GigaAM · Whisper · text runtimes · OpenRouter
                                                      └─ notice, consent window, settings, activity
```

- **Extension** (Chrome MV3, `wallet/extension`). Its ID is fixed by a `key` in the manifest (`niobopdoiopjilmlonjcgllpmgjemhpk`). It injects `window.byoa` into every `https://` page and into `http://localhost` and `http://127.0.0.1` pages, at `document_start`, and dispatches `byoa:ready`. It shows nothing of its own except the toolbar popup: wallet connected or not running, the versions, and a button that opens the wallet.
- **Native host** (`byoa-host.exe`, .NET 10). A relay with no decisions: it forwards frames as they are, answers the fallback when the wallet is not there (also when its pipe breaks or was already closed under it: the host keeps running and connects again on the next message), and starts the wallet only for a `request` (or the popup's `open`). It is registered for the current user only.
- **Wallet** (`Byoa.Wallet.exe`, .NET 10 and Avalonia 11). A tray app with one instance per user (a mutex) that serves the pipe. Its engine is a UI-free library (`Byoa.Wallet.Core`) with unit tests. Each connection can handle independent messages, while the site keeps at most four calls in flight and may queue a bounded number; the wallet refuses a fifth call as a backstop. The native pipe still applies its own bounded backpressure.

## 2. Messages

### Page API (CONTRACT 0.1)

```js
window.byoa.version                                     // "0.1"
await byoa.request(manifest)                            // { grantId, skills, expiresAt } | null
await byoa.call(grantId, "audio.transcribe",
                { audio: { mime: "audio/wav", base64 } },   // 16 kHz mono 16-bit PCM WAV, up to 15 s
                { language: "ru" })                     // { ok: true, output: { text }, receipt: { cost: "0.000 USD", provider: "local" | "home-server" | "api-key" } } | { ok: false }
await byoa.call(grantId, "text.generate",
                { messages: [{ role: "user", content: "Answer briefly." }] },
                { maxTokens: 128 })                       // same result shape; text is in output.text
await byoa.release(grantId)
```

`window.byoa` is a frozen, non-writable property. `null` and `{ ok: false }` are the only "no", whatever the reason.

### Extension, host and wallet

One JSON message per frame: `{ "id": n, "type", "origin", "userActivation", "payload" }`, answered by `{ "id": n, "result" }`.

| type | sent by | payload | result |
|---|---|---|---|
| `request` | a page | `{ "manifest": {...} }` | a grant or `null` |
| `call` | a page | `{ "grantId", "skill", "input", "params" }` | `{ "ok": true, "output", "receipt" }` or `{ "ok": false }` |
| `release` | a page | `{ "grantId" }` | `null` |
| `status` | the popup | `{}` | `{ "running": true, "version", "quiet" }`; from the host when the wallet is not running: `{ "running": false }` |
| `open` | the popup | `{}` | `{ "opened": true }` (starts the wallet if needed and opens its window) |

`origin` is set by the service worker from `sender.origin` (top frame of a tab, https or localhost); `userActivation` by the isolated-world script, only for `request`. `status` and `open` are additions for the popup; pages cannot send them. Frames from the browser are limited to 8 MB, frames to the browser to 1 MB (Chrome's limit). Timeouts in the service worker: request 125 s (the wallet itself gives up after 120 s), call 90 s.

## 3. What happens on a request

1. The origin must be `https://host[:port]`, or `http://localhost` / `http://127.0.0.1` for tests. The manifest must be valid under the draft rules (`Manifest.Validate`).
2. A **blocked** site gets `null` at once; no notice, ever.
3. If the site is **allowed with "decide on my own"** and the manifest asks for nothing new (no new skill the wallet can serve, no required skill it cannot serve, the expected cost per session not above the visitor's budget for the site), the wallet grants silently, click or no click, once the models are loaded (item 6).
4. **Without a click** anything else gets `null`; the tray icon gets a quiet dot and the site's count of held-back requests goes up.
5. **With a click**: the notice, then the consent window. A second request from the same origin while the first is asking shares its answer; the question stays open while any page that asked still waits for it (closing the first tab does not close it for the second). One prompt is shown at a time. With no answer in 120 s the page gets `null`.
6. **Allow** stores the site's decision (skills, budget per session, "decide on my own", the expected cost and the languages) and opens a session grant: a random 144-bit id, valid 12 hours, held in memory only (a restarted wallet ends all sessions; the page then asks again). **The page gets its grant only when the models are ready**: the wallet loads the first local source of the route for each language the manifest declares and waits for it, with "Preparing your model…" in a small toast meanwhile (shown only when it takes more than 150 ms). If that source fails, the next local source gets the chance. A preparation wait is capped at 30 s; after that the grant is answered and a ready fallback or the normal "AI unavailable" route decides the first call. The silent grant of item 3 waits the same way, without a toast. A model the route prefers but that is not downloaded yet starts downloading before the grant. The wallet also loads the models of the allowed sites when it starts. A slow preparation holds this site only; other sites continue to run.
7. **Asking for more** (a new skill the wallet can serve, a required skill it cannot serve, or an expected cost per session above the visitor's budget) shows only the difference: an "updated" chip, "Only what is new", and "Budget $0.05 → $0.10 per session" (from the visitor's budget to the site's new expected cost). A required skill no source can serve is shown as missing and Allow is not offered, so the page gets `null`; it is never granted silently. A higher expected cost that is still within the visitor's budget asks nothing.

## 4. What happens on a call

1. The grant must exist and belong to the calling origin, and the site must still be allowed.
2. The clip is decoded and measured by the wallet (`clipSeconds` from the WAV data, never from the page). PCM 8/16/24/32-bit and float WAVs at any common rate, mono or stereo, are accepted and converted to 16 kHz mono.
3. The wallet's own per-day limits: calls per site per day; money per site per day and for the whole wallet (paid sources only). Calls still running count too: a call holds its slot, and a paid source its estimate, from its start to its end, so many calls at once cannot pass a daily limit before the first one is logged.
4. The **route**: the skill's chain in the visitor's order, keeping sources that are switched on, available and know the call's language (`params.language`, or the only language the manifest declares). For each source in turn:
   - the **gate**: `Gate.CheckCall(manifest, grant, call, state)` with the provider kind (`local-model`, `home-server` or `api-key`), the estimated cost and the measured clip or text size; the slot and the estimated cost are reserved in the session state under a per-grant lock;
   - `blocked` (expired, clip too long, rate over the manifest's limit, skill not granted, …) ends the call with `{ ok: false }`; `not-local` moves on to the next source; `ask` (over the budget per session) moves on and, if nothing else can answer, ends with `{ ok: false }` and one notice that the site reached a limit;
   - a paid source with "ask before paid" asks the visitor (Allow once, Always for this site, No; no answer in 60 s is No);
   - a speech source runs for at most 45 s; a text source gets at most 40 s and the complete text route gets 74 s, below the page's 90 s call timeout. The caller's cancellation is passed through. A failure releases only a genuinely unknown reservation; a paid response with a known or missing cost is settled using the reported cost or the estimate, and the next source is tried.
5. The activity log records the call: time, site, skill, clip length, language, the source that answered, the fallback route when the first choice did not answer, the cost and the time the source took. Never the audio or the text.

Receipts give the cost with at least three decimals and `provider` as `local`, `home-server`, or `api-key`; the page never learns which model answered.

## 5. The manager's suggestion

0.1 has no AI manager. The consent row and the Skills page explain the route from the sources' properties (`languages`, `runs`, measured seconds per clip, cost): "Manager suggests GigaAM v3 for Russian because it is made for Russian, it runs on this PC with no cloud bill and about 0.1 s a clip. If it cannot answer: Whisper large-v3-q5_0, then Scribe v2 (OpenRouter) (paid, asks first)."

## 6. Measured on the development machine (RTX 5080 Laptop, 16 GB)

| Source | Typical time inside the wallet | Notes |
|---|---|---|
| GigaAM v3, CPU | 45-70 ms for a 2-3 s word, 100-160 ms for an 8 s sentence | Russian: 7 of 8 synthetic single words exact, sentence word error rate 0.05 |
| Whisper large-v3 q5_0, GPU (Vulkan) | 400-700 ms per clip | Slovak single words spoken by a person: 12 of 20 exact, 15 of 20 counting numbers written as digits; large-v3-turbo got 9 of 20, so large-v3 is the default |
| Scribe v2 through OpenRouter | 0.6-1.7 s | about $0.00006 for a 2 s word |

From Allow to `request()` resolved, on a freshly started wallet (the models are loaded before the grant, section 3):

| Manifest languages | Allow → grant | Model ready after | First call after the grant |
|---|---|---|---|
| ru | 1.1 s | GigaAM 0.9 s | 80 ms (GigaAM) |
| sk | 1.95-2.1 s | Whisper 1.7-1.9 s | 470-480 ms (Whisper) |
| ru + sk (the e2e test; Geo-1) | 2.4 s | both 1.9-2.0 s | 80 ms (ru), 476 ms (sk) |

A silent grant ("decide on my own") on a wallet that the request itself starts: 2.1 s (ru) and 2.5 s (sk), wallet start included. The first Whisper load ever on a machine takes 8-15 s (it prepares its GPU shaders); the page receives its grant after the first usable route source is ready, or after the next fallback is ready if loading fails.

## 7. What 0.1 does not do

- Only `audio.transcribe` and `text.generate`. Pictures, video and other skills show as "none of your sources can do this"; a required one cannot be allowed.
- No subscriptions or connected accounts, no provider account manager, no tests or quality signals, no purpose judge (the hook stays empty), no per-hour budgets, no visitor conditions in words. A visitor may add an OpenAI-compatible home server, but it is labelled "another computer (host)" and is not treated as a local model.
- "Over the budget" (`ask`) is not asked in the middle of a session: the call fails and the visitor raises the budget in the settings.
- Windows only (DPAPI, the tray, the registry). The engine itself has no Windows dependency except the secret store.
- Sessions do not survive a restart of the wallet.
- No streaming; one clip per call.
- The direct link does not go through a proxy (the address check needs the site's real address).

## 8. Security notes

- **Origin.** It comes from Chrome (`sender.origin` of a top frame in a tab), never from the page; grants are bound to it, and another origin's call with a stolen grant id gets `{ ok: false }`. Pages in iframes are not served in 0.1.
- **The click rule.** The isolated-world script reads `navigator.userActivation.isActive` itself; the page cannot set it. A page can still call `request` in the few seconds after any click on it; that is the browser's definition of a user gesture.
- **The same "no".** Declined, blocked, no fitting model, no answer, wallet not running, limits and errors all reach the page as `null` or `{ ok: false }`. A blocked site gets its `null` without a notice, like a visitor without a wallet. Timing still differs between "answered at once" and "the visitor pressed Decline"; a page can tell that a person looked, not what they chose.
- **Who can talk to the wallet.** The native host accepts only this extension's ID. The pipe is per user and opened with `CurrentUserOnly` on both ends. Any program running as the same Windows user could still talk to the pipe; the same program could also read the user's DPAPI secrets, so the wallet does not try to defend against it.
- **Keys.** Stored with DPAPI for the current user and an extra entropy string; decrypted only for the moment a request needs one; never shown, logged or sent anywhere except to their own provider. Error messages from the provider carry only the HTTP status.
- **Answers are user input.** The wallet trims and length-limits the text (4,000 characters) but does not sanitise it; pages must treat it like anything a visitor types.
- **Input limits.** Frames up to 8 MB, audio up to 6 MB, text messages up to 32 and 20,000 characters, answers up to 4,000 characters, manifests up to 64 KB; the WAV reader refuses anything it does not understand, and checks chunk sizes in 64-bit arithmetic (a chunk that claims to run past the end of the clip is bad audio, not an overflow). The site keeps at most four calls in flight and may queue only a bounded number; the wallet refuses beyond four.
- **Local models are read only to the scanner.** The scanner only reads visitor-provided model folders. Wallet downloads go only to the active wallet home's `models` folder (normally `%LOCALAPPDATA%\BYOA\wallet\models`), including the CPU Whisper copy under `models\whisper`, and are checked against the size and SHA-256 Hugging Face publishes. The visitor may remove a model from Sources, and an approved MCP agent may remove it on the visitor's request, but only when the wallet's ownership marker and settings ledger both identify the file as downloaded by this wallet. A path outside the active folder, a redirected folder, or a visitor-provided model is refused.
- **Registry.** Only the two native host keys and the `bringyourownagent` scheme key under HKCU, written by `install.ps1` and removed by `uninstall.ps1` (which also removes the one Startup shortcut `install.ps1 -StartWithWindows` made, and no other file).
- **The direct link.** Before the visitor's Allow nothing leaves the wallet, so a site cannot learn whether a wallet is installed or whether the visitor declined. Session codes are single use and short-lived because a link may reach another program that registered the scheme later. The second process that hands a link to the running wallet waits at most a few seconds for each step. The pairing token lets its holder use the visitor's AI on that one site, within the same grant and limits; it is kept like a key (DPAPI) and only under "decide on my own". A same-user program can hand the wallet a link over the pipe, which is no more than it could do by starting the wallet with that link; the visitor still sees the notice unless the site already has "decide on my own".
- **A model that fails to load** (a broken folder, a missing runtime) is marked broken on the Sources page and skipped by the route until its folder changes; calls fall back to the next source.
- **Logs.** The activity log keeps no audio and no text. Crashes go to `errors.log` with exception types and stacks only.

## 9. What the libraries and the spec need next

Found while building 0.1:

1. **Daily and hourly budgets** in the grant (the vision has them; the rules have only `perSession`). The wallet enforces its own per-day limits outside the gate for now.
2. **Receipts** in the spec. The contract's receipt (`cost`, `provider: "local" | "api-key"`) uses provider words that differ from the rules' provider kinds (`local-model`, `home-server`, …).
3. **Typed documents in the .NET library**: builders for grant, call and state, so a wallet does not write their JSON by hand, and the effective limits exposed for display.
4. **Reservations**: the execution contract asks wallets to reserve the slot and the estimate before a call and settle afterwards; a small helper in the libraries would make every wallet do it the same way.
5. **Skill parameters** (`languages` for `audio.transcribe`) are not checked by the rules, but the wallet routes on them; the first skill parameters deserve rules of their own.
6. **"Asking for more"** needs a definition in the spec (which changes count: new skills, higher expected cost, new languages?). 0.1 counts new skills it can serve, required skills it cannot serve, and an expected cost per session above the visitor's budget.
7. The contract's extension-host messages say nothing about the payload shapes or about a status message; section 2 above documents what 0.1 uses.
8. **Readiness**: the contract has no "preparing" state, so the wallet holds `request()` until its first usable route source is loaded and shows the visitor a progress hint in its own window. A progress hint in the page API would let sites show their own "preparing" instead of waiting without knowing why.

## 10. Transports

A site reaches the wallet in one of two ways. Both end in the same engine: the same notice and consent, the same
grants, blocks, "decide on my own", budgets, daily limits, gate, routing, fallbacks, "ask before paid", models ready
before the answer, and the same activity log. Grants and decisions belong to the origin, so a site allowed through
one transport is allowed through the other.

| | Extension (CONTRACT.md) | Direct link (CONTRACT-DIRECT.md) |
|---|---|---|
| The visitor needs | the browser extension | nothing in the browser; the wallet registers a URL scheme |
| The page starts it with | `window.byoa.request(manifest)` in a click handler | a click on a `bringyourownagent://connect?...` link (a browser launches a protocol only after a click) |
| The manifest comes from | the page | the link itself (base64url, at most 4096 bytes); the site's welcome echoes it |
| Calls come from | the page, through the extension and the native host | the site's server, over the wallet's own WebSocket |
| Identity | Chrome's `sender.origin` | TLS: the origin `https://host[:port]` of the `wss://` endpoint |
| Activity | as before | every entry marked "via link" |

### The direct link

- **The link.** `bringyourownagent://connect?v=0.1&endpoint=<wss URL>&manifest=<base64url of the manifest JSON, no padding, at most 4096 bytes decoded>&session=<code>` (CONTRACT-DIRECT.md with its §7). The scheme is `bringyourownagent`, never `byoa` (a generic name another program could take; on Windows the last program to register a scheme gets its links).
- **No contact before Allow.** The wallet makes no network request of any kind before the visitor's Allow (or a silent "decide on my own" grant): the manifest comes in the link, so not even a DNS lookup of the site happens. Decline, Block, a closed notice and a visitor without a wallet all look the same to the site: it never hears from the wallet. (An earlier form fetched the manifest before the consent; an external review showed that this told the site a wallet was installed, so the contract was changed.)
- **Registration.** `install.ps1` writes `HKCU\Software\Classes\bringyourownagent` ("URL Protocol"; `shell\open\command` = `"<wallet exe>" --url "%1"`); `uninstall.ps1` removes exactly that key, and only while its command is still this wallet's. A link starts the wallet; when the wallet is already running, the new process hands the link to it over the per-user pipe and exits. The native host may also relay a link frame from the browser, preserving the browser's `userActivation` bit; the command-line hand-off defaults to an activated browser link.
- **Checks before anything else.** The endpoint must be `wss://`; no user name, no fragment, no IP literals (in any spelling), no `localhost`, `.localhost`, `.local`, `.home.arpa`, `.internal`, `.lan` or single-label names. The manifest must be base64url without padding, at most 4096 bytes, a JSON object, and a valid manifest under the draft rules (the .NET library's `Manifest.Validate`). The session code is 22 to 256 URL-safe characters (at least 128 bits). A link that fails any check, repeats a code seen in the last 10 minutes, or belongs to a blocked site does nothing but leave an activity entry: no notice, no request. At most 4 links are acted on at once; more are ignored (a program flooding the wallet with links).
- **Addresses.** The WebSocket connection, after Allow, resolves the host itself and refuses it if any address is private, loopback, link-local, carrier-grade NAT, multicast or another special range (also inside IPv6: mapped, NAT64, 6to4, Teredo); it then connects to an address it checked, so DNS cannot change the answer in between. No proxy, no cookies, no redirects. Such a refusal is final; a connection that fails for network reasons is tried again with backoff while the session code may still be valid (60 s after the link), until the site answers `error`.
- **The developer switch.** `BYOA_WALLET_DEV_LOCAL=1` (tests only) also allows `ws://localhost`; the origin is then `http://localhost:<port>`, the same as an extension page there.
- **The consent.** The usual notice and consent, from the link's manifest (only the difference when an allowed site asks for more), or the silent grant under "decide on my own". A link-origin consent says "came by a link: confirmed after you allow"; only the extension path, where the browser vouches for the origin, says "verified".
- **The channel.** After the grant, and after the routed models are loaded, the wallet connects and sends `hello` (the session code, the wallet, the granted skills and the grant's expiry). The site answers `welcome` (with a pairing token and the manifest it put in the link) or `error` and a close. The welcome's manifest must be the link's, byte for byte: otherwise the wallet says `bye`, closes, forgets any pairing, revokes the site's grant and notes "the site's manifest differs from its link" in Activity. Then the site sends `call` frames and the wallet answers `result` frames: the site keeps at most 4 calls in flight and may queue a bounded number; the wallet refuses a fifth as a backstop. Frames are JSON text up to 4 MB; a frame that is not a JSON message, or a site that speaks before its welcome, closes the connection; unknown frame types are ignored. The wallet sends `{"type":"ping"}` every 30 s, and WebSocket pings that end a connection whose site stopped answering. `bye` from either side ends it.
- **Pairing.** The welcome's token must be 22 to 1024 characters from `A-Z`, `a-z`, `0-9`, `_` and `-`; it is kept (DPAPI, `pairings.dat` in the data folder) only for a site with "decide on my own". It is stored with the manifest the visitor agreed to. With it the wallet reconnects after a dropped connection (backoff 1, 2, 5, 10, then 30 s) and after its own restart, with `hello` carrying the pairing instead of a code and no notice, under a silent grant from the stored manifest. The welcome then carries the site's current manifest: within what the site is allowed it is taken silently; if it asks for more, the wallet says `bye`, closes and forgets the pairing, and the site needs a new link, where the consent shows only the difference. A grant that ends (12 hours) is renewed the same way. Without the token, a dropped connection stays closed.
- **Revoke and Block** in the wallet say `bye`, close the site's connections and forget its pairing. Turning "decide on my own" off ends the reconnecting at the next attempt. **Pause** (by hand in Sites, or by the wallet after the site goes unused for the time set there, a day by default) closes the connections the same way and stops the reconnecting; the pairing is forgotten at the next attempt, and the site comes back only through a new link, after a click, with the consent window's **Resume**.
- **What the visitor sees.** Sites shows "Connected without the extension" (and "paired" when the wallet will come back on its own); Activity marks the link's events (link ignored, connected, closed, refused) and every request and call "via link".

## 11. Registry, MCP and agents

Preparation is a real wait, not an unbounded page timeout: after Allow the wallet keeps the request pending until the first usable local source (or the next local fallback) has downloaded and loaded, for no more than 30 s. A slow preparation shows its progress in the wallet and then returns a grant whose first call can use a ready fallback or say "AI unavailable". It does not hold requests from other sites.

The wallet has one registry for sources, models and routes. The Sources page, Help, and the MCP bridge read and change that registry, so an agent sees the same source ids, language chains, model catalog and activity that the visitor sees in the wallet. Added sources can be edited, disabled, reordered and removed; built-in sources can be disabled. Model downloads use the wallet's active `models` folder. The visitor can confirm deletion in Sources; an approved MCP agent acts immediately on the visitor's explicit request. Both use the same registry check: only a file below the active folder that this wallet recorded as downloaded, in its wallet-created model folder, can be removed. Paths outside it and visitor-provided model folders are refused, and the removal is logged with the actor.

`byoa-mcp.exe` is a stdio JSON-RPC 2.0 MCP server (protocol revision `2025-06-18`). It does not start the wallet. It connects to a per-user named pipe whose name includes the active `BYOA_WALLET_HOME`, and the wallet creates the pipe with `CurrentUserOnly`. The bridge keeps no settings, model files or secrets. The first connection shows the announced agent name and the executable path of the process that launched the bridge. The visitor can Allow, Not now, or Block; an approved agent reconnects silently until revoked.

Normal source, model, route, test and activity tools are available after Allow. Site access, limits, budgets, paid-approval changes and approval of another agent are separate Full-access operations. Full access is off by default and can be granted or revoked in Settings → Agents. The wallet still owns visitor consent; an agent cannot manufacture an approval by calling a tool.

API keys are write-only. `request_api_key_entry` opens a field in the wallet so the visitor enters the key; `set_api_key` is provided only for a key the visitor has deliberately handed to the agent. Neither path returns the key, and Activity records only that a key was saved. Help articles are embedded once in the core assembly and are rendered by the Help window and returned by the MCP `help` tool.

Measured on the development machine with the end-to-end test (`wallet/tests/e2e/direct_e2e.py`, fake sites on `ws://localhost` that count every request they get): no request reaches a site before Allow; link to notice 1.2-3.0 s with the wallet starting cold; Allow to `hello` 2.5-3.5 s (the models loaded first, 2.4-3.3 s); calls round trip from the site's server 46-180 ms in Russian (GigaAM) and 490-830 ms in Slovak (Whisper); a restarted wallet back on the connection 2.9-3.5 s after it starts. Against the Geo-1 game run locally (its direct mode, no extension in the browser, the manifest in its link): Allow to the game saying the wallet is connected 2.8-3.8 s; the server's phrases answered by GigaAM in 61-76 ms; a page reload binds the same connection again; a restarted wallet is bound again 3.4-3.6 s after it starts; every welcome echoed the link's manifest. A real click in a headed Chromium shows Chromium's own prompt ("Open <the app>?", with the exe's description, "Agent wallet", once Windows has not cached an older name) and then the wallet's notice; that second process hands the link to the running wallet over the pipe.
