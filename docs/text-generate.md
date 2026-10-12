# text.generate: the wallet's second skill

Status: working version, 12 October 2026. The wallet serves speech and text generation through the same engine, gate, route and activity log. The measurements in section 6 come from real runs on the machine this was built on.

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

`language` narrows to the primary tag (`de-AT` → `de`) and defaults to the manifest's only language. One text source answers one call at a time; while it is busy the wallet waits 10 s and then moves to the next source. Each source gets at most 40 s and the complete text route gets 74 s, leaving room inside the page's 90 s call timeout. The caller's cancellation is passed to the provider and releases the reservation when it leaves. Every refusal — too many messages, a bad role, an empty input, the gate's limits — comes back as `{ ok: false }` with a plain reason in the activity log, and no source ever sees a broken request.

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

A thinking model must not leak its thoughts to a page. The wallet removes closed or unterminated `<think>...</think>` and `<reasoning>...</reasoning>` blocks; removes `<|channel|>thought|analysis|reasoning` blocks through the next `<|channel|>final|answer` marker (including optional `<|message|>` markers); and removes a leading `Thinking:`, `Reasoning:`, or `Thought process:` block through `Answer:` or `Final answer:`. It also accepts a schema-constrained choice found in `reasoning_content` without exposing that channel. A local model that burns its whole token budget on hidden reasoning gets exactly one more try with four times the room (at most 4096 tokens); a paid source does not get that extra paid attempt.

## 3. Sources

**`OpenAiCompatibleSource`** — one model in a runtime that speaks the OpenAI chat/completions protocol: LM Studio (default `http://127.0.0.1:1234/v1`), Ollama (default `http://127.0.0.1:11434`, the wallet appends `/v1`), or any other server the visitor added with an explicit address (llama.cpp, Jan, vLLM…). A numeric loopback address is classified as `local-model`; every other host is `home-server` and shown as “another computer (host)”. Answers are bounded to a few megabytes, and the shared client **never follows redirects**: an endpoint on this PC cannot send the wallet's requests — or a visitor's API key — onward to another computer.

**`OpenRouterTextSource`** — paid, through the visitor's OpenRouter key (from the secret store, by `SecretRef`). The cost comes back from the API's `usage` (asked for explicitly); before a call, the estimate comes from OpenRouter's public price list (read about once an hour) or a conservative flat $0.02 per answer. A failed call never quotes the key, not even when OpenRouter's own error echoes it.

**`FakeTextSource`** — the tests'. The engine, the router and the hygiene rules do not know the difference.

## 4. The runtime scanner

`RuntimeScanner` asks each runtime who it is and what it has loaded, at most one probe every 700 ms per endpoint, every 30 s from the `SourceSet`'s loop — and never throws. Probe bodies are bounded to 512 KB, and malformed added endpoints are reported as a stopped runtime:

- **LM Studio** on `127.0.0.1:1234`: `/api/v0/models` (its `state` says which models are loaded), falling back to plain `/v1/models`.
- **Ollama** on `127.0.0.1:11434`: `/api/tags` for the installed models, `/api/ps` for the loaded ones.
- an added openai-compatible server on the address the visitor typed: `/models`.

The scanner sets each source's `Up` (is the runtime running) and `Listed` (does it still offer the model); the router reads those to skip a source with a reason people can read: "not running", "model not found", "no API key", "failed to load", "turned off". OpenRouter is never probed on the local network. A source whose address is not a parsed loopback IP is marked "runs on another computer (host)" and shown that way.

## 5. The chain and the settings

One `SourceSetting` in the settings builds one text source (kind, model, optional label, optional endpoint, skills). `SourceSet` keeps them in one stable list (`SourceSet.Text`) that the engine holds for its whole life — a settings change swaps one array, atomically; `SourceSet.FindText(id)` returns one source by its canonical id, including the endpoint. The registry adds a new source and its route stop in one settings write, before the first paid stop, so a local model is tried before anything that costs money. A rebuild never writes settings back while handling its own `Changed` event. The desktop needs no adapter of its own — see [agents/NEEDS-text.md](agents/NEEDS-text.md).

## 6. GigaAM, CPU Whisper and hardware defaults

GigaAM v3 is the first speech choice for Russian. If the visitor has no matching model folder, the wallet downloads the five pinned files (about 230 MB) into `models\\gigaam\\sherpa-onnx-nemo-transducer-giga-am-v3-russian-2025-12-16`. It writes to an owned staging directory, checks every published size and SHA-256, and moves the complete directory into place only at the end. An interrupted or corrupt download never becomes a usable model. A GigaAM folder already found in the visitor's own model folders is used without copying or changing it.

