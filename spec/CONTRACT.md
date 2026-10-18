# BYOA 0.1 contract: web page, browser extension and desktop wallet

> **Draft.** This is the binding description of how the reference agent wallet ([`../wallet/`](../wallet/)) talks to a web page through a browser extension. It is written down so that other wallets and other sites can interoperate. Where it disagrees with the rules in [rules-0.1-draft.md](rules-0.1-draft.md), the rules win for anything the rules cover (manifests, grants, the call gate). The second way to reach a wallet, without any extension, is in [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md).

## 1. The page API: `window.byoa`

The extension injects it into every `https://` page, and into `http://localhost` and `http://127.0.0.1` pages (for tests), **in the top frame only, not in sub-frames**: a third-party frame on a page cannot see the wallet. It runs in the page's MAIN world at `document_start`. After injecting, it dispatches `window.dispatchEvent(new Event("byoa:ready"))`. A page must also check `window.byoa` directly, because the event may fire before the page's script runs.

```js
window.byoa = {
  version: "0.1",

  // Ask for permission. Call it from a click handler on the page (transient user activation).
  // Resolves with a grant, or with null meaning "AI unavailable".
  // null covers: declined, blocked, no fitting model, no answer within 120 s, wallet not running, and so on.
  // The page cannot tell these apart, and that is intended.
  request(manifest) -> Promise<{ grantId: string, skills: string[], expiresAt: string } | null>,

  // One use of a granted skill.
  // ok:false means "unavailable for this call": blocked by a limit, no model answered, the grant was revoked,
  // or a paid fallback was refused. The page must then use its own path.
  call(grantId, skill, input, params) -> Promise<
      { ok: true, output: object, receipt: { cost: "0.000 USD", provider: "local" | "api-key" } }
    | { ok: false } >,

  release(grantId) -> Promise<void>
};
```

**What the extension and the wallet enforce (not the page):**

- **No click, no notice.** A `request` made without transient user activation shows no notice. The wallet only gets a quiet badge on its tray icon, and the promise resolves `null`. The one exception: when this origin already has a grant with "decide on my own" ticked that covers the manifest, the promise resolves with that grant at once and silently.
- **Origin.** The origin comes from the extension (the sender's URL), never from the page. Grants are bound to the origin.
- **Asking for more.** If an origin asks for more than its existing grant covers (a new skill, a language it was not allowed, a higher expected cost), the consent shows only the difference.
- **Call checks.** Each `call` is checked by the wallet against the manifest's limits and the grant, with the rules of [rules-0.1-draft.md](rules-0.1-draft.md) (manifest rules, grant, call, gate), before any provider runs.
- **Trust.** Answers are user input. A page must treat `output` like anything a visitor types.
- **Same answer for every refusal.** "Declined", "blocked" and "no wallet" look the same to the page.

## 2. The manifest

A manifest is the requester's document as described in [rules-0.1-draft.md](rules-0.1-draft.md) and validated by the reference libraries. Example (a family geography game that listens to country names):

```json
{
  "byoa": "0.1-draft",
  "purpose": {
    "summary": "Family geography game: recognise country names spoken aloud",
    "limits": { "maxClipSeconds": 15, "maxCallsPerMinute": 40 }
  },
  "skills": {
    "audio.transcribe": { "languages": ["en", "es", "ru", "sk"], "required": true }
  },
  "privacy": "personal-voice",
  "expectedCost": { "perSession": "0.05 USD" }
}
```

## 3. Skills in 0.1

### 3.1 `audio.transcribe` (speech to text)

- `input`: `{ "audio": { "mime": "audio/wav", "base64": "<16 kHz mono 16-bit PCM WAV>" } }`
- `params`: `{ "language": "ru" }` (a BCP 47 primary language tag; optional)
- `output`: `{ "text": "..." }`

The wallet measures the clip's length from the WAV itself and never trusts the page. Clips are up to 15 seconds.

### 3.2 `text.generate` (a short text answer from a language model)

- `input`: `{ "messages": [ { "role": "system" | "user" | "assistant", "content": "..." }, ... ] }`
- `params` (all optional):
  - `maxTokens`: the most tokens to write. The wallet caps it.
  - `temperature`: 0 to 1.
  - `language`: a BCP 47 primary language tag, a hint for choosing a model.
  - `choices`: an array of strings. When given, `output.text` is **exactly one** of them. The wallet constrains the model, or normalises the model's answer (trimming, case-insensitive matching, stripping quotes and punctuation), and treats an answer that is still not one of the choices as a failed call (`ok:false`).
- `output`: `{ "text": "..." }`: the answer only, never the model's hidden reasoning.
- The wallet caps the number of messages and the total input size; a call over a cap is `ok:false`.
- Which source answered (a model on this computer, or a model behind the visitor's own key) is never revealed to the page; the receipt says only `local` or `api-key` and the cost.

## 4. Extension, native host and wallet

- **Messaging.**
  - A Chrome MV3 extension. The native messaging host name is `org.bringyourownagent.wallet`.
  - The extension's ID is fixed by a `key` in its `manifest.json`.
  - Message JSON: `{ "id": n, "type": "request" | "call" | "release", "origin": "...", "userActivation": true | false, "payload": {...} }` and the answer `{ "id": n, "result": ... }`.
  - The isolated-world content script checks `navigator.userActivation.isActive` itself. It never trusts a flag sent by the page.
- **Native host.** `byoa-host.exe` is a small console program speaking the stdio protocol of native messaging: a 4-byte little-endian length, then UTF-8 JSON. It relays to the running wallet over a per-user named pipe and starts the wallet if it is not running.
- **Install.** The reference wallet's `install.ps1` registers the host for the current user (HKCU `Software\Google\Chrome\NativeMessagingHosts\...`, and the same for Chromium); `uninstall.ps1` removes exactly those keys.

## 5. The connection indicator on the site (recommended for every site)

A site that uses a wallet shows, in its header or toolbar on every screen, whether the wallet is connected, and lets the visitor disconnect it there, at any moment. This is a SHOULD, not a protocol rule: it is the good manners of a site that borrows someone's AI.

- **Where.** In the header (or the toolbar of the game), always visible, not inside a menu or a settings panel. On a narrow screen the name may collapse, the state and the button do not.
- **What it says.** The product's name, **Agent wallet**, in English on every language of the site, and the state in the site's language:
  - *connected* (the wallet answers now);
  - *waiting* or *reconnecting* (a link was opened and the wallet has not come yet, or it dropped and comes back by itself);
  - *not connected*.
  The state is a status region (`role="status"`), and the colour is never the only sign of it (the word is there).
- **Disconnect.** While connected or reconnecting, the same place offers **Disconnect**: one click, no question. It lets the wallet go at once: with the extension the site calls `window.byoa.release(grantId)`; with a link the site's server says `bye` to the wallet, closes the connection and forgets the pairing, and the page forgets its token. The site does not call the wallet again, falls back to what it does without one (or says so), and the indicator turns to *not connected*. Disconnecting never needs the wallet's answer; a wallet that is gone is let go all the same.
- **Connect.** While not connected, the same place offers **Connect**: one click; the connection starts from the visitor's own click (a browser does not launch the wallet's link without one). While waiting, it offers nothing: the wallet's own window asks the visitor.
- **No surprises.** The indicator never connects by itself, and a disconnect by the visitor is final until the visitor connects again (a stored pairing is forgotten, so a reload does not bring the wallet back).

Reference implementations: the two showcase sites, `geo.appshub.net` and `twenty-questions.appshub.net`.
