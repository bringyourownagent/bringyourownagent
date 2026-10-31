# BYOA specification 0.1 (draft)

> **Draft for discussion.** Nothing here is a standard yet, and any rule may change before 0.1 is final. Comments and objections are welcome as GitHub issues. The key words MUST, SHOULD and MAY are used in their usual sense (RFC 2119), but only where this document says "conforming".

BYOA ("Bring Your Own Agent") lets a website or app use the visitor's own AI, within limits the visitor sets, without the site paying for it and without the site learning which AI answered. This document is the overview and the obligations of the two sides. The details live in three companion documents, which are **normative** for what they cover:

| Document | What it fixes |
|---|---|
| [rules-0.1-draft.md](rules-0.1-draft.md) | the manifest, the grant, the call and the **gate**: every rule a program can check, with stable codes |
| [CONTRACT.md](CONTRACT.md) | the browser binding: `window.byoa`, the skills `audio.transcribe`, `text.generate` and `image.generate` (the last from wallet 0.37), the extension and native host |
| [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) | the binding without an extension: the `bringyourownagent://` link and the wallet's channel to the site's server |

The ideas behind them, and what is proposed for later, are in [../docs/vision.md](../docs/vision.md). Where this document and a companion disagree, the companion wins.

## 1. Roles

| Role | Who |
|---|---|
| **Visitor** | the person. Owns the models and keys, decides what each site may do, and sets the limits. |
| **Agent wallet** | software on the visitor's side. Holds keys and connections, asks the visitor, routes each call to a model, meters it, enforces the limits, and keeps a log. |
| **Provider** | what actually performs a skill: a model on the visitor's computer or home server, a model behind the visitor's own API key, later a connected account or subscription where its terms allow. |
| **Requester** | the website (later also a desktop app or another agent) that asks. |

A requester never talks to a provider and never sees a key. It talks to the wallet, and the wallet decides.

## 2. The four messages

1. **Manifest** (requester to wallet): what the requester needs and why. The skills it asks for (each with parameters such as languages, and whether it is required), the purpose in words with hard limits a program can check (for example `maxClipSeconds`, `maxCallsPerMinute`), the data's privacy class, and an expected cost.
2. **Grant** (kept by the wallet): what the visitor allowed. The skills, a budget, a validity period, and the conditions that apply. A grant belongs to one **origin**; it is never transferable.
3. **Call**: one use of one skill under a grant: the skill's input and parameters.
4. **Receipt** (wallet to requester): the cost of the call and the kind of provider (`local` or `api-key`), nothing more.

The wallet MUST check every call against the manifest's limits and the grant before any provider runs; the rules for that check are the gate in [rules-0.1-draft.md](rules-0.1-draft.md).

## 3. How a visitor says yes

This is the part that protects the visitor, and a conforming wallet MUST keep to it.

1. **Only after a click.** A request made without a user click on the requester's page MUST NOT show a prompt. The wallet MAY mark it quietly (a badge) and MUST answer "AI unavailable".
2. **A short notice, then a short consent.** First a small notice that a site wants to use the visitor's AI (Show, Decline or Block). Then one window: the site's verified origin, its purpose in its own words, one row per skill with the model that would serve it, the limit, and Allow or Decline. A skill the visitor cannot serve says so and what would fix it; an optional skill can be left off.
3. **The same answer for every no.** Declined, blocked, no wallet installed and "no fitting model" MUST look the same to the requester: `null` from `request`, or no connection at all. The requester cannot learn which, so asking again gains it nothing. In the direct-link binding the wallet MUST NOT make any network request before the visitor's Allow, so that even the existence of a wallet is not revealed.
4. **Deciding on my own.** The visitor MAY tick "next time, decide on my own within these limits". A later request that fits the grant is then answered without a prompt; the wallet MUST ask again for anything that does not fit, and for every direct link (CONTRACT-DIRECT.md §3).
5. **Only the difference.** When an origin asks for more than it was granted (a new skill, a new language, a higher expected cost), the consent window MUST show only what changed.
6. **Paid is asked first.** A call that would cost money MUST be allowed by a visitor's rule or confirmed by the visitor; the default is to ask.
7. **Revoking and pausing.** The visitor can revoke or block a site at any time; the wallet MUST then close its connections and refuse its calls. A wallet MAY pause a site that has not been used for a while, and MUST ask again (with a plain "resume?") before serving it.

