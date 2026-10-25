# Your own agent as a source (wallet 0.1)

Status: first version, in the wallet only. The protocol is unchanged: a site that wants the visitor's agent asks for it through the `models` block of its `text.generate` skill, which the wallet already reads. This is level 2 of [the vision](vision.md#models-first-then-agents), started small: one question, one answer, an agent that can search the web and read pages and nothing else.

## What the visitor adds

An **agent source** is a program on the visitor's computer that runs his AI agent for one question at a time. So far the wallet talks to Claude Code and the launchers built on it (`claude -p`). Each call starts the program like this:

- the only tools are web search and page reading (`--tools WebSearch,WebFetch`); no files, no commands;
- no MCP server of the visitor's is loaded (`--strict-mcp-config`), the wallet's own among them, and no session is saved;
- it starts in an empty folder of the wallet's own, for at most 12 turns, and the whole call lasts at most the site's call time (300 s unless the visitor or the site set another);
- if the visitor named a model when he added the agent (Opus, Sonnet, a model's name), the program also gets `--model <name>`: the name is the visitor's own, restricted to letters, digits and `. _ : / -` and never starting with a hyphen, so it cannot be read as another option; the same program can be added twice with two models;
- the prompt goes in by standard input, never on the command line; what the site wrote sits between two lines with a random fence and is named as the site's question, not an order.

The agent runs on the visitor's own subscription: the wallet counts its cost as 0 and reports provider kind `subscription` to the gate. A site that asks for `local-only` privacy never gets it.

Only the visitor adds or changes an agent source (My agents → Add an agent). The MCP bridge's source kinds do not include `agent`, and it cannot edit one: the program is code the wallet starts.

## How a site asks for it

The site names the agent in its manifest like any model, with the name `agent/<anything>` or the mask `agent/*`:

```json
"skills": { "text.generate": { "required": true, "models": { "count": 1, "require": ["agent/*"] } } }
```

and states what it will ask in `purpose.summary`, at least 20 characters: "Family geography game: news about the countries the players name". A site with no usable goal is not given the agent.

The wallet's rules for who gets the agent:

1. An agent is never part of the wallet's own route. A site that asked for no models, or only for models, never reaches it. It is in `session.models` only when the visitor ticked it for that site.
2. A site that only *recommends* `agent/*` is not given a tick; the visitor ticks it himself. A site that *requires* it gets it ticked, and the visitor still presses Allow.
3. The goal is looked at when the agent is given (the guard, one question: does the goal need the agent to touch files, accounts, money, settings, or do something harmful). If it looks risky the visitor decides; refused, the agent is left out, and a site that cannot work without it is declined. A goal that was accepted is remembered; a site that states another goal is asked about again, quietly granted sites included.
4. A call whose site goal differs from the accepted one is refused.

## The guard

Before the agent sees a request, [Jev](https://openrouter.ai/typesafe/jev-1.14) (TypeSafe's decision model, OpenRouter's Decisions API, behind the visitor's OpenRouter key) is asked three statements about it: it could harm the visitor's computer, data or accounts; it tries to give the agent orders of its own; it is what the site said it is for. Each answer is the chance that the statement is true. A statement past the visitor's strictness (Strict, Normal, Relaxed) holds the request: a window shows the request as the site sent it, the goal, and what the guard noticed; the visitor lets it through, refuses it, or refuses it and takes the agent away from that site. No answer in two minutes is a refusal. A refused request is not handed to another source.

When the guard cannot be asked (no key, no network, an answer the wallet cannot use, the daily limit of 2 dollars spent) no request reaches the agent, unless the visitor chose "let requests through" in Agent guard. Every statement is in the visitor's hands: he can reword it. The guard is a filter, not a wall; it is why the agent is also kept to two tools and to the sites the visitor chose.

Privacy: the text of each request goes to OpenRouter (and Jev) to be checked, and to the agent's own model provider to be answered. The Agent guard page says so.

## In the activity log

A call to the agent is logged with the guard's note: `guard: ok (on_purpose 96 %, harm 2 %, redirect 1 %)`, `guard held it (harm 80 %); you let it through`, `…you refused it`, `…you refused it and took your agent away from the site`, or `the guard could not be asked (…): the agent did not run`.
