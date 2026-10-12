# text.generate: the wallet's second skill

Status: working version, 12 October 2026. The speech skill is described in [wallet-0.1.md](wallet-0.1.md); this is its text sibling, built on the same engine, gate, route and activity log. The measurements in section 6 come from real runs on the machine this was built on.

## 1. The page API

```js
await byoa.call(grantId, "text.generate",
                { messages: [{ role: "user", content: "…" }, …] },
                { maxTokens, temperature, language, choices })
//                                     → { ok: true, output: { text }, receipt: { cost, provider } } | { ok: false }
```

A manifest asks for it like any skill: `"skills": { "text.generate": { "required": true, "languages": ["de"] } }`. The languages are optional; `maxTextChars` in `purpose.limits` caps what the wallet will forward (checked by the gate, as `clip-too-long` is for speech — the code is `text-too-long`). Roles: `system`, `user`, `assistant`. The page gets the answer text and a receipt; it never learns which source answered.

**The wallet's own caps**, whatever a page asks for:

| what | at most | default |
|---|---|---|
| messages | 32 | — |
| characters across all messages | 20 000 | — |
| `maxTokens` | 1024 | 512 |
| `temperature` | 0–1 | 0.7 |
| `choices` | 20 × 100 chars, 500 together | — |
| the answer back to the page | 4000 chars (cut) | — |

`language` narrows to the primary tag (`de-AT` → `de`) and defaults to the manifest's only language. One text source answers one call at a time; while it is busy the wallet waits 10 s and then moves to the next source. A source gets 120 s. Every refusal — too many messages, a bad role, an empty input, the gate's limits — comes back as `{ ok: false }` with a plain reason in the activity log, and no source ever sees a broken request.

## 2. What happens on a call

The same path as speech: the grant must be live and belong to the calling origin, the gate checks the call against the manifest (`maxTextChars`, rate, budget — a paid source reserves its estimate first, the real cost replaces it after), then the route walks the skill's chain: the first source that is on, available and knows the language tries; if it fails, is busy or gives an unusable answer, the next one is tried. The activity log records skill, language, the source that answered, the fallback chain and the cost — never the messages or the answer text.

A required `text.generate` no source can serve is caught at the consent window already: the skill shows as missing, Allow is not offered, and the page gets no grant at all.

### choices

A page that asks a yes/no question passes `choices: ["yes", "no"]` and must get exactly one of those strings back, or a plain error:

1. The source that supports structured output sends the schema (`response_format` with a one-property enum; OpenRouter additionally `provider.require_parameters`, so a provider that silently ignores the schema is refused instead of answered).
2. A runtime that answers HTTP 400 to the schema gets the choices in the prompt instead ("Answer with exactly one of the following and nothing else: \"yes\" | \"no\".") and remembers the refusal for the rest of the wallet's life.
3. The answer is dressed down: reasoning tags removed, trimmed, quotes and trailing punctuation off, inner whitespace folded, case folded. `"  \"YES\"! "` is the page's `yes`. Only a whole-answer match counts — "absolutely not" is not `no`.
4. No match: the source is asked once more with the instruction in the prompt. Still no match: the next source in the chain; if none answers, `{ ok: false }` with "no source answered with one of the choices".

### hidden reasoning