## 4. Skills

A skill is a named kind of work with a fixed shape of input and output. The vocabulary of 0.1 is `text.generate`, `text.understand`, `text.embed`, `audio.transcribe`, `audio.speak`, `image.understand`, `image.generate`, `video.understand` and `video.generate`. A skill is always called a skill: the word **role** stays with the four Roles of section 1 and with a message's role (`system`, `user`, `assistant`). Three of them have a defined shape, in [CONTRACT.md](CONTRACT.md) section 3:

- `audio.transcribe`: a WAV clip (up to 15 seconds) in, text out.
- `text.generate`: chat messages in, a short text answer out; with `choices` the answer is exactly one of the given strings. From wallet 0.37 a message may also carry picture parts, for a source that reads pictures (CONTRACT.md section 3.7).
- `image.generate`: a prompt in, pictures out as parts (CONTRACT.md section 3.7). Wallet 0.37: a wallet before it serves none of it, and a site that requires the skill gets no Allow — the safe failure of a skill a wallet cannot serve.

The other names of the vocabulary have no shape yet; a wallet serves none of them.

Rules that hold for every skill:

- **The answer is user input.** The requester MUST NOT treat a wallet's output as trusted: validate it, limit its length, escape it when it is displayed.
- **The wallet measures; the page does not say.** The wallet takes sizes and durations from the data itself, never from a field the requester filled in.
- **Failure is one shape.** A call that cannot be served is `{ ok: false }`: blocked by a limit, no model answered, the grant ended, a paid fallback was refused. The requester MUST then use its own path; it is told no more.
- **The model is not revealed.** Which provider or model answered is the wallet's business.
- **A skill is filled by fit** (wallet 0.36). A requester may state conditions on the properties of the sources it wants (`needs`) and the kind of source (`model`, `agent`, `any`); the wallet offers only what fits the skill's own built-in rule first, and answers with verdicts, never values, in `can` (CONTRACT.md sections 3.3, 3.5 and 3.6).

Security notes that the new keys add (the reasons are in the contract sections named):

- Pictures are not text, and a wallet's guard reads text only. A requester that sends pictures asks the visitor to trust a channel the wallet cannot check. Pictures go only to models; never to an agent (CONTRACT.md sections 3.6 and 3.7).
- `can` returns verdicts, not values: one boolean per condition the requester asked about, at most four keys. A value would identify a model more than a name does (CONTRACT.md section 3.3).
- A changed `needs` is the requester asking for more; the visitor is asked again, never decided for (CONTRACT.md section 3.5).
- The receipt stays cost and kind. What an agent lacked is said in `result.lacks`, not in the receipt (CONTRACT.md section 3.6).
- An agent never serves a skill whose output is not text, and a requester never widens an agent's profile (CONTRACT.md section 3.6).

## 5. Purpose and limits

A manifest declares its purpose twice: in words, and as hard limits. The hard limits are checked by code on every call, for free and reliably. A small model, preferably a local one, MAY judge what code cannot (for example long documents arriving where short phrases were declared); it treats the content as data, never as instructions. Reactions, from mildest: allow, warn, ask, block. The first draft of the hard limits (`maxClipSeconds`, `maxCallsPerMinute`, `maxTextChars`) and their codes are in [rules-0.1-draft.md](rules-0.1-draft.md). The visitor MAY add conditions of their own; they only ever make the result stricter.

## 6. Privacy classes

A manifest names the class of the data it sends: `public`, `personal`, `personal-voice`, `sensitive` or `local-only`. `local-only` is the one class that changes the gate in this draft: only providers that run on the visitor's own computer or home server may serve the call, and the wallet MUST verify where a call really runs. The other classes inform routing choices the wallet makes for the visitor (for example, "no provider that keeps data") and are not specified further yet.

## 7. Money

- **Budgets** are in the grant (this draft: per session; per day and per hour are planned). The wallet MUST stop serving a grant whose budget is used up.
- **Estimates and receipts.** The wallet estimates a call's cost before it runs and reports the real cost afterwards. A local model costs nothing.
- **The requester sees cost, not source.** A receipt carries the cost and `local` or `api-key`, never a provider's name or a key.
- **A site that pays nothing.** A requester that expects only local models can say so with an expected cost of `0.00 USD`; a wallet MUST NOT use a paid provider for it without asking the visitor.

