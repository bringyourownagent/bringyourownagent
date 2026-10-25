# BYOA — Vision

Draft, first published 10 October 2026, updated 11 October 2026. Author: [@ultrathinker](https://github.com/ultrathinker).

> Everything here is a proposal. The draft libraries (version 0.0.2) implement only [spec/rules-0.1-draft.md](../spec/rules-0.1-draft.md): checking a manifest and the hard limits of a single call. Parts marked **Proposed** are ideas for later drafts of the specification; nothing in them is implemented yet.

## 1. The shift

Until now a website served a person who came alone. More and more, a person comes with agents: paid AI subscriptions, API keys, models on their own computer or home server. Each agent has skills — understanding text, recognising speech, reading images and video, generating speech, images and video.

Sites, in turn, increasingly need exactly these skills, and today they must buy them and pay for every visitor. BYOA connects the two: **a site asks for skills, the visitor's agents provide them, the visitor controls the cost.**

BYOA is not an alternative to the AI companies; it connects them. The most capable agents often come with subscriptions from large providers, and they are central to the protocol. Next to them stand API keys, open models and models on the visitor's own hardware. The protocol gives every provider the same way into every site, and every person the same right to choose which agent to bring, or to use several at once.

### Models first, then agents

There are two levels, and BYOA starts with the first.

- **Level 1: skills, served by models.** A site asks for a skill — transcribe speech, translate, solve a math problem, describe an image — and the wallet answers each request with a model. One request in, one answer out: no memory, no tools, no steps the site cannot see.
- **Level 2: tasks, handed to an agent (Proposed, later).** A site hands a task to the visitor's agent: a model with tools and memory that works in several steps ("plan my next five Spanish lessons"). A first, small version exists in the wallet: one question, one answer, an agent that can search the web and read pages and nothing else, behind a guard (see [agent-source.md](agent-source.md)).

The name "Bring Your Own Agent" says where this goes: start with models, grow to agents. Agents raise a risk that models do not: an agent knows a lot about its owner (memory, history, other sites, contacts). At level 2 the wallet must limit what the visitor's agent may tell each site, per site and per kind of fact, with "nothing" as the default. The wallet's own manager (Section 8.3) is the first agent of this kind, and it works only for the visitor.

## 2. Roles

| Role | Who | Responsibility |
|---|---|---|
| **Visitor** | a person | owns the agents, sets budgets and conditions, approves grants, chooses a model for each skill |
| **Agent wallet** | software on the visitor's side (first a browser extension with a small local companion app) | holds keys and connections, matches requests to providers, routes each skill to its model and its fallbacks, meters, enforces limits, checks purpose, keeps the log |
| **Manager** (Proposed) | an AI agent inside the wallet | works only for the visitor: suggests which model serves each skill, explains why, confirms only within the visitor's rules |
| **Provider** | a subscription, an API key, an OAuth-connected account, a local model, a home server | performs the skill |
| **Requester** | a website, later also a desktop app or another agent | declares needs and purpose, calls skills within a grant, may offer a test and watch quality |

## 3. Core messages

The core is four messages. They do not depend on how they are carried.

1. **Manifest** — what the requester needs and why: required and optional skills with parameters (language, streaming, latency, minimum quality), the declared purpose, the data sensitivity, an expected cost per session. Proposed for later drafts: requirements as conditions on model properties and an optional test set (Section 4).
2. **Grant** — what the visitor allowed: skills, budget (per session, per hour, per day), validity period, privacy rule ("local only", "no training use"), the purpose conditions in force.
3. **Call** — one use of a skill under a grant.
4. **Receipt** — what it cost and which limits remain. The requester learns the cost, not the provider or the key.

```
Requester ── Manifest: "speech-to-text (ru, streaming), small text model; purpose: family game" ──▶ Agent wallet
                                                                                                     │ matches providers
Requester ◀── Grant: skills, 20¢ per session, 1 hour, "local only" ────────────────────────────────┤ asks the visitor (short consent)
Requester ── Call ──▶ Agent wallet ──▶ local model / API key / subscription / home server
          ◀── result + Receipt ──┘ meters, enforces limits, checks purpose, logs
```

### 3.1 The consent flow (Proposed)

How a wallet asks the visitor before a Grant. None of this is in the draft rules yet.

1. **A request only after a click.** A requester may ask the wallet only after the visitor clicks something on it (a user gesture). A request without one shows no prompt; the wallet keeps it quietly, for example as a dot on its icon.
2. **A short notice first.** "geo-game.example wants to use your AI": Show, Decline, or Block this site.
3. **A short consent window.** The site (verified domain), one line on what it does, its declared purpose quoted as the site's own words, one row per skill with the model the wallet's manager chose for it (and why, on request), one limit ("up to $0.20 per session"), and Allow or Decline. A skill no model of the visitor's can serve says so and names the fix; an optional skill can be left off. Tests, fallback chains and the other limits live in the wallet's detailed settings, not on this window.
4. **The same answer for every no.** Declined, blocked and "no wallet installed" look identical to the requester: AI is unavailable, and it falls back to its own mode. It cannot tell them apart, so asking again gains it nothing. A blocked site's later requests show no notice at all.
5. **Automatic decisions within limits.** The visitor may tick "next time, decide on my own within these limits". The same request is then allowed without a prompt, and the wallet speaks up only when a person is needed: a limit is reached, a paid fallback is needed, or the site warns about weak answers.
6. **Only the difference.** If the requester later asks for more (a new skill or a higher budget), the wallet asks again and shows only what changed.

## 4. Skills, requirements and tests

### 4.1 Skills

Skills are named capabilities, not models. The vocabulary of the 0.1 draft:

- `text.generate`, `text.understand` (classify, extract, answer), `text.embed`
- `audio.transcribe`, `audio.speak`
- `image.understand`, `image.generate`
- `video.understand`, `video.generate`

Each skill carries parameters (languages, streaming, context size, latency, quality tier). A requester may name preferred vendors or models, but the default is to ask for the skill.

### 4.2 Requirements are properties, not model names (Proposed)

A model is described by a set of key-value properties. Its real name is one optional property among them. A requester states what it needs as **conditions on properties**, and the wallet checks those conditions against the visitor's models. A requester may state requirements; it does not have to.

A starter vocabulary of standard keys:

| Key | Meaning | Example value | Typical condition |
|---|---|---|---|
| `name` | the model's own name; optional | `"speech-large-v3"` | rarely; requiring a vendor goes against "skills, not vendors" |
| `runs` | where the model runs | `"this-device"`, `"home-server"`, `"cloud"` | `oneOf` (for example local only for personal photos) |
| `input` | modalities it accepts | `["text", "audio", "image"]` | `includes` |
| `output` | modalities it produces | `["text"]` | `includes` |
| `languages` | languages it handles well (BCP 47 tags) | `["en", "ru", "sk"]` | `includes` |
| `context` | context length, in thousands of tokens | `32` | `min` |
| `latency` | typical seconds to the first answer | `0.6` | `max` |
| `speed` | tokens per second once it answers | `45` | `min` |
| `level` | an approximate general ability, 1 to 5 | `3` | `min` |
| `bench.<name>` | the score on a known public benchmark | `"bench.gsm8k": 84` | `min` |
| `cost` | expected cost of one call, as a money string | `"0.003 USD"` | `max` |
| `x-<name>` | extension keys, free for anyone to define | `"x-vad": true` | any |

Operators: `min`, `max`, `includes` (all the listed values are present), `oneOf`, `equals`. A condition on a key the model does not state is "unknown": the wallet shows it and the visitor decides. Unknown keys in a manifest are ignored, so newer manifests keep working with older wallets.

Requirements may differ per skill and even inside one skill. A skill can name **tasks**, each with its own conditions, so the wallet can route each task to a different model: solving a math problem needs a strong math benchmark, explaining the steps only a moderate level; translating into Chinese needs a stronger model than translating back. Next to `requires`, a requester may add softer `recommends` conditions that the manager weighs but does not enforce.

Where the properties come from: the provider's published description of the model, the wallet's own measurements (latency and speed are measured, not trusted) and the visitor's own notes. The wallet keeps them on the visitor's device.

An illustrative fragment of a manifest in the proposed format (inside a skill, these keys are skill parameters, which the 0.1-draft rules do not check):

```json
"skills": {
  "audio.transcribe": {
    "required": true,
    "requires": {
      "languages": { "includes": ["ru", "sk"] },
      "latency": { "max": 1 }
    },
    "test": { "modes": ["wallet", "site"], "scoring": "wer", "items": "/byoa/tests/countries.json" }
  },
  "text.generate": {
    "tasks": {
      "solve":   { "required": true,  "requires": { "bench.gsm8k": { "min": 75 }, "level": { "min": 3 } } },
      "explain": { "required": false, "requires": { "level": { "min": 2 } }, "recommends": { "level": { "min": 4 } } }
    }
  }
}
```

### 4.3 Tests (Proposed, optional)

Whether to test is the visitor's decision. Two modes, both supported:

- **The wallet runs the test.** The manifest carries a test set: inputs with reference answers. The wallet sends them to the candidate model and scores the answers by simple comparison — word error rate for transcription, exact match for math, required key words for a short description. No second model is needed, and the site learns nothing.
- **The site runs the test.** The visitor allows a separate, small test budget; the site calls the visitor's model a few times through the wallet and shows the score right away. This is the simplest for the visitor and the only way when "correct" cannot be checked by comparison, such as the quality of a translation or of a conversation.

Tests come in sizes: quick (about 5 items), medium (10 to 12) and full (30 to 40). Results are remembered on the visitor's device for each model and skill (and each requester's test set), and are re-checked from time to time, for example every 30 days, because providers update models without notice. Test calls are paid from the test budget, never from the requester's working budget.