A thinking model must not leak its thoughts to a page. The wallet strips `<think>…</think>` (and an unterminated `<think>` — everything after it is reasoning too), ignores the `reasoning_content` channel for the answer (but accepts a schema answer that arrived there), and a local model that burnt its whole token budget on hidden reasoning gets exactly one more try with four times the room (at most 4096 tokens, local only — a paid source would spend the visitor's money twice, so its route hears the failure at once).

## 3. Sources

**`OpenAiCompatibleSource`** — one model in a runtime that speaks the OpenAI chat/completions protocol: LM Studio (default `http://127.0.0.1:1234/v1`), Ollama (default `http://127.0.0.1:11434`, the wallet appends `/v1`), or any other server the visitor added with an explicit address (llama.cpp, Jan, vLLM…). Answers one request at a time (its `Up`/`Listed` state comes from the scanner). Every text source and the scanner share one `HttpClient` that **never follows redirects**: an address on this PC cannot send the wallet's requests — or the visitor's API key — onward to another computer.

**`OpenRouterTextSource`** — paid, through the visitor's OpenRouter key (from the secret store, by `SecretRef`). The cost comes back from the API's `usage` (asked for explicitly); before a call, the estimate comes from OpenRouter's public price list (read about once an hour) or a conservative flat $0.02 per answer. A failed call never quotes the key, not even when OpenRouter's own error echoes it.

**`FakeTextSource`** — the tests'. The engine, the router and the hygiene rules do not know the difference.

## 4. The runtime scanner

`RuntimeScanner` asks each runtime who it is and what it has loaded, at most one probe every 700 ms per endpoint, every 30 s from the `SourceSet`'s loop — and never throws:

- **LM Studio** on `127.0.0.1:1234`: `/api/v0/models` (its `state` says which models are loaded), falling back to plain `/v1/models`.
- **Ollama** on `127.0.0.1:11434`: `/api/tags` for the installed models, `/api/ps` for the loaded ones.
- an added openai-compatible server on the address the visitor typed: `/models`.

The scanner sets each source's `Up` (is the runtime running) and `Listed` (does it still offer the model); the router reads those to skip a source with a reason people can read: "not running", "model not found", "no API key", "failed to load", "turned off". OpenRouter is never probed on the local network. A source whose address is not loopback is marked "runs on another computer" and shown that way.

## 5. The chain and the settings

One `SourceSetting` in the settings builds one text source (kind, model, optional label, optional endpoint, skills). `SourceSet` keeps them in one stable list (`SourceSet.Text`) that the engine holds for its whole life — a settings change swaps one array, atomically; `SourceSet.FindText(id)` returns one source by id ("lmstudio:qwen/qwen3.5-4b"). `Router.EnsureTextChain` gives every new source a stop in the `text.generate` chain, always before the first paid stop already there, so a local model is tried before anything that costs money. The desktop needs no new settings code of its own — see [agents/NEEDS-text.md](agents/NEEDS-text.md).

## 6. The CPU Whisper and the measurements

The CPU fallback (`WhisperCpuSource`) is the speech skill's safety net for machines where the big GPU Whisper does not work. It is the same whisper.cpp engine with the GPU switched off and all cores allowed, its own copy of **ggml-small-q5_1** (190 MB) in the models folder of the wallet home, downloaded and SHA-256-checked on first use.

**Why small and not base** — both measured on this machine (24 logical cores, CPU runtime), six synthesized speech clips of 3.9–4.4 s, three English (Piper `lessac-medium`) and three Spanish (Piper `davefx-medium`), word error rate against the spoken text:

| model | download | per clip | real time | wer all | en | es |
|---|---|---|---|---|---|---|
| ggml-base-q5_1 | 60 MB | 0.4 s | 10.3× | 6.2% | 8.6% | 3.3% |
| ggml-small-q5_1 | 190 MB | 1.3 s | 3.1× | 0.0% | 0.0% | 0.0% |

Base misheard real words: "She poured a" → "Cheap or to", Spanish "duerme" → "tuerme" — exactly the kind of single-word mistake the wallet's clips are full of. Small was letter-perfect in both languages, including "mañana" and "niños" with the diacritics. Three times slower is still a fifth of a second's speech in half a second — fine for a fallback, so the extra 130 MB download buys the accuracy.

**The Vulkan lesson.** whisper.net is told which runtime libraries to try, in order. With Vulkan first — the right order for the GPU Whisper — the CPU source crashed on this machine before its first inference (a native abort inside ggml), even with `use_gpu` off. The CPU source therefore loads the CPU runtime alone; the GPU Whisper keeps Vulkan-first and its own CPU fallback.

The router keeps the two out of each other's way: while the big Whisper is ready or already downloading, the CPU source is skipped ("the big Whisper already works on this PC"); it steps in when the GPU route cannot run.

**text.generate against the real LM Studio** (qwen/qwen3.5-4b on 127.0.0.1:1234, through `OpenAiCompatibleSource`, 12 October 2026): a plain question took 2.5 s including the model's first-token warm-up; ten yes/no questions with `choices: ["yes", "no"]` came back **10/10 valid and 10/10 correct**, 0.4–0.6 s per answer, schema-constrained, no retries needed.

## 7. What the tests cover

`wallet/tests/Byoa.Wallet.Tests` (271 tests): the engine path end to end (caps, gate limits, choices with the retry and the plain errors, hidden reasoning, fallback order, the paid question, the session budget); the OpenAI-compatible source and OpenRouter against raw loopback fakes (bodies on the wire, schema fallbacks, token retries, the key that never shows in an error, prices from usage and from the price list); the scanner against fake LM Studio and Ollama servers; routing and the chain order; the answer hygiene (`CleanAnswer`, `MatchChoice`) and the CPU Whisper's state.
