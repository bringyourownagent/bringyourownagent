# Agent wallet 0.1: design

Status: working version for Windows, 11 October 2026. Code and install steps: [wallet/](../wallet/README.md).

The wallet is the visitor's side of BYOA. 0.1 is the first one that runs: a page asks for the visitor's AI, the visitor allows it in a short consent window, and the page's speech clips are recognised by the visitor's own models. It follows the consent flow of the vision ([3.1](vision.md#31-the-consent-flow-proposed)) and checks every call with the draft rules ([spec/rules-0.1-draft.md](../spec/rules-0.1-draft.md)) through the .NET library before any model runs.

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
                                                      │                                    GigaAM · Whisper · OpenRouter
                                                      └─ notice, consent window, settings, activity
```

- **Extension** (Chrome MV3, `wallet/extension`). Its ID is fixed by a `key` in the manifest (`niobopdoiopjilmlonjcgllpmgjemhpk`). It injects `window.byoa` into every `https://` page and into `http://localhost` and `http://127.0.0.1` pages, at `document_start`, and dispatches `byoa:ready`. It shows nothing of its own except the toolbar popup: wallet connected or not running, the versions, and a button that opens the wallet.
- **Native host** (`byoa-host.exe`, .NET 10). A relay with no decisions: it forwards frames as they are, answers the fallback when the wallet is not there, and starts the wallet only for a `request` (or the popup's `open`). It is registered for the current user only.
- **Wallet** (`Byoa.Wallet.exe`, .NET 10 and Avalonia 11). A tray app with one instance per user (a mutex) that serves the pipe. Its engine is a UI-free library (`Byoa.Wallet.Core`) with unit tests.

## 2. Messages

### Page API (CONTRACT 0.1)

```js
window.byoa.version                                     // "0.1"
await byoa.request(manifest)                            // { grantId, skills, expiresAt } | null
await byoa.call(grantId, "audio.transcribe",
                { audio: { mime: "audio/wav", base64 } },   // 16 kHz mono 16-bit PCM WAV, up to 15 s
                { language: "ru" })                     // { ok: true, output: { text }, receipt: { cost: "0.000 USD", provider: "local" | "api-key" } } | { ok: false }
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
3. If the site is **allowed with "decide on my own"** and the manifest asks for nothing new (no new skill the wallet can serve, the expected cost per session not above the one allowed), the wallet grants at once and silently, click or no click.
4. **Without a click** anything else gets `null`; the tray icon gets a quiet dot and the site's count of held-back requests goes up.
5. **With a click**: the notice, then the consent window. A second request from the same origin while the first is asking shares its answer. One prompt is shown at a time. With no answer in 120 s the page gets `null`.
6. **Allow** stores the site's decision (skills, budget per session, "decide on my own", the expected cost and the languages) and opens a session grant: a random 144-bit id, valid 12 hours, held in memory only (a restarted wallet ends all sessions; the page then asks again). The models the site's languages will need are loaded in the background at once, and again when the wallet starts while some site is allowed, so the first call is fast.
7. **Asking for more** (a new skill, or a higher expected cost) shows only the difference: an "updated" chip, "Only what is new", and "Budget $0.05 → $0.10 per session".

## 4. What happens on a call

1. The grant must exist and belong to the calling origin, and the site must still be allowed.
2. The clip is decoded and measured by the wallet (`clipSeconds` from the WAV data, never from the page). PCM 8/16/24/32-bit and float WAVs at any common rate, mono or stereo, are accepted and converted to 16 kHz mono.
3. The wallet's own per-day limits: calls per site per day; money per site per day and for the whole wallet (paid sources only).
4. The **route**: the skill's chain in the visitor's order, keeping sources that are switched on, available and know the call's language (`params.language`, or the only language the manifest declares). For each source in turn:
   - the **gate**: `Gate.CheckCall(manifest, grant, call, state)` with the provider kind (`local-model` or `api-key`), the estimated cost and the measured clip; the slot and the estimated cost are reserved in the session state under a per-grant lock;
   - `blocked` (expired, clip too long, rate over the manifest's limit, skill not granted, …) ends the call with `{ ok: false }`; `not-local` moves on to the next source; `ask` (over the budget per session) moves on and, if nothing else can answer, ends with `{ ok: false }` and one notice that the site reached a limit;
   - a paid source with "ask before paid" asks the visitor (Allow once, Always for this site, No; no answer in 60 s is No);
   - the source runs (45 s at most); a failure releases the reservation and the next source is tried; the real cost replaces the estimate.
5. The activity log records the call: time, site, skill, clip length, language, the source that answered, the fallback route when the first choice did not answer, the cost and the time the source took. Never the audio or the text.

Receipts give the cost with at least three decimals and `provider` as `local` or `api-key`; the page never learns which model answered.

## 5. The manager's suggestion

0.1 has no AI manager. The consent row and the Skills page explain the route from the sources' properties (`languages`, `runs`, measured seconds per clip, cost): "Manager suggests GigaAM v3 for Russian because it is made for Russian, it runs on this PC with no cloud bill and about 0.1 s a clip. If it cannot answer: Whisper large-v3-q5_0, then Scribe v2 (OpenRouter) (paid, asks first)."

## 6. Measured on the development machine (RTX 5080 Laptop, 16 GB)

| Source | Typical time inside the wallet | Notes |
|---|---|---|
| GigaAM v3, CPU | 45-70 ms for a 2-3 s word, 100-160 ms for an 8 s sentence | Russian: 7 of 8 synthetic single words exact, sentence word error rate 0.05 |
| Whisper large-v3 q5_0, GPU (Vulkan) | 400-700 ms per clip | Slovak single words spoken by a person: 12 of 20 exact, 15 of 20 counting numbers written as digits; large-v3-turbo got 9 of 20, so large-v3 is the default |
| Scribe v2 through OpenRouter | 0.6-1.7 s | about $0.00006 for a 2 s word |

The first call after a grant: about 1.1 s (Russian) and 1.7 s (Slovak) while the models finish loading; the first Whisper load ever takes 8-15 s (it prepares its GPU shaders), which is why the wallet warms the models when it grants.

## 7. What 0.1 does not do

- Only `audio.transcribe`. Other skills show as "none of your sources can do this"; a required one cannot be allowed.
- No subscriptions or connected accounts, no home servers, no tests or quality signals, no purpose judge (the hook stays empty), no per-hour budgets, no visitor conditions in words.
- "Over the budget" (`ask`) is not asked in the middle of a session: the call fails and the visitor raises the budget in the settings.
- Windows only (DPAPI, the tray, the registry). The engine itself has no Windows dependency except the secret store.
- Sessions do not survive a restart of the wallet.
- No streaming; one clip per call.

## 8. Security notes

- **Origin.** It comes from Chrome (`sender.origin` of a top frame in a tab), never from the page; grants are bound to it, and another origin's call with a stolen grant id gets `{ ok: false }`. Pages in iframes are not served in 0.1.
- **The click rule.** The isolated-world script reads `navigator.userActivation.isActive` itself; the page cannot set it. A page can still call `request` in the few seconds after any click on it; that is the browser's definition of a user gesture.
- **The same "no".** Declined, blocked, no fitting model, no answer, wallet not running, limits and errors all reach the page as `null` or `{ ok: false }`. A blocked site gets its `null` without a notice, like a visitor without a wallet. Timing still differs between "answered at once" and "the visitor pressed Decline"; a page can tell that a person looked, not what they chose.
- **Who can talk to the wallet.** The native host accepts only this extension's ID. The pipe is per user and opened with `CurrentUserOnly` on both ends. Any program running as the same Windows user could still talk to the pipe; the same program could also read the user's DPAPI secrets, so the wallet does not try to defend against it.
- **Keys.** Stored with DPAPI for the current user and an extra entropy string; decrypted only for the moment a request needs one; never shown, logged or sent anywhere except to their own provider. Error messages from the provider carry only the HTTP status.
- **Answers are user input.** The wallet trims and length-limits the text (4,000 characters) but does not sanitise it; pages must treat it like anything a visitor types.
- **Input limits.** Frames up to 8 MB, audio up to 6 MB, manifests up to 64 KB; the WAV reader refuses anything it does not understand.
- **Local models are read only.** The scanner only reads the visitor's model folders; downloads go only to `%LOCALAPPDATA%\BYOA\models`, checked against the size and SHA-256 Hugging Face publishes.
- **Registry.** Only the two native host keys under HKCU, written by `install.ps1` and removed by `uninstall.ps1`.
- **Logs.** The activity log keeps no audio and no text. Crashes go to `errors.log` with exception types and stacks only.

## 9. What the libraries and the spec need next

Found while building 0.1:

1. **Daily and hourly budgets** in the grant (the vision has them; the rules have only `perSession`). The wallet enforces its own per-day limits outside the gate for now.
2. **Receipts** in the spec. The contract's receipt (`cost`, `provider: "local" | "api-key"`) uses provider words that differ from the rules' provider kinds (`local-model`, `home-server`, …).
3. **Typed documents in the .NET library**: builders for grant, call and state, so a wallet does not write their JSON by hand, and the effective limits exposed for display.
4. **Reservations**: the execution contract asks wallets to reserve the slot and the estimate before a call and settle afterwards; a small helper in the libraries would make every wallet do it the same way.
5. **Skill parameters** (`languages` for `audio.transcribe`) are not checked by the rules, but the wallet routes on them; the first skill parameters deserve rules of their own.
6. **"Asking for more"** needs a definition in the spec (which changes count: new skills, higher expected cost, new languages?). 0.1 counts new skills it can serve and a higher expected cost per session.
7. The contract's extension-host messages say nothing about the payload shapes or about a status message; section 2 above documents what 0.1 uses.