### 4.4 Quality monitoring by the site (Proposed)

During normal work a requester may check the answers it receives (against a known answer, with a sanity check or with its own reviewer) and tell the wallet:

- **warn** — "your model gets Slovak names wrong; switch?";
- **pause** — the requester stops calling until the visitor switches to another model.

The protocol carries the signal; it does not judge quality. The wallet shows the message together with the manager's suggestion, and the visitor decides. A weak model is the visitor's own choice and responsibility, not the requester's.

## 5. Declared purpose

The idea comes from the author's earlier SSH-guard tool, where a connection is opened with a stated goal and a fast model checks every command against it.

- **The requester declares its purpose** in the manifest, in two layers: hard limits a program can check (only `audio.transcribe`; clips up to 15 seconds; at most N calls a minute; budget) and the purpose in words ("a family game: recognising country names").
- **The visitor may add their own conditions** in plain words: "only for the game; warn me if text is sent". The wallet turns them into rules and shows them for confirmation. Proposed: conditions and purposes can be set for the requester as a whole and for a single model or source ("API key: only clips under 10 seconds", "cloud models: never my voice").
- **Two layers of checking.** Code checks what code can check — reliably and for free. A small model, preferably local, judges only what code cannot (for example, long documents arriving instead of short phrases). The judge treats the content as data, never as instructions, because the content comes from the requester.
- **Reactions** escalate: allow, warn, pause and ask, block. Everything is logged.
- Purpose limitation is an old principle of data protection law; BYOA makes it machine-checked.

