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
  // A site that works with text also gets its `limits` (section 3.4); a site that asked for several models
  // gets the `models` it was given (section 3.3).
  request(manifest) -> Promise<{ grantId: string, skills: string[], expiresAt: string,
                                 limits?: { maxTextChars, maxAnswerTokens, maxCallSeconds, maxParallelCalls, maxCallsPerMinute },
                                 models?: { "text.generate": [ { ref, name, id, runs } ] } } | null>,

  // One use of a granted skill.
  // ok:false means "unavailable for this call": blocked by a limit, no model answered, the grant was revoked,
  // or a paid fallback was refused. The page must then use its own path.
  // A call may wait in the wallet for its own limit (`maxCallSeconds`, up to 600 s): a page times out no sooner than that.
  call(grantId, skill, input, params) -> Promise<
      { ok: true, output: object, receipt: { cost: "0.000 USD", provider: "local" | "api-key", model?: string } }
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

`connect` (optional): `{ "check": true }` says that the site shows the connect check code on its page while it waits for a link-connected wallet ([CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) section 3a). A wallet that sees it requires the visitor to compare the code before Allow.

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
  - `thinking`: `false` (or `"off"`) asks a model that reasons before it answers to answer at once; left out, the model decides. A simple question deserves `false`: a reasoning model can spend minutes, and all of `maxTokens`, on hidden thoughts before its first word. The visitor can also switch thinking off for a model of his, and that switch wins over a page that wants thinking. Only models that run on the visitor's PC or on a server of his own obey it (the wallet sends `chat_template_kwargs.enable_thinking: false`); a model behind an API key answers as it always does, and a model that does not reason is not changed.
  - `language`: a BCP 47 primary language tag, a hint for choosing a model.
  - `choices`: an array of strings. When given, `output.text` is **exactly one** of them. The wallet constrains the model, or normalises the model's answer (trimming, case-insensitive matching, stripping quotes and punctuation), and treats an answer that is still not one of the choices as a failed call (`ok:false`).
  - `model`: a model of the grant's `models` list, by its `ref` (section 3.3). Without it the call goes to the models the site was given, in the visitor's order, or, for a site that asked for none, along the wallet's own route.
  - `stream`: `true` asks for the answer in pieces as it is written. Over the direct link ([CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) section 4) the pieces come in `chunk` frames before the result. The extension transport does not stream; the call there answers once. Pieces are a preview: the final `output.text` is the whole answer, without the model's hidden reasoning, and it replaces anything shown. A call with `choices` is not streamed.
- `output`: `{ "text": "..." }`: the answer only, never the model's hidden reasoning, at most 128,000 characters.
- The wallet caps the number of messages (128) and holds a call to the limits of section 3.4; a call over a limit is `ok:false`.
- Which source answered is never revealed to a site that asked for no models; the receipt says only `local` or `api-key` and the cost. A site that asked for several models learns which of **its** models answered, by `ref` (`receipt.model`), and, when the visitor allows it, their names (section 3.3).

### 3.3 Asking for several models (`models`)

A site whose work needs several different models (a judge that wants the opinions of different AIs, a quiz that compares answers) says so inside the skill:

```json
"skills": {
  "text.generate": {
    "required": true,
    "models": {
      "count": { "min": 2, "max": 5 },
      "distinct": "vendor",
      "require": [],
      "recommend": ["anthropic/claude-opus-4.6", "openai/gpt-5*", "x-ai/grok-4.1-fast"],
      "why": "Different AIs from different companies judge each side: less bias.",
      "whyLong": "Every model reads both versions of your question and scores both sides. ...",
      "names": true
    }
  }
}
```

