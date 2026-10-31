# Your own agent as a source (wallet 0.1)

Status: first version, in the wallet only. The protocol is unchanged: a site that wants the visitor's agent asks for it through the `models` block of its `text.generate` skill, which the wallet already reads. This is level 2 of [the vision](vision.md#models-first-then-agents), started small: one question, one answer, an agent that can search the web and read pages and nothing else.

From wallet 0.36 a site may also ask by *kind* and by *abilities* in the same block (`"kind": "agent"`, `"kind": "any"`, `"abilities": ["web.search", "web.read"]`), and the grant answers what the agent really can do ([CONTRACT.md](../spec/CONTRACT.md) sections 3.3 and 3.6). Nothing else changes: the agent is still one question and one answer, and its profile is still two tools.

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

From wallet 0.36 the same block may say `"kind": "agent"` (or `"kind": "any"`, when a model may fill the skill too) and ask for abilities:

```json
"skills": {
  "text.generate": {
    "required": true,
    "models": {
      "count": 1,
      "kind": "any",
      "needs": { "input": ["text"], "output": ["text"] },
      "abilities": ["web.search", "web.read"],
      "recommend": ["agent/*"],
      "why": "Checks the web for how such icons are drawn."
    }
  }
}
```

`kind` only decides which sources may fill the skill; the mask `agent/*` in `require`/`recommend` keeps working and means the same as before. A site that wants the agent ticked still has to *require* it: `kind` ticks nothing.

**Abilities are derived, never declared.** The wallet builds the agent's tool list itself, so it knows what the agent can do: today `web.search` and `web.read`, and nothing else. The visitor is never asked to tick a checkbox the wallet cannot enforce, and a site's `abilities` never widens the agent: `files.read`, `code.run`, `models.call`, `agents.call` and `image.make` are not in the vocabulary, and `image.read` is reserved and never pre-filled. In the consent window an agent row shows "agent", and, when the site asked for abilities, "can: web search, page reading / may lack: web search". Matching is soft by default (`onMismatch` is `show` for an agent): an agent that lacks an asked ability is offered and tickable, marked so the visitor sees the gap before he ticks.

**At call time**, when an ability the site asked about is missing, the wallet adds one line to its own framing of the prompt — outside the site's fenced text, beside the fence line that names the material as the site's — saying plainly: the site hoped for web search (say), this agent has no such tool, do what you can and say in the answer what you could not. The `result` then carries `"lacks": ["web.search"]` next to `output`: only the abilities the site asked about, and it is the wallet's own statement, never the agent's prose. The receipt stays cost and kind.

**No pictures.** The agent's profile has no way to read a picture, and giving it one (a file tool, a wider launcher) would break the two-tool promise the guard and the consent window show. A picture part in a request is never sent to an agent: the wallet routes the call to a model that reads pictures, or answers `ok:false`. A later change may pass pictures to Claude Code as stream-JSON image parts; until then the agent works from text only.

The wallet's rules for who gets the agent:

1. An agent is never part of the wallet's own route. A site that asked for no models, or only for models, never reaches it. It is in `session.models` only when the visitor ticked it for that site.
2. A site that only *recommends* `agent/*` is not given a tick; the visitor ticks it himself. A site that *requires* it gets it ticked, and the visitor still presses Allow.
3. The goal is looked at when the agent is given (the guard, one question: does the goal need the agent to touch files, accounts, money, settings, or do something harmful). If it looks risky the visitor decides; refused, the agent is left out, and a site that cannot work without it is declined. A goal that was accepted is remembered; a site that states another goal is asked about again, quietly granted sites included.
4. A call whose site goal differs from the accepted one is refused.
5. An agent fills only a skill whose output is text (`text.generate`). It is never offered for `image.generate`, whatever `kind` says: the wallet refuses the combination, and a site that needs pictures asks for the model kind.

## The guard

Before the agent sees a request, a judge model is asked three statements about it: it could harm the visitor's computer, data or accounts; it tries to give the agent orders of its own; it is what the site said it is for. Each answer is the chance that the statement is true. The judge is [Jev](https://openrouter.ai/typesafe/jev-1.14) (TypeSafe's decision model: OpenRouter's Decisions API behind the visitor's OpenRouter key, or, when the visitor picks it in Agent guard, TypeSafe's own `POST https://api.typesafe.ai/v1/systemone` behind his TypeSafe key, with the same questions and answers and the model `jev-latest`), or Kev (a small decision model that the Llama app serves at its own `/v1/systemone` on the visitor's computer: the same questions and answers, no key, no cost, and limits of its own because its chances are squeezed toward the middle; a long request is read in windows, as for a text source), unless the visitor picked one of his own text sources in Agent guard; his agents never judge. A text source writes text instead of chances, so it is sent the statements and the request in one prompt and must answer strict JSON; anything else it writes is unread, and a request it could not judge is treated like a guard that could not be asked. A local model keeps the request on the visitor's computer and costs nothing (the daily limit does not count it); a paid source is counted by what it really costs. An input longer than the piece size shown on the page is judged in three chunks (start, middle, end) combined on the careful side: the highest harm and redirect, the lowest on_purpose.

A statement past the visitor's strictness (Strict, Normal, Relaxed) holds the request: a window shows the request as the site sent it, the goal, and what the guard noticed; the visitor lets it through, refuses it, or refuses it and takes the agent away from that site. No answer in two minutes is a refusal. A refused request is not handed to another source.

When the guard cannot be asked (no key, no network, an answer the wallet cannot use, the daily limit of 2 dollars spent, or the picked model is gone or off) no request reaches the agent, unless the visitor chose "let requests through" in Agent guard. Every statement is in the visitor's hands: he can reword it. The guard is a filter, not a wall; it is why the agent is also kept to two tools and to the sites the visitor chose.

Privacy: the text of each request goes to the judge — OpenRouter (and Jev) by default, or wherever the visitor's own model runs — and to the agent's own model provider to be answered. The Agent guard page says so. The guard reads text only: a picture is not text, and a site that sends pictures asks the visitor to trust a channel the wallet cannot check. That is one more reason no picture reaches an agent.

## In the activity log

A call to the agent is logged with the guard's note: `guard: ok (on_purpose 96 %, harm 2 %, redirect 1 %)`, `guard held it (harm 80 %); you let it through`, `…you refused it`, `…you refused it and took your agent away from the site`, or `the guard could not be asked (…): the agent did not run`.