## 6. Privacy, trust and openness

The requester marks the sensitivity of the data it sends. The visitor sets rules per site: "local models only", "no providers that keep data", "only these vendors". The wallet routes accordingly or refuses.

### The trust rule

A requester must **never trust output from a visitor's model**, exactly as it never trusts any other user input (think of cross-site scripting and forged form fields). The visitor can connect any model, or a script that pretends to be one. Requesters validate, escape and sandbox what comes back.

So BYOA results are for tasks that benefit the visitor — learning, translation, assistance, accessibility — and never for scores, prizes, payments, rankings or moderation, where cheating would pay. A family game may use them because a cheating model only spoils the cheater's own game.

### Openness

The requester's prompts pass through the visitor's wallet, and the visitor can read every one of them in the log. This openness is accepted, not a problem to solve: a requester should not send through a visitor's model what it would not show the visitor. A requester that needs secret prompts should run its own models.

## 7. Local models

A local model or a home server is just another provider, and an important one:

- the data does not leave the visitor's machine or home;
- there is no per-request cost;
- no vendor's permission is needed.

A laptop with a 16 GB GPU runs speech recognition and a small language model with room to spare. Sites must never talk to a local model directly: a model server open to any web page lets any page use the visitor's GPU. The wallet stands in between.

## 8. Budgets, routing and limits