## 8. Bindings

A binding says how the four messages travel. Two exist in 0.1, and both end in the same wallet logic: the same notice and consent, grants, limits, routing, fallbacks, "ask before paid" and log. A site allowed through one is allowed through the other, because grants belong to the origin.

| | Browser extension | Direct link |
|---|---|---|
| Spec | [CONTRACT.md](CONTRACT.md) | [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) |
| The visitor needs | an extension | nothing in the browser; the wallet registers a URL scheme |
| The page starts it with | `window.byoa.request(manifest)` from a click | a click on a `bringyourownagent://connect?...` link |
| The manifest comes from | the page | the link itself |
| Calls come from | the page, through the extension | the site's server, over the wallet's WebSocket |
| The site's identity | the browser's own origin | TLS: the origin of the `wss://` endpoint |

Planned: desktop applications (the same messages over a local channel with explicit pairing), then agent to agent.

## 9. What a conforming wallet does

1. Applies sections 3 and 4, and checks every call with the gate before any provider runs.
2. Takes a requester's identity from the transport (the browser or TLS), never from the request.
3. Keeps keys and secrets itself, encrypted for the visitor's account; never gives them to a requester or logs them.
4. Does not log the content of calls (audio or text) in its activity log.
5. Treats a link or request from an unknown or malformed source as nothing: no prompt, no network.
6. Never reaches a private, loopback or link-local address on a requester's behalf in the direct-link binding.
7. Applies a call's time and size caps itself, and caps what it returns.

## 10. What a conforming requester does

1. Asks only after a click, and asks for what it needs: required skills, an honest purpose, small limits, an honest expected cost.
2. Always has its own path for "AI unavailable" and does not tell the visitor which kind of "no" it was.
3. Treats every output as user input.
4. In the direct-link binding: issues single-use, short-lived, rate-limited codes; sends nothing before a valid `hello`; echoes the manifest in `welcome`; keeps frames small; and treats a silent or failing wallet as gone.
5. Never asks a visitor for a key.

## 11. Security considerations

- **A program running as the visitor is fully trusted.** It can read the wallet's settings and key store, change them, and talk to the wallet as the visitor's own tools do. The wallet cannot defend against it. For a polite agent the wallet keeps its promises: no tool returns a key, Full access gates the tools that see or change sites, limits and built-in sources, no tool allows a site, approves a paid call or approves another agent, and a new agent needs the visitor's Allow. Against web pages, other accounts, a stranger's site and accidents, the wallet protects the visitor, as the items below say.
- **A hostile page or link** can only cause a prompt, and only after a click. It cannot make the wallet connect to a local or private address, learn whether a wallet exists, or obtain a grant without the visitor's Allow. With the Chrome extension installed, a page can detect the wallet, because the extension defines `window.byoa` on every https page.
- **A hostile requester** is limited by the visitor's grant: skills, sizes, call rates, money. A compromised requester is no more dangerous than a malicious site the visitor chose to allow, and the visitor can revoke it.
- **A hostile wallet output** (a prompt-injected model) is user input to the requester; the requester's own validation is the defence. Constraining the answer with `choices` reduces what a model can say.
- **Registered URL schemes** are claimed by the last application that registers them on some systems, so a link carries only short-lived, single-use codes, and the scheme name `bringyourownagent` is long and specific on purpose.
- **Secrets**: API keys can be written to the wallet and used. A requester, a page and a managing agent cannot read them back through the wallet. A program running as the visitor can read the key store (see the first item of this section).
- **Logs** hold events and costs, not the audio or text of calls.
- **Local-only data** must stay local: the wallet verifies where a call runs.

## 12. Open issues

Listed with their reasons in [../docs/vision.md](../docs/vision.md) (section 12) and in the wallet's design notes ([../docs/wallet-0.1.md](../docs/wallet-0.1.md), section 9). The main ones: daily and hourly budgets in the grant; receipt kinds; what counts as "asking for more"; skill parameters beyond languages; a "preparing" state in the page API so a site can show its own progress while models load; identity of requesters beyond the origin; and the format of quality signals.
