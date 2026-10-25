# text.generate: the wallet's second skill

Status: working version, 12 October 2026; the speech sources were reworked on 17 October 2026 (section 6). The wallet serves speech and text generation through the same engine, gate, route and activity log, and holds no model: both skills are served by programs that run one. The measurements in section 6 come from real runs on the machine this was built on.

## 1. The page API

```js
await byoa.call(grantId, "text.generate",
                { messages: [{ role: "user", content: "…" }, …] },
                { maxTokens, temperature, language, choices, thinking })
//                                     → { ok: true, output: { text }, receipt: { cost, provider } } | { ok: false }
```

A manifest asks for it like any skill: `"skills": { "text.generate": { "required": true, "languages": ["de"] } }`. The languages are optional; `maxTextChars` in `purpose.limits` caps what the wallet will forward (checked by the gate, as `clip-too-long` is for speech — the code is `text-too-long`). Roles: `system`, `user`, `assistant`. The page gets the answer text and a receipt; it never learns which source answered.

**The wallet's own caps**, whatever a page asks for:

| what | default | ceiling | who sets it |
|---|---|---|---|
| characters across all messages (`maxTextChars`) | 100 000 | 400 000 | the visitor, per site; the site may ask |
| `maxTokens` (the answer's length, `maxAnswerTokens`) | 512 if the page names none | 32 000 (default limit 20 000) | the visitor, per site; the site may ask |
| the longest wait for a call (`maxCallSeconds`) | 300 s | 600 s | the visitor, per site; the site may ask |
| calls in flight at once (`maxParallelCalls`) | 8 | 32 | the visitor, per site; the site may ask |
| calls a minute (`maxCallsPerMinute`) | 300 | 600 | the visitor, per site; the site may ask |
| messages | 128 | 128 | fixed |
| `temperature` | 0.7 | 0–1 | the page |
| `thinking` | on (the model decides) | `false` / `"off"` turns it off | the page asks; the visitor's switch per model wins |
| `choices` | 20 × 100 chars, 500 together | — | fixed |
| the answer back to the page | 128 000 chars (cut) | — | fixed |

The limits are the visitor's own (Limits page, for every site and for one), held back by what the site declared in `purpose.limits`; see [spec/CONTRACT.md](../spec/CONTRACT.md) section 3.4. `language` narrows to the primary tag (`de-AT` → `de`) and defaults to the manifest's only language. A local source answers one call at a time and a source behind a key four; a call on the wallet's route waits 10 s for a busy source and then moves to the next, while a call for one named model (below) has nothing to fall back to and waits in line for its own limit. Each call gets the visitor's wait limit (300 s unless set otherwise) for the source and for the whole route. The caller's cancellation is passed to the provider and releases the reservation when it leaves. Every refusal — too many messages, a bad role, an empty input, the gate's limits — comes back as `{ ok: false }` with a plain reason in the activity log, and no source ever sees a broken request.

### Thinking

A model that reasons before it answers (Qwen3.5, DeepSeek-R1 and the like) can spend minutes, and the whole `maxTokens`, on hidden thoughts, and then answer nothing. A page that asks something simple passes `params.thinking: false`; the wallet then sends a local runtime `chat_template_kwargs: { enable_thinking: false }` and the model answers at once. The visitor has the same switch for each model of his on this PC or on a server of his own (Sources, "Let the model think before it answers", on by default), and it wins over a page that wants thinking. A model behind an API key and a model that does not reason are not changed. A reasoning model that is left to think and spends its whole budget gets one more try with four times the room, which on a slow machine may be longer than the wait limit.

### Several models, and a stream

A site may ask for several different models ([spec/CONTRACT.md](../spec/CONTRACT.md) section 3.3): the consent window ticks what the visitor has, the grant lists the models the site got (`ref`, name, id, where it runs), and a call names one with `params.model`. A call that names no model goes to the site's models in the visitor's order; a site that asked for no models gets the wallet's own route, as before. `params.stream: true` over the direct link answers in `chunk` frames and then the result. Models behind a key sit at OpenRouter (hundreds of models, one key) or at a vendor's own API (OpenAI, Anthropic, Google, MiniMax, DeepSeek, xAI, Z.ai (also its Coding Plan address), Mistral, or an address the visitor names); the catalog of OpenRouter, read once a day and kept on disk, gives the pickers their list and the price of a call.

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

**`OpenAiCompatibleSpeechSource`** — the same kind of runtime, for the skill `audio.transcribe`: see section 6.

**`FakeTextSource`** — the tests'. The engine, the router and the hygiene rules do not know the difference.

## 4. The runtime scanner

`RuntimeScanner` asks each runtime who it is and what it has loaded, at most one probe every 700 ms per endpoint, every 30 s from the `SourceSet`'s loop — and never throws. Probe bodies are bounded to 512 KB, and malformed added endpoints are reported as a stopped runtime:

- **LM Studio** on `127.0.0.1:1234`: `/api/v0/models` (its `state` says which models are loaded), falling back to plain `/v1/models`.
- **Ollama** on `127.0.0.1:11434`: `/api/tags` for the installed models, `/api/ps` for the loaded ones.
- **Llama** (the app from the llama.cpp team and Hugging Face, <https://llama.app>) on `127.0.0.1:9931`: a plain OpenAI-compatible server, asked at `/v1/models`. It has no source kind of its own: the scanner lists it under `openai-compatible` and only the window and the messages call it "Llama" (`LocalRuntimes`). A model added from it is an ordinary `openai-compatible` source. When no local model app answers, Add a model offers a "Get Llama" button that opens `llama.app` in the browser; the wallet downloads and installs nothing itself. The standard-port rule for agents is unchanged: an agent that adds port 9931 still needs the visitor's confirmation.
- an added openai-compatible server on the address the visitor typed: `/models`.

The scanner sets each source's `Up` (is the runtime running) and `Listed` (does it still offer the model); the router reads those to skip a source with a reason people can read: "not running", "model not found", "no API key", "failed to load", "turned off". OpenRouter is never probed on the local network. A source whose address is not a parsed loopback IP is marked "runs on another computer (host)" and shown that way.

## 5. The chain and the settings

One `SourceSetting` in the settings builds one source (kind, model, optional label, optional endpoint, one skill): a text source, or, with the skill `audio.transcribe`, a speech source. `SourceSet` keeps the text sources in one stable list (`SourceSet.Text`) and the speech sources, the paid one last, in another (`SourceSet.All`), which the engine holds for its whole life — a settings change swaps one array, atomically; `SourceSet.FindText(id)` returns one source by its canonical id, including the endpoint. The registry adds a new source and its route stop in one settings write, before the first paid stop, so a local model is tried before anything that costs money. A rebuild never writes settings back while handling its own `Changed` event. The desktop needs no adapter of its own — see [agents/NEEDS-text.md](agents/NEEDS-text.md).

## 6. Speech to text through a program: no model inside the wallet

Until 16 October 2026 the wallet carried its own speech engines (GigaAM through sherpa-onnx, Whisper through Whisper.net, with model downloads, folders, GPU probes and the licences that came with them). They are gone. A wallet is a go-between: the model runs in a program the visitor already trusts, and the wallet hands it the request. That removes the native libraries, the download manager, and the question of who updates a model, from the wallet.

**`OpenAiCompatibleSpeechSource`** is the speech counterpart of `OpenAiCompatibleSource`: one speech model in a program on this PC (or a confirmed home server) that takes audio in a chat request. The clip goes to `POST {endpoint}/chat/completions` as a user message with an `input_audio` part (16 kHz mono WAV, base64) and one text part: "The audio is in Russian (write it in Cyrillic). Transcribe it word for word in Russian, exactly as spoken. Do not translate. Output only the transcription, nothing else." (the language from the call; without one, "in the language of the speaker"). `temperature` is 0, `max_tokens` grows with the clip (16 per second plus 64, never above 2048), and `chat_template_kwargs.enable_thinking` is false. The answer is the message's `content`, without the `language Russian<asr_text>` tag that Qwen3-ASR puts in front, and without a `<think>` block. One clip at a time per source: a local runtime answers one request, and the second clip waits. The error text of a refused call (`error.message`, one short line) is kept, so a Test with a model that cannot hear audio says so ("image input is not supported - hint: provide the mmproj") instead of "did not answer".

The first call after the program starts may load the model, which took about 75 s through the Llama app from a cold disk; later calls 0.1-0.9 s. That is what the preparing step is for: after Allow the wallet sends one short silent clip to the first local speech source of each language, so the first real phrase is as quick as the others, and waits at most 30 s for it.

One added source serves one skill, and a speech source's id ends in `#transcribe` (`openai-compatible:renesas/qwen3-asr-1.7b-gguf:fp16@http://127.0.0.1:9931/v1#transcribe`), so the same model can be added once for text and once for speech. The paid ElevenLabs Scribe v2 through OpenRouter (`openrouter-scribe`) is still built in, as the last stop of the speech route; the wallet's route for a new install is that stop alone.

**Which model.** The same 96 synthetic clips (Piper voices: Russian and English country names and short sentences; no noise, no children's voices, so absolute numbers are friendlier than life) went through each candidate. Russian and English words out of 24, sentences exact out of 12 (word error rate in brackets), time per clip:

| Model | RU words | RU sentences | EN words | EN sentences | Time |
|---|---|---|---|---|---|
| **Qwen3-ASR 1.7B, in the Llama app** | 19 | 10 (0.03) | 22 | 12 | 0.15-0.9 s on a GPU |
| Whisper large-v3 (CPU int8; what Speaches / faster-whisper servers run) | 17 | 10 (0.07) | 22 | 12 | about 4.5 s on a CPU |
| GigaAM v3 (what the wallet used to carry) | 17 | 9 (0.07) | – | – | 0.07-0.1 s |
| Parakeet v3 | 14 | 10 (0.07) | 19 | 12 | 0.08-0.14 s |
| Gemma 4 E4B Q4_0, in Llama | 14 | 10 (0.07) | 20 | 12 | 0.15-0.35 s |
| Gemma Q8_0, in Llama | 12 | 8 (0.09) | – | – | – |

Qwen3-ASR is both the best of these on the clips and a model the Llama app runs with no extra server, so it is the one we recommend; accuracy did not depend on the text prompt (it was the same without one). The one hard Russian voice ("denis") was hard for every model. Slovak is third priority and was left out of this round. Qwen3-ASR sits in `Renesas/Qwen3-ASR-1.7B-GGUF` (FP16 with its `mmproj` file); it works the same through the Llama app's router (model id `Renesas/Qwen3-ASR-1.7B-GGUF:FP16`) as through a plain `llama.exe serve`.

**text.generate against the real LM Studio** (qwen/qwen3.5-4b on 127.0.0.1:1234, through `OpenAiCompatibleSource`, 12 October 2026): a plain question took 1.4 s (2.5 s on the day's first, cold call — the model's warm-up); ten yes/no questions with `choices: ["yes", "no"]` came back **10/10 valid and 10/10 correct**, 0.3–0.5 s per answer, schema-constrained, no retries needed.

## 7. What the tests cover

`wallet/tests/Byoa.Wallet.Tests` covers the engine path end to end (caps, gate limits, paid settlement on empty and choice-miss failures, the second paid attempt, cancellation, hidden reasoning, fallback order, the paid question and the session budget); the OpenAI-compatible source and OpenRouter against raw loopback fakes (bounded bodies, redirects, schema fallbacks, token retries, the key that never shows in an error, prices from usage and from the price list); the scanner and `SourceSet` (stable replacement, duplicate ids, malformed endpoints, endpoint-aware identities and disposal); the speech source against a fake Llama (the `input_audio` body, the language instruction, the tag stripped from the answer, the runtime's own error, one clip at a time, the warm-up clip), the provider set's speech list and the migration of a settings file from the version with the engines inside, speech models added, tested, moved and removed through the registry; language-specific routes and consent wording. The same tests cover `CleanAnswer` and `MatchChoice` formats documented above.