The visitor sets a budget per site and per time window, with a quick slider when a site first asks. After a session the visitor sees the actual cost ("this game took 6 cents") and can raise or lower the limit. Local models cost nothing in money; the wallet can still limit their use by time or load.

### 8.1 A model for each skill (Proposed)

For each skill of each requester, and for each task inside a skill, the visitor chooses a source — one model at one provider — and sets its own limits. The requester never learns which one: it gets results and receipts.

### 8.2 Fallback chains (Proposed)

Each skill has an ordered chain of any number of sources, each with its own limits and settings. If a source is unavailable or busy (a game is using the GPU, the home server is off, a provider's rate limit is hit), the next one is tried. A paid source in the chain runs only if the visitor allowed it in advance, with its own cap. When the chain is exhausted, the requester falls back to its own mode.

### 8.3 The manager (Proposed)

The wallet contains a manager: an AI agent that works only for the visitor and may itself run on a local model. For each skill it proposes which of the visitor's models to use, from the visitor's own experience — test results, ratings, measured speed and cost, all stored only on the visitor's device — and from the requester's recommendations. It explains its choice ("speech-large on this PC: 96% on this site's test, 0.6 s, no cloud bill"). The visitor confirms, or lets the manager confirm on its own within rules the visitor sets (for example, only when the choice costs nothing and scored at least 90% in the visitor's test). The manager has its own limits, like any other user of the wallet.

### 8.4 The limits hierarchy (Proposed)

Limits nest, and a call goes ahead only within all of them; the tightest one wins:

- **per skill** — calls a minute, money a day, input size;
- **per source** — the cap of a paid fallback;
- **per requester** — per session and per day, plus a separate test budget;
- **for the manager** — its own budget and number of suggestions;
- **for the whole wallet** — per day, per week and per month.

## 9. Transports and platforms

- **v0.1 — browser:** an extension exposes the protocol to web pages and talks to a small local companion app for local models and secrets.
- **Next — desktop apps** on Windows, macOS and Linux: the same messages over a local channel, with explicit pairing.
- **Later — agent to agent and network services.**

Desktops and laptops come first: that is where local models, a tray app and long sessions are practical. Phones come later, with their own limits on battery, memory and background work, and with the problem of reaching a model at home from outside it.

## 10. Building on what exists

BYOA is the missing layer, not a replacement:

- calls to models — the OpenAI-compatible API that local servers and aggregators already speak;
- describing agents and skills — in the spirit of A2A agent cards;
- consent and connected accounts — OAuth;
- the reverse direction, where a site exposes tools to the visitor's agent — WebMCP and MCP.

## 11. Adoption path

The first users are people who already run local models and have no way to use them on the web. The first sites are small games and tools that cannot afford to pay for every visitor. The first demonstration is the geography game where this idea started: voice recognised by the visitor's own GPU, the site paying nothing.

## 12. Open questions

- Subscriptions: the terms of each vendor decide where a subscription may be used; the protocol stays neutral and uses what is allowed.
- Matching quality: how a requester states the minimum quality it needs, and how the wallet knows what a local model can do. The property vocabulary and tests (Section 4) are a proposed answer; open are who maintains the standard keys and benchmark names, how to keep requesters from requiring a vendor through `name`, and how much the wallet should trust a provider's own description.
- Tests: the format of a test set; keeping public test sets from being learned by heart; how a requester-run test is bounded and billed.
- Quality signals: the format of a warn or pause message, and how to keep a requester from using "quality" to push visitors towards one provider.
- Agents (level 2): how to express what an agent may tell a requester, and how the visitor can audit it.
- The manager: how much it may decide on its own by default.
- Abuse: requesters that over-ask or disguise their traffic; reputation for requesters that break their declared purpose.
- Identity: how a requester proves who it is, and how a grant is bound to it.
- Phones: reaching a home server or a laptop from a phone.
