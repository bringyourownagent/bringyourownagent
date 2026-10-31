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
  // A site that works with text also gets its `limits` (section 3.4); a site that asked for models
  // gets the `models` it was given, one list per skill (section 3.3), each entry with `can` when the site
  // asked by properties, and `capabilities`, the feature names this wallet understands (section 3.3).
  request(manifest) -> Promise<{ grantId: string, skills: string[], expiresAt: string,
                                 limits?: { maxTextChars, maxAnswerTokens, maxCallSeconds, maxParallelCalls, maxCallsPerMinute },
                                 models?: { "<skill>": [ { ref, name, id, runs, kind, can?, abilities? } ] },
                                 capabilities?: string[] } | null>,

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
      "needs": { "input": ["text"], "output": ["text"], "context": 32 },
      "kind": "model",
      "onMismatch": "drop",
      "why": "Different AIs from different companies judge each side: less bias.",
      "whyLong": "Every model reads both versions of your question and scores both sides. ...",
      "names": true
    }
  }
}
```

- **`count`**: how many different models the site uses: a number, or `{ "min", "max" }` (1 to 64; `min` defaults to 1, `max` to 5). The block is read even without `count`: the defaults then apply, and `needs`, `kind`, `onMismatch` and `abilities` work as they do with it.
- **`distinct`**: `vendor` asks for models from different companies where possible; `model` (the default) only for different models.
- **`require`**: models without which the site cannot work. Each element is a name, a mask ending in `*`, or an array of those meaning "any of these". A visitor who cannot give them is not offered Allow.
- **`recommend`**: models the site likes, in its order (names and masks). A site that only recommends can always be allowed.
- **`needs`** (wallet 0.36): conditions on the *properties* of the sources (section 3.5): `"needs": { "input": ["text", "image"], "output": ["text"], "context": 64, "runs": ["api-key"] }`. At most the four known keys are read; a key the wallet does not know is ignored, and `x-` names are free for anyone. In one line: `require` picks by name, `needs` by properties — a source must satisfy both when both are given.
- **`kind`** (wallet 0.36): `"model"` (the default), `"agent"` or `"any"`: which sources may fill the skill. A site that wants the visitor's own agent writes `"kind": "any"` and names it as any model, `agent/<name>` or the mask `agent/*` (section 3.6). `kind` only filters; it ticks nothing.
- **`onMismatch`** (wallet 0.36): `"drop"` or `"show"`, and the default is `drop` for a model, `show` for an agent. `drop`: a source that fails a condition on a property the wallet *knows* is not offered at all. `show`: it is offered with the mismatch shown and never pre-ticked. A condition on a property the wallet does not know never drops anything: the source is offered with an "unknown" mark, not pre-ticked.
- **`abilities`** (wallet 0.36): for a skill that an agent may fill, the abilities the site hopes for, `["web.search", "web.read"]` (section 3.6). It never changes what an agent may do.
- **Names** are in OpenRouter's style, `vendor/model`, without regard to case; dots, underscores, date suffixes and the variant after a colon (`:free`) do not matter, so `claude-opus-4.6`, `claude-opus-4-6` and `claude-opus-4-6-20260101` are one name. A mask (`openai/gpt-5*`) matches every model of that vendor whose name begins so. A name without a vendor is compared by its model part only. The wallet matches names against the models the visitor has, whichever road each comes by (a key at OpenRouter, a key at the vendor, a program on his PC); it never tells the site which road.
- **`why`** (at most 160 characters) is what the consent window shows at once; **`whyLong`** (at most 2,000) is behind a "More" button. Both are the site's words, shown as such.
- **`names`**: `true` asks to see the names of the models it is given. The visitor may hide them.

**What the wallet does.** It proposes the models to tick: first those the site requires, then what the visitor gave this site last time, then the site's recommendations that he has, then his default set, but only as far as the site's `min`; models behind a key he has but has not added are offered, and ticked only when too few remain. The visitor changes the ticks, adds models from his own keys, and allows. A site never sees a model he did not tick.

**What the site gets.** In the grant, `models`: per skill — every skill the site was given models for, not only `text.generate` — a list of `{ "ref", "name", "id", "runs", "kind" }` in the visitor's order. `ref` (`m1`, `m2`...) is what a call names. `runs` is `local`, `home-server` or `api-key`. `kind` is `agent` when the visitor's own AI agent answers for the entry (it can search the web, so it may take much longer than a model) and `model` otherwise; it is sent even when the names are hidden, and a wallet that does not know the field sends none (a site then treats every entry as a model). `name` and `id` (the model's name as in `require`/`recommend`) are the real ones only when the site asked for names and the visitor allows; otherwise `name` is `Model A`, `Model B`... and `id` is `null`. Two roads to one model are one entry; the wallet may use either, but it never substitutes another model: a call for a model that cannot answer is `ok:false`. A call for a `ref` the site was not given is `ok:false`.

**`can`, and `capabilities`** (wallet 0.36). When a site wrote `needs` (or `abilities` for an agent), each entry of the list also carries `can`: one boolean for each condition the wallet recognised — `{"input":true,"output":true,"context":true}` — never a value, and a property the wallet does not know is simply absent, so a site that asked for three conditions and receives two verdicts knows the wallet could not check the third. An entry whose `kind` is `agent` also carries `abilities`, one boolean for each ability the site asked about: `{"web.search":true}`. Both objects hold at most four keys. Next to `models`, the grant carries `capabilities`: the feature names of the closed list `needs`, `can`, `kind`, `cancel`, `image.generate`, `images-in`, `parts`, `frame-limit` that this wallet supports. A site never infers a feature from a missing key: a wallet that does not understand `needs` sends neither `needs` in `capabilities` nor any `can`, and the site uses its own path; an older wallet sends no `capabilities` at all, and a newer wallet behind an older extension or relay sends none of it either (the two transports pass the field on from wallet 0.36).

**Changes.** The visitor may add, remove and reorder a site's models, hide the names and change its limits at any time. Over the direct link the wallet sends a `grant` frame with the new `models` and `limits`; with the extension the page learns the models at its next `request`.

**Older wallets** ignore the block: the grant has no `models`, and the site works with the one model the wallet routes to, or does without a wallet if it needs several. Wallet 0.36 reads `needs`, `kind`, `onMismatch` and `abilities` as well; a wallet before it ignores them as it ignores the block, and the site finds no `capabilities` and no `can`.

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
- The 0.0.2 libraries know only `maxClipSeconds`, `maxCallsPerMinute` and `maxTextChars`; 0.0.3 adds `maxFrameBytes`. The limits they do not know are reported as `limit-unknown` warnings and ignored. The wallet reads them itself.
- The grant tells the site what it was held to (`limits`, in the same names). A site stays within them: a call over a limit is `ok:false`.
- `maxFrameBytes` (a whole number of bytes) is the one limit of the direct link alone: it says what a site accepts in one frame from the wallet ([CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) section 4). No gate rule counts it: it bounds frames, not calls.

### 3.4a The wallet's picture limits (wallet 0.37)

Pictures have limits of their own, and they are never a site's to declare: `maxImages`, `maxImageBytes`, `maxResultBytes` and `maxImagesPerMinute` are the wallet's and the visitor's alone (the gate has no picture vocabulary and fails closed on an unknown limit). A site never writes them in `purpose.limits`; the grant tells the site what it was held to.

| grant name | meaning | default | ceiling |
|---|---|---|---|
| `maxImages` | pictures in one call, in either direction | 4 | 8 |
| `maxImageBytes` | decoded bytes of one picture | 1,000,000 | 3,000,000 |
| `maxResultBytes` | decoded bytes of all pictures of one answer | 8,000,000 | 16,000,000 |
| `maxImagesPerMinute` | pictures a minute | 20 | 60 |

All the pictures a site sends in one call together are at most 3,000,000 decoded bytes, whatever `maxImages` and `maxImageBytes` allow, so the call frame stays inside the 4 MB a wallet accepts.

### 3.5 Properties and the primary-role rule (wallet 0.36)

Every source a wallet can offer a site — a model or the visitor's own agent — has four properties. A site states conditions on them in `needs` (section 3.3); the wallet answers, per entry of the grant's `models`, in `can`.

| Property | Meaning | Where the wallet gets it, in order |
|---|---|---|
| `input` | modalities it accepts: `text`, `image`, `audio` | the visitor's declaration for that source; the provider's catalogue (`architecture.input_modalities`); unknown |
| `output` | modalities it produces | the same, from `output_modalities` |
| `context` | context length, in thousands of tokens | the same, from `context_length`; unknown for most local models |
| `runs` | where it runs: `local`, `home-server` or `api-key` | derived from the source kind; never unknown |

The visitor's declaration always wins over the catalogue, and the wallet shows which of the two each value came from. In "Add a model" and in a source's own settings the visitor gets a small "What it can do" box (reads pictures, draws pictures, hears audio) and the tick **"Also use as a text model"** (`alsoText`, per source, off by default, meaningful only for a source whose `output` includes `image`).

**The primary-role rule (absolute).** A skill's built-in properties come first, and a site's `needs` can only narrow them, never widen them:

- `text.generate` is offered only sources whose `output` includes `text` and does not include `image` — unless the visitor ticked `alsoText` for that source. No site key (`needs`, `kind`, `require`) lifts the second clause; only the visitor's own tick does. A site that wants a picture in an answer asks for `image.generate`, whose result shape carries it. A source whose `output` is unknown is offered, marked "unknown", and never pre-ticked.
- `image.generate` (wallet 0.37) is offered only sources whose `output` is known to include `image`.

The rule holds everywhere a source is chosen: the consent window, the grant, the wallet's own route, and every call. A stored choice, a reused answer or a silent renewal whose source no longer fits is dropped from the grant, and the visitor is asked again when the required count is no longer met.

**A changed `needs` is asking for more.** A new key, a new value or a raised minimum re-opens the consent window: the wallet never decides it on its own.

### 3.6 Agents and abilities (wallet 0.36)

A site asks for the visitor's own agent with `"kind": "agent"` (or `"kind": "any"`) and the name `agent/<name>` or the mask `agent/*` in `require` or `recommend`, as [agent-source.md](../docs/agent-source.md) says. Matching is soft by default (`onMismatch` is `show` for an agent): an agent that lacks an asked ability is still offered, and tickable.

- **`abilities`** (inside `models`): what the site hopes the agent's environment can do. The vocabulary of 0.1 is `web.search` and `web.read`; `image.read` is reserved and never pre-filled; `x-` names are free for extensions. `files.read`, `code.run`, `models.call`, `agents.call` and `image.make` are not in 0.1.
- The wallet **derives** an agent's abilities from the profile it launches (today: web search and page reading, and nothing else) and shows "can: ... / may lack: ...". It never asks the visitor to declare an ability the wallet cannot enforce.
- `kind` ticks nothing: only `require`, by name or mask, ticks an agent, exactly as for a model. A site that only recommends one gets no tick.
- An agent is offered only for a skill whose output is text. It never serves `image.generate`. A site never widens an agent's profile: what an agent calls under its own roof is the visitor's business, and the wallet's own MCP is not loaded for it.
- At call time, when an ability the site asked about is missing, the wallet adds one line to its own framing of the prompt — outside the site's fenced text — saying what the site hoped for and that the agent has no such tool, and the `result` carries `"lacks": ["web.read"]`: the missing names, and only the abilities the site asked about, next to `output` and `receipt`. The receipt stays cost and kind; `lacks` is the wallet's own statement, never the agent's prose.
- No picture is sent to an agent in this release (section 3.7).

### 3.7 `image.generate`, and pictures in `text.generate` (wallet 0.37)

Serving pictures is wallet 0.37. The text of this section is written now so that a wallet and a site can be built to it; a wallet before 0.37 serves no `image.generate` and no picture part, and a site that requires the skill gets no Allow — the safe failure an old wallet already gives for a skill it cannot serve.

**The call.**

```json
{"type":"call","id":7,"skill":"image.generate","model":"m1",
 "input":{"prompt":"...","references":[{"mime":"image/png","data":"<base64>"}],"count":1,"aspect":"1:1"},
 "params":{}}
```

- `prompt`: text, at most `maxTextChars`. `references`: pictures for the model to look at, only for a source whose `input` is known to include `image`; for any other source the call is `ok:false`. `count`: 1 to 4 pictures. `aspect`: one of `1:1`, `4:3`, `3:4`, `16:9`, `9:16`.
- The wallet detects the type and the size of every picture from the bytes themselves, never from a field the site filled in.

**The answer: parts, one shape on every transport.** The wallet sends each picture as frames before the `result`:

```json
{"type":"image","id":7,"index":0,"seq":0,"of":3,"mime":"image/png","data":"<base64, at most 512 KB>"}
{"type":"result","id":7,"ok":true,
 "output":{"images":[{"mime":"image/png","size":734000,"sha256":"...","reduced":false}]},
 "receipt":{"cost":"0.040 USD","provider":"api-key","model":"m1"}}
```

The `result` carries only metadata; the bytes are in the frames. The site joins the parts of each picture by `index`, checks the size and the `sha256`, and treats every picture as untrusted input. There is no second, inline shape: a small picture travels as parts like a big one, on the direct link, through the relay and through the extension alike (an extension frame is at most 1 MB, which is why a part is at most 512 KB of base64). A picture that is too big is reduced by the wallet itself — re-encoded, then scaled down — when the visitor's switch "Reduce big pictures myself" allows it, and `reduced: true` with `originalSize` says so; it is never reduced silently. A call that fails is `ok:false`, and the site discards every part it already received for it.

**Pictures in `text.generate`.** A message's `content` may be an array of parts:

```json
{"role":"user","content":[
  {"type":"text","text":"Turn this picture into clean SVG."},
  {"type":"image","mime":"image/png","data":"<base64>"}]}
```

only for a source whose `input` is known to include `image`. A site MUST NOT send an `image` part unless the grant's `can.input` for that model is `true`; without `capabilities` saying the wallet understands `can`, or with no `can` at all, it uses its own path. A wallet that cannot use a part it received answers `ok:false`, never an answer as if the part were absent. Picture bytes do not count toward `maxTextChars`; they count toward the picture limits of section 3.4a.

**Both skills together** (the icon site's manifest after this release; before it, the second skill has no pictures to look at and the site keeps its text-only manifest, with `needs: {"output":["text"]}` and `kind` if it wants them):

```json
"skills": {
  "image.generate": { "required": true, "models": { "count": { "min": 1, "max": 4 }, "needs": { "output": ["image"] },
    "recommend": ["google/gemini-3.1-flash-image*", "openai/gpt-image-*"], "why": "Draws the picture candidates for your icon." } },
  "text.generate": { "required": true, "models": { "count": 1, "kind": "any", "needs": { "input": ["text", "image"], "output": ["text"], "context": 64 },
    "recommend": ["anthropic/claude-sonnet-5.5", "agent/*"], "why": "Looks at a candidate and writes clean SVG code for it." } }
}
```

(`google/gemini-3.1-flash-image*` is a mask of the kind section 3.3 allows — one star, at the end. A mask with a star in the middle matches nothing.)

`image.generate` is served first by OpenRouter image-output models through the visitor's key; the wallet's estimate before a paid call covers `count` pictures, and "ask before paid" and budgets apply as for text. A picture is personal data by default: a site that sends pictures declares at least `personal`, and a `local-only` site is not served until a local image runtime exists.

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
