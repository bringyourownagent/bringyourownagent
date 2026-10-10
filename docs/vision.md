# BYOA — Vision

Draft, 10 October 2026. Author: [@ultrathinker](https://github.com/ultrathinker).

## 1. The shift

Until now a website served a person who came alone. More and more, a person comes with agents: paid AI subscriptions, API keys, models on their own computer or home server. Each agent has skills — understanding text, recognising speech, reading images and video, generating speech, images and video.

Sites, in turn, increasingly need exactly these skills, and today they must buy them and pay for every visitor. BYOA connects the two: **a site asks for skills, the visitor's agents provide them, the visitor controls the cost.**

BYOA is not an alternative to the AI companies; it connects them. The most capable agents often come with subscriptions from large providers, and they are central to the protocol. Next to them stand API keys, open models and models on the visitor's own hardware. The protocol gives every provider the same way into every site, and every person the same right to choose which agent to bring, or to use several at once.

## 2. Roles

| Role | Who | Responsibility |
|---|---|---|
| **Visitor** | a person | owns the agents, sets budgets and conditions, approves grants |
| **Agent wallet** | software on the visitor's side (first a browser extension with a small local companion app) | holds keys and connections, matches requests to providers, meters, enforces limits, checks purpose, keeps the log |
| **Provider** | a subscription, an API key, an OAuth-connected account, a local model, a home server | performs the skill |
| **Requester** | a website, later also a desktop app or another agent | declares needs and purpose, calls skills within a grant |

## 3. Core messages

The core is four messages. They do not depend on how they are carried.

1. **Manifest** — what the requester needs and why: required and optional skills with parameters (language, streaming, latency, minimum quality), the declared purpose, the data sensitivity, an expected cost per session.
2. **Grant** — what the visitor allowed: skills, budget (per session, per hour, per day), validity period, privacy rule ("local only", "no training use"), the purpose conditions in force.
3. **Call** — one use of a skill under a grant.
4. **Receipt** — what it cost and which limits remain. The requester learns the cost, not the provider or the key.

```
Requester ── Manifest: "speech-to-text (ru, streaming), small text model; purpose: family game" ──▶ Agent wallet
                                                                                                     │ matches providers
Requester ◀── Grant: skills, 20¢ per session, 1 hour, "local only" ────────────────────────────────┤ asks the visitor (budget slider)
Requester ── Call ──▶ Agent wallet ──▶ local model / API key / subscription / home server
          ◀── result + Receipt ──┘ meters, enforces limits, checks purpose, logs
```

## 4. Skills

Skills are named capabilities, not models. A first vocabulary:

- `text.generate`, `text.understand` (classify, extract, answer)
- `audio.transcribe`, `audio.speak`
- `image.understand`, `image.generate`
- `video.understand`, `video.generate`
- `embed`

Each skill carries parameters (languages, streaming, context size, latency, quality tier). A requester may name preferred vendors or models, but the default is to ask for the skill.

## 5. Declared purpose

The idea comes from the author's earlier SSH-guard tool, where a connection is opened with a stated goal and a fast model checks every command against it.

- **The requester declares its purpose** in the manifest, in two layers: hard limits a program can check (only `audio.transcribe`; clips up to 15 seconds; at most N calls a minute; budget) and the purpose in words ("a family game: recognising country names").
- **The visitor may add their own conditions** in plain words: "only for the game; warn me if text is sent". The wallet turns them into rules and shows them for confirmation.
- **Two layers of checking.** Code checks what code can check — reliably and for free. A small model, preferably local, judges only what code cannot (for example, long documents arriving instead of short phrases). The judge treats the content as data, never as instructions, because the content comes from the requester.
- **Reactions** escalate: allow, warn, pause and ask, block. Everything is logged.
- Purpose limitation is an old principle of data protection law; BYOA makes it machine-checked.

## 6. Privacy

The requester marks the sensitivity of the data it sends. The visitor sets rules per site: "local models only", "no providers that keep data", "only these vendors". The wallet routes accordingly or refuses.

## 7. Local models

A local model or a home server is just another provider, and an important one:

- the data does not leave the visitor's machine or home;
- there is no per-request cost;
- no vendor's permission is needed.

A laptop with a 16 GB GPU runs speech recognition and a small language model with room to spare. Sites must never talk to a local model directly: a model server open to any web page lets any page use the visitor's GPU. The wallet stands in between.

## 8. Budgets and cost

The visitor sets a budget per site and per time window, with a quick slider when a site first asks. After a session the visitor sees the actual cost ("this game took 6 cents") and can raise or lower the limit. Local models cost nothing in money; the wallet can still limit their use by time or load.

## 9. Transports

- **v0.1 — browser:** an extension exposes the protocol to web pages and talks to a small local companion app for local models and secrets.
- **Next — desktop apps** on Windows, macOS and Linux: the same messages over a local channel, with explicit pairing.
- **Later — agent to agent and network services.**

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
- Matching quality: how a requester states the minimum quality it needs, and how the wallet knows what a local model can do.
- Abuse: requesters that over-ask or disguise their traffic; reputation for requesters that break their declared purpose.
- Identity: how a requester proves who it is, and how a grant is bound to it.
- Phones: reaching a home server or a laptop from a phone.