The pinned Hugging Face LFS metadata is: `encoder.int8.onnx` 224,570,814 bytes, SHA-256 `0ec54e5f130a91c0f228d21795ede10b31c992a1f73e45dcd827e843a71a9b50`; `decoder.onnx` 3,331,651, `5d1cfef155d8e07f213bd2f3229b5f6c5c29355c7fa5c1b9eb62cb1925725254`; `joiner.onnx` 1,440,448, `fd1d02f45c2ad3d6b67cc149811ad794ab4b020ed49a0a9e2790a8619d1cddd8`; `tokens.txt` 196, `17cc514451bcceac9c280068c71502f8448f99e9fb1456b8d0761651fd0392f2`; and `LICENSE` 1,070, `f00de6715714c7a63d08639cdbfaa40224eefc407302614bd19f1a8b98c875aa`.

At startup the wallet probes Vulkan once, before any Whisper factory exists. It accepts a real integrated, discrete, or usable virtual physical device; software renderers such as llvmpipe and Microsoft Basic Render are rejected. With AVX and a usable GPU the native order is `[Vulkan, Cpu, CpuNoAvx]`: large Whisper uses the GPU and the CPU Whisper source says that the GPU build is in use. Without a usable GPU the order is `[Cpu, CpuNoAvx]`, and the default speech chain is GigaAM for Russian, small CPU Whisper for other languages, then the paid fallback; the large model remains available as an explicit choice. Without AVX both local Whisper sources say `unavailable: this processor has no AVX (too slow)` and are skipped. `BYOA_WHISPER_RUNTIME=auto|gpu|cpu` overrides the automatic GPU choice for diagnostics or an informed visitor; it is read once at startup.

The CPU fallback (`WhisperCpuSource`) is the speech skill's safety net for machines where the big GPU Whisper does not work. It is the same whisper.cpp engine with the GPU switched off and all cores allowed, its own copy of **ggml-small-q5_1** (190 MB) in the models folder of the wallet home, downloaded and SHA-256-checked on first use.

**Why small and not base** — both measured on this machine (24 logical cores, CPU runtime), six synthesized speech clips of 3.9–4.4 s, three English (Piper `lessac-medium`) and three Spanish (Piper `davefx-medium`), word error rate against the spoken text:

| model | download | per clip | real time | wer all | en | es |
|---|---|---|---|---|---|---|
| ggml-base-q5_1 | 60 MB | 0.4 s | 10.2× | 6.2% | 8.6% | 3.3% |
| ggml-small-q5_1 | 190 MB | 1.2–1.5 s | 2.9× | 0.0% | 0.0% | 0.0% |

Base misheard real words: "She poured a" → "Cheap or to", Spanish "duerme" → "tuerme" — exactly the kind of single-word mistake the wallet's clips are full of. Small was letter-perfect in both languages, including "mañana" and "niños" with the diacritics. Three times slower is still a fifth of a second's speech in half a second — fine for a fallback, so the extra 130 MB download buys the accuracy.

**The native-runtime lesson.** whisper.net chooses its native library order process-wide. The wallet decides that order once, before either factory exists: Vulkan first only when the probe found a usable GPU, then the AVX CPU build and the no-AVX build; otherwise CPU first. This keeps the CPU factory from loading after Vulkan without locking GPU machines out of the fast path. On a processor without AVX both Whisper sources are unavailable with the honest state `unavailable: this processor has no AVX (too slow)` and the router skips them.

The router keeps the two out of each other's way: while the big Whisper is ready, the CPU source is skipped ("the big Whisper already works on this PC"). If the big model is still downloading, the CPU stop remains eligible when its own model is ready, so a fresh machine does not pay or wait unnecessarily.

**text.generate against the real LM Studio** (qwen/qwen3.5-4b on 127.0.0.1:1234, through `OpenAiCompatibleSource`, 12 October 2026): a plain question took 1.4 s (2.5 s on the day's first, cold call — the model's warm-up); ten yes/no questions with `choices: ["yes", "no"]` came back **10/10 valid and 10/10 correct**, 0.3–0.5 s per answer, schema-constrained, no retries needed.

## 7. What the tests cover

`wallet/tests/Byoa.Wallet.Tests` covers the engine path end to end (caps, gate limits, paid settlement on empty and choice-miss failures, the second paid attempt, cancellation, hidden reasoning, fallback order, the paid question and the session budget); the OpenAI-compatible source and OpenRouter against raw loopback fakes (bounded bodies, redirects, schema fallbacks, token retries, the key that never shows in an error, prices from usage and from the price list); the scanner and `SourceSet` (stable replacement, duplicate ids, malformed endpoints, endpoint-aware identities and disposal); language-specific routes, consent wording, and CPU Whisper's no-AVX state. The same tests cover `CleanAnswer` and `MatchChoice` formats documented above.
