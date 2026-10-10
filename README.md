# Bring Your Own Agent (BYOA)

**An open protocol that lets websites and apps use the visitor's own AI agents — their subscriptions, API keys or local models — within limits the visitor sets.**

> Status: early draft. The idea was first published on **10 October 2026**. Specification v0.1 is in progress.

## The problem

Every site that wants voice input, smart search or a conversational helper has to pay for AI models itself. So it either charges visitors and builds billing, drops the feature, or makes people type exact keywords.

Meanwhile the visitors already pay for AI: ChatGPT, Claude or Gemini subscriptions, API keys, OpenRouter credits, or a model running on their own GPU or home server. None of that can be used on someone else's site.

## The idea

People no longer browse alone: everyone arrives with their own set of agents. BYOA lets a site say what it needs, and the visitor's agents do the work.

1. The site declares the **skills** it needs (for example, speech-to-text in Russian and light text understanding) and its **purpose** ("a family game: short voice clips with country names").
2. The visitor's **agent wallet** matches the request with the providers the visitor has: a subscription, an API key, a local model, a home server.
3. The visitor approves a **grant** with a budget: "20 cents for this site", "50 cents per hour", "local models only".
4. The site calls the skills through the wallet. It never sees keys, and it does not need to know which provider is used. The wallet meters the spend, enforces the limits, checks the traffic against the declared purpose and keeps a log.

## Principles

- **Skills, not vendors.** A site asks for a capability, not for a particular company's model. It may state preferences or a minimum quality.
- **Every kind of provider is first-class:** subscriptions where the vendor allows it, API keys, accounts connected with OAuth, and models on the visitor's own computer or home server.
- **The visitor pays and decides.** Budgets per site and per time window, the actual cost is visible, a grant can be revoked at any moment.
- **Keys never leave the wallet.**
- **Declared purpose.** A site states what it will use the skills for; the visitor can add their own conditions in plain words. The wallet checks every request against both — hard limits in code, softer checks by a small (preferably local) model — and warns, pauses or blocks.
- **Privacy is part of the protocol.** A site marks how sensitive its data is; the visitor can require "local models only" for a site.
- **Graceful fallback.** No wallet — the site falls back to its own mode (paid, reduced or text-only).
- **Transport-agnostic core.** The browser comes first; the same messages are meant to serve desktop apps on Windows, macOS and Linux, and agent-to-agent interaction later.
- **Build on what exists:** OpenAI-compatible inference APIs, OAuth, MCP and WebMCP, A2A agent cards.
- **Open and neutral.** No vendor owns the protocol.
- **For everyone, against no one.** BYOA does not compete with AI providers — it connects them. The most capable agents often come with subscriptions from large providers, and they are central to BYOA; next to them stand API keys, open models and models on the visitor's own hardware. Every provider gets the same way into every site, and every person the same right to choose which agent to bring, or to use several at once.

## Where it came from

A family geography game: players name countries out loud, the map fills in. It needs two models — speech recognition and a small text model — and every spoken phrase costs the site owner money. The visitors' own laptops could do both for free, and many of them already pay for AI subscriptions. There was no standard way to use either.

## In this repository

- [docs/vision.md](docs/vision.md) — the concept in more detail
- [docs/prior-art.md](docs/prior-art.md) — related work and how BYOA differs
- [spec/](spec/) — the protocol specification (v0.1 in progress)

## Author

Idea and protocol: [@ultrathinker](https://github.com/ultrathinker).
Website: <https://bringyourownagent.org>

## License

Documents and the specification: [CC BY 4.0](LICENSE-DOCS). Code: [Apache License 2.0](LICENSE).