- **`count`**: how many different models the site uses: a number, or `{ "min", "max" }` (1 to 16; `min` defaults to 1, `max` to 5).
- **`distinct`**: `vendor` asks for models from different companies where possible; `model` (the default) only for different models.
- **`require`**: models without which the site cannot work. Each element is a name, a mask ending in `*`, or an array of those meaning "any of these". A visitor who cannot give them is not offered Allow.
- **`recommend`**: models the site likes, in its order (names and masks). A site that only recommends can always be allowed.
- **Names** are in OpenRouter's style, `vendor/model`, without regard to case; dots, underscores, date suffixes and the variant after a colon (`:free`) do not matter, so `claude-opus-4.6`, `claude-opus-4-6` and `claude-opus-4-6-20260101` are one name. A mask (`openai/gpt-5*`) matches every model of that vendor whose name begins so. A name without a vendor is compared by its model part only. The wallet matches names against the models the visitor has, whichever road each comes by (a key at OpenRouter, a key at the vendor, a program on his PC); it never tells the site which road.
- **`why`** (at most 160 characters) is what the consent window shows at once; **`whyLong`** (at most 2,000) is behind a "More" button. Both are the site's words, shown as such.
- **`names`**: `true` asks to see the names of the models it is given. The visitor may hide them.

**What the wallet does.** It proposes the models to tick: first those the site requires, then what the visitor gave this site last time, then the site's recommendations that he has, then his default set, but only as far as the site's `min`; models behind a key he has but has not added are offered, and ticked only when too few remain. The visitor changes the ticks, adds models from his own keys, and allows. A site never sees a model he did not tick.

**What the site gets.** In the grant, `models`: per skill, a list of `{ "ref", "name", "id", "runs", "kind" }` in the visitor's order. `ref` (`m1`, `m2`...) is what a call names. `runs` is `local`, `home-server` or `api-key`. `kind` is `agent` when the visitor's own AI agent answers for the entry (it can search the web, so it may take much longer than a model) and `model` otherwise; it is sent even when the names are hidden, and a wallet that does not know the field sends none (a site then treats every entry as a model). `name` and `id` (the model's name as in `require`/`recommend`) are the real ones only when the site asked for names and the visitor allows; otherwise `name` is `Model A`, `Model B`... and `id` is `null`. Two roads to one model are one entry; the wallet may use either, but it never substitutes another model: a call for a model that cannot answer is `ok:false`. A call for a `ref` the site was not given is `ok:false`.

**Changes.** The visitor may add, remove and reorder a site's models, hide the names and change its limits at any time. Over the direct link the wallet sends a `grant` frame with the new `models` and `limits`; with the extension the page learns the models at its next `request`.

**Older wallets** ignore the block: the grant has no `models`, and the site works with the one model the wallet routes to, or does without a wallet if it needs several.

### 3.4 Limits of a text call

The size of what a site may send, the length of an answer, the wait and the number of calls at once are limits with three layers: the visitor's defaults for every site (Limits page of the wallet), his own for one site, and what the site declares in `purpose.limits`. The tightest wins, and nothing goes above the wallet's ceilings.

| manifest name | meaning | default | ceiling |
|---|---|---|---|
| `maxTextChars` | characters of messages in one call | 100,000 | 400,000 |
| `maxAnswerTokens` | tokens in one answer | 20,000 | 32,000 |
| `maxCallSeconds` | the longest wait for one answer (a queue in front of a busy model included) | 300 | 600 |
| `maxParallelCalls` | calls of the site in flight at once | 8 | 32 |
| `maxCallsPerMinute` | calls a minute | 300 | 600 |

- A declared limit is the site's own: it holds the site back, as the rules of [rules-0.1-draft.md](rules-0.1-draft.md) say for `maxTextChars` and `maxCallsPerMinute`, and the wallet applies the other three the same way. It is also what the site asks for: when it is above the visitor's, the consent window says so and offers to accept it, once, with a tick (on by default). A site that asks for the same again is not asked about again; one that asks for more is.
- The 0.0.2 libraries know only `maxClipSeconds`, `maxCallsPerMinute` and `maxTextChars`: they report the other three as `limit-unknown` warnings and ignore them. The wallet reads them itself.
- The grant tells the site what it was held to (`limits`, in the same names). A site stays within them: a call over a limit is `ok:false`.

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
