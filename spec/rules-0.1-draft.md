# BYOA first rules (0.1-draft)

> **DRAFT FOR DISCUSSION.** This is a first proposal, not a standard. Every code, field name and decision below may change before specification v0.1. Comments and objections are welcome as GitHub issues.

This document lists the first rules of BYOA that a program can check, in plain English. Each rule has a stable code. The reference implementations in [`../sdk/`](../sdk/) (Python and Node.js, version 0.0.1) implement exactly these rules, and the shared test vectors in [`../sdk/test-vectors/`](../sdk/test-vectors/) pin down their behaviour.

There are two groups of rules:

1. **Manifest rules** — is the requester's manifest well formed? (Section 3.)
2. **Call rules** — the "hard limits checked by code": may this one call go ahead under the visitor's grant? (Section 5.)

The softer check of the declared purpose by a small model is out of scope here; Section 6 describes the hook where it plugs in.

## 1. Conventions

- **Documents are JSON.** The four documents used here are the **manifest** (written by the requester), the **grant** (what the visitor allowed, kept by the agent wallet), the **call** (one use of a skill, as the wallet sees it after choosing a provider) and the **state** (what the wallet remembers within a session).
- **Paths** in errors and reasons are JSON Pointers ([RFC 6901](https://www.rfc-editor.org/rfc/rfc6901)), relative to the document they point into: `/skills/audio.transcribe/required`. The empty string means the whole document.
- **Money** is a string `"<amount> <currency>"`: a non-negative decimal with at most 12 digits before the point and at most 6 after it, one space, and a three-letter upper-case currency code in the form of ISO 4217. Valid: `"0.06 USD"`, `"0 USD"`, `"12 EUR"`, `"0.000001 USD"`. Invalid: `"$0.06"`, `"0.06usd"`, `".5 USD"`, `"01 USD"`, `"-1 USD"`, `"0.1234567 USD"`. The bounds keep the arithmetic exact in every language (whole micro-units fit in a 64-bit integer); implementations must never use binary floating point for money. The currency code is checked for form only, not against the ISO list. The gate never converts currencies, not even zero: a free call is written as zero in the budget's currency (`"0 USD"` under a USD budget); `"0 EUR"` under a USD budget is a currency mismatch.
- **Timestamps** use this subset of [RFC 3339](https://www.rfc-editor.org/rfc/rfc3339) date-times: `YYYY-MM-DDTHH:MM:SS`, an optional fraction of one or more digits after a point, then `Z` or an offset `+HH:MM` / `-HH:MM`. The offset is required; `T` and `Z` may also be lower case; the year has four digits (0000-9999); the date must exist (`2026-02-29` does not); hours 00-23, minutes and seconds 00-59, so leap seconds (`:60`) are not accepted; offsets up to 23:59. Examples: `"2026-10-10T13:00:00Z"`, `"2026-10-10T15:00:00.250+02:00"`. Timestamps are compared as instants at millisecond precision: fraction digits after the third are cut off, never rounded (`.0009` is `.000`).
- **Lengths** of strings are counted in Unicode code points, not in bytes or UTF-16 units.
- **Numbers** are JSON numbers, finite, from 0 to 9007199254740991 (2^53 - 1, the largest integer every JSON implementation reads exactly). Anything larger, such as `9007199254740992`, `1e300`, `1e400` or a 400-digit integer, is invalid, so every implementation compares the same numbers the same way. A "count" (calls, characters) must also be a whole number; `20.0` counts as whole. `true` and `"20"` are not numbers. These bounds apply to every limit and every measurement.
- **`null` means absent.** A field whose value is `null` is treated exactly as if it were missing, everywhere.
- **Order of results.** Problems are reported in the order of the rule tables below. Where a rule looks at several keys of one object, the keys are visited in Unicode code point order, so every implementation reports the same list in the same order.

## 2. Vocabulary used by the rules

| Name | Values in 0.1-draft |
|---|---|
| Protocol version (`byoa`) | `"0.1-draft"` |
| Skill vocabulary | `text.generate`, `text.understand`, `text.embed`, `audio.transcribe`, `audio.speak`, `image.understand`, `image.generate`, `video.understand`, `video.generate` |
| Privacy classes | `public`, `personal`, `personal-voice`, `sensitive`, `local-only` |
| Limits | `maxClipSeconds` (number), `maxCallsPerMinute` (count), `maxTextChars` (count) |
| Expected cost periods | `perCall`, `perSession`, `perHour` |
| Provider kinds | `subscription`, `api-key`, `connected-account`, `local-model`, `home-server` |
| Decisions, mildest first | `allowed`, `warn`, `ask`, `blocked` |

**Privacy classes**, meaning of each:

- `public` — the data is not personal (game moves, public text).
- `personal` — personal data, for example what the visitor types.
- `personal-voice` — personal data that includes the visitor's voice.
- `sensitive` — special kinds of personal data: health, finance, data about children and the like.
- `local-only` — the requester itself asks that the data never leave the visitor's computer or home: only `local-model` and `home-server` providers may run it. The wallet must verify where a call actually runs: a home server that forwards the request to a cloud API is not local.

In this draft only `local-only` changes what the gate does (rule `not-local`). The other classes inform routing in the wallet (for example, "no providers that keep data"), which is not specified yet.

**Local providers** are `local-model` and `home-server`; the other three kinds are cloud providers.

## 3. Manifest rules

`validateManifest(manifest)` returns `{ ok, errors, warnings }`. Each item is `{ code, path, message }`. `ok` is true when there are no errors; warnings never make a manifest invalid. The message is for people and may change; programs should use the code.

The manifest shown on the BYOA site is valid and produces no warnings:

```json
{
  "byoa": "0.1-draft",
  "purpose": {
    "summary": "Family geography game: recognise country names",
    "limits": { "maxClipSeconds": 15, "maxCallsPerMinute": 20 }
  },
  "skills": {
    "audio.transcribe": { "languages": ["ru", "sk"], "required": true },
    "text.understand":  { "size": "small", "required": false }
  },
  "privacy": "personal-voice",
  "expectedCost": { "perSession": "0.06 USD" }
}
```

| # | Code | Severity | Rule | Why |
|---|---|---|---|---|
| M1 | `manifest-not-object` | error | The manifest must be a JSON object. Nothing else is checked otherwise. | — |
| M2 | `version-missing` | error | `byoa` must be present. | A wallet must know which rules it is reading. |
| M3 | `version-unsupported` | error | `byoa` must be a supported version string (`"0.1-draft"`). | Rules change between drafts; guessing is unsafe. |
| M4 | `purpose-missing` | error | `purpose` must be present and be an object. | Declared purpose is the core of the protocol. |
| M5 | `summary-missing` | error | `purpose.summary` must be a string with at least one non-whitespace character. | The visitor sees it before granting anything. |
| M6 | `summary-too-long` | error | `purpose.summary` must be at most 200 characters. | It is one short sentence for a consent screen, not a policy. Details belong elsewhere. |
| M7 | `limit-invalid` | error | `purpose.limits`, if present, must be an object; each known limit must be a number from 0 to 2^53 - 1 (Section 1), and the counts (`maxCallsPerMinute`, `maxTextChars`) whole numbers. | These are the hard limits the gate enforces. |
| M8 | `limit-unknown` | warning | A limit name that is not known. It is ignored, so a newer manifest still works with an older wallet. | Forward compatibility; also catches typos. |
| M9 | `field-unknown` | warning | `purpose` has a field other than `summary` and `limits`. | Forward compatibility. |
| M10 | `skills-missing` | error | `skills` must be an object with at least one skill. | A manifest that asks for nothing has no use. |
| M11 | `skill-id-invalid` | error | A skill id must have the form `domain.action`: two lower-case parts joined by a dot, each part starting with a letter and made of letters, digits and single inner hyphens, at most 64 characters. | One predictable shape for matching skills to providers. |
| M12 | `skill-unknown` | warning | A well-formed skill id that is not in the 0.1-draft vocabulary. | New skills will appear; typos such as `audio.transcibe` should still be visible. |
| M13 | `skill-invalid` | error | Each skill's value must be an object of parameters. | — |
| M14 | `skill-required-invalid` | error | Each skill must state `"required": true` or `"required": false`. | The visitor must see which skills the site cannot work without; no hidden default. |
| M15 | `no-required-skill` | warning | No skill has `"required": true`. | Legal (the site works without any of them), but usually a mistake. |
| M16 | `privacy-missing` | error | `privacy` must be present. | Privacy is part of the protocol; the wallet routes by it. |
| M17 | `privacy-unknown` | error | `privacy` must be one of the privacy classes. An unknown class is an error, not a warning. | A wallet must not guess how sensitive the data is. |
| M18 | `cost-missing` | warning | `expectedCost` is absent, or has none of the known periods. | Visitors decide faster when they see an expected cost. |
| M19 | `cost-invalid` | error | `expectedCost`, if present, must be an object; each known period must be a money string. | — |
| M20 | `field-unknown` | warning | `expectedCost` has a field other than the known periods. | Forward compatibility. |
| M21 | `field-unknown` | warning | The manifest has a top-level field other than `byoa`, `purpose`, `skills`, `privacy`, `expectedCost`. | Forward compatibility: older wallets ignore newer fields. |

Skill parameters other than `required` (such as `languages` or `size`) are skill-specific and are not checked in this draft.

## 4. Grant, call and state

These shapes exist so that the gate can be tested. They are a starting point for the Grant and Call messages of the specification, not their final form.

**Grant** — what the visitor allowed for one requester.

```json
{
  "byoa": "0.1-draft",
  "skills": ["audio.transcribe", "text.understand"],
  "budget": { "perSession": "0.20 USD" },
  "expiresAt": "2026-10-10T13:00:00Z",
  "limits": { "maxClipSeconds": 10 },
  "localOnly": false,
  "warnIf": { "sendsText": true, "callCostAbove": "0.02 USD", "spendAbove": "0.15 USD" }
}
```

| Field | Required | Meaning |
|---|---|---|
| `byoa` | yes | protocol version, `"0.1-draft"` |
| `skills` | yes | array of granted skill ids (may be empty) |
| `budget.perSession` | yes | the most this requester may spend in one session |
| `expiresAt` | yes | when the grant ends. Every grant has an end; "always" is written as a far date, so the visitor sees it |
| `limits` | no | the visitor's own limits, same names as manifest limits. The stricter of the two always applies; a grant cannot loosen a manifest limit |
| `localOnly` | no | `true`: only local providers may run calls |
| `warnIf.sendsText` | no | `true`: warn when a call sends text (the visitor's condition "warn me if it sends text") |
| `warnIf.callCostAbove` | no | warn when one call costs more than this |
| `warnIf.spendAbove` | no | warn when the session spend would go above this |

**Grants fail closed.** A grant with an unknown field anywhere (top level, `budget`, `limits`, `warnIf`) is invalid, and every call under it is blocked. A limit the visitor set must never be silently ignored because this version does not understand it. All money in a grant must be in the budget's currency.

**Call** — one use of a skill, after the wallet has chosen a provider and measured the input.

```json
{
  "skill": "audio.transcribe",
  "at": "2026-10-10T12:00:00Z",
  "provider": "api-key",
  "estimatedCost": "0.003 USD",
  "input": { "clipSeconds": 9, "textChars": 0 },
  "params": { "language": "ru" }
}
```

| Field | Required | Meaning |
|---|---|---|
| `skill` | yes | the skill id |
| `at` | yes | when the call would run |
| `provider` | yes | the kind of provider the wallet would use |
| `estimatedCost` | yes | the wallet's estimate for this call (`"0 USD"` for a local model) |
| `input.clipSeconds` | while a clip limit applies | length in seconds of the audio clip being sent; `0` when the call sends no audio |
| `input.textChars` | while a text limit or `warnIf.sendsText` applies | number of characters of text being sent; `0` when the call sends no text |
| `params` | no | skill parameters from the requester; not checked by the gate |

The gate never sees the content itself; it checks the measurements the wallet gives it. **A measurement is required whenever a limit or a warning that uses it is in force**: `input.clipSeconds` while any effective `maxClipSeconds` exists, and `input.textChars` while any effective `maxTextChars` exists or the grant has `warnIf.sendsText: true`. A call without it, or with a misspelt key such as `clipSecs`, is `call-invalid`; a call that sends no such input states `0` explicitly. Otherwise a missing number would silently bypass the limit.

`input.textChars` counts **all** text the wallet forwards to the provider for this call, including anything the wallet adds itself (instructions, a system prompt, context), not only the requester's text. A call "sends text" when `input.textChars` is above zero. Unknown call fields are ignored: a measurement this version does not know cannot have a limit, because grants refuse unknown limits.

**State** — what the wallet remembers within the session. May be absent (nothing spent, no calls yet).

```json
{ "spent": "0.04 USD", "callTimes": ["2026-10-10T11:59:30Z", "2026-10-10T11:59:45Z"] }
```

`spent` is the money already spent in this session; `callTimes` are the times of calls that actually ran. Like grants, state fails closed: an unknown field (for example a typo `spend`) makes it invalid, because a misspelt `spent` would otherwise reset the spend to zero.

## 5. Call rules: `checkCall(manifest, grant, call, state)`

The gate returns `{ decision, reasons }`. Each reason is `{ code, decision, message }`, plus `path` for the input-checking rules. The overall decision is the most severe decision among the reasons, or `allowed` when there are none.

What the decisions mean for the wallet:

- `allowed` — run the call.
- `warn` — run the call, tell the visitor, log it.
- `ask` — pause; do not run the call unless the visitor approves it now. **If nobody can be asked (no visitor present), `ask` means `blocked`.**
- `blocked` — do not run the call; log it.

**Step 1: inputs.** If any input is invalid, the gate stops here and blocks. All problems found are reported, in this order:

| # | Code | Decision | When |
|---|---|---|---|
| C1 | `manifest-invalid` | blocked | the manifest has errors under Section 3 (one reason per error; the message starts with the manifest rule code) |
| C2 | `grant-invalid` | blocked | the grant does not have the shape in Section 4, has an unknown field, or mixes currencies |
| C3 | `call-invalid` | blocked | the call does not have the shape in Section 4, or (step 1b) lacks a required measurement |
| C4 | `state-invalid` | blocked | the state is present but does not have the shape in Section 4 |

**Step 1b: required measurements.** Once all four inputs are valid, the gate checks that the call states every measurement a limit or warning in force needs (Section 4): first `input.clipSeconds`, then `input.textChars`. Each missing one is a `call-invalid` reason with its path (`/input/clipSeconds`, `/input/textChars`), and the gate stops and blocks. Because this needs the effective limits, it is checked only when the manifest and the grant are valid.

**Step 2: hard limits.** All rules are evaluated, so the visitor sees every reason, not only the first.

| # | Code | Decision | When |
|---|---|---|---|
| C5 | `grant-expired` | blocked | `call.at` is at or after `grant.expiresAt` |
| C6 | `skill-not-declared` | blocked | the skill is not in the manifest |
| C7 | `skill-not-granted` | blocked | the skill is not in `grant.skills` |
| C8 | `not-local` | blocked | the grant has `localOnly: true` or the manifest privacy is `local-only`, and the provider is not `local-model` or `home-server` |
| C9 | `clip-too-long` | blocked | `input.clipSeconds` is above the effective `maxClipSeconds` |
| C10 | `text-too-long` | blocked | `input.textChars` is above the effective `maxTextChars` |
| C11 | `rate-exceeded` | blocked | the number of `state.callTimes` in the minute before the call (later than `at` minus 60 seconds, and not later than `at`) is already at or above the effective `maxCallsPerMinute` |
| C12 | `currency-mismatch` | blocked | `estimatedCost` or `state.spent` is not in the budget's currency (C13, C15 and C16 are then skipped) |
| C13 | `budget-exceeded` | **ask** | `spent + estimatedCost` is above `budget.perSession` (reaching it exactly is allowed) |
| C14 | `text-sent` | warn | `grant.warnIf.sendsText` is true and the call sends text |
| C15 | `call-cost-high` | warn | `estimatedCost` is above `grant.warnIf.callCostAbove` |
| C16 | `spend-high` | warn | `spent + estimatedCost` is above `grant.warnIf.spendAbove` |

The **effective limit** is the smaller of the manifest limit and the grant limit when both exist, otherwise whichever exists. With no limit of a kind, the matching rule does not apply. Unknown manifest limits (warning M8) are not enforced.

**Step 3: purpose judge** (Section 6), only if the decision so far is not `blocked`.

### Decisions taken in this draft, and why

- **Budget exceeded is `ask`, not `blocked`.** The visitor set the budget and may want to raise it for this session; the wallet never spends past the budget without a new yes. An unattended wallet treats `ask` as `blocked`, so nothing is spent without consent either way.
- **Expired grant, undeclared or ungranted skill, cloud call under local-only, over-long clip or text, rate over the limit: `blocked`.** These are limits either side stated explicitly; asking again would make them meaningless.
- **"Warn me if it sends text" is `warn`.** The visitor asked to be told, not to be stopped.
- **The stricter limit wins.** The requester's own limits bind it; the visitor can only tighten them.
- **Invalid input blocks.** A gate that cannot read its inputs must not let calls through.
- **A missing measurement blocks.** A limit that can be skipped by leaving out a number is not a limit.

## 6. The purpose judge hook

Code cannot judge everything: "long documents arriving instead of short phrases" or "this does not look like a geography game" needs a model. The gate leaves a hook for that and does not implement it:

- A **judge** is a function `judge(manifest, grant, call)` that returns no opinion (`null` / `None`) or an **opinion** `{ decision, code?, message? }`, where `decision` is one of the four decisions.
- The default judge has no opinion. Passing no judge, or `null` / `None`, means no judge.
- The judge is consulted only when the hard limits did not block. An opinion can only make the decision stricter, never milder; `allowed` changes nothing.
- The reason added has the opinion's `code`, or `purpose-doubt` when none is given.
- If the judge fails, returns something that is not an opinion, or is not callable at all (for example an object passed by mistake), the gate adds `purpose-judge-error` with decision `ask`, unless the call is already blocked. A misconfigured judge must never be silently skipped.
- Because a judge usually calls a model, which is asynchronous, the libraries also offer `applyOpinion(result, opinion)`: run `checkCall`; if the result is `blocked`, stop; otherwise await the judge yourself, turn a failure or rejection into an `ask` opinion, and merge it.
- The judge treats the content as data, never as instructions, because the content comes from the requester.

The shared vectors in `sdk/test-vectors/purpose-judge.json` pin this behaviour.

## 7. Execution contract

`checkCall` is a pure function: it evaluates the documents it is given and returns a decision. Everything else is the wallet's job.

- **The wallet owns the facts.** It sets the call time `at`, classifies the provider honestly, measures the input (`clipSeconds`, `textChars`) and keeps the state. The gate trusts these values; a wallet that reports them wrongly defeats the limits.
- **The gate neither runs the call nor updates the state.** After a call actually runs, the wallet adds its time to `callTimes` and its real cost to `spent`, and persists the state.
- **Concurrent calls.** Two calls checked against the same state snapshot can both pass even if together they exceed the budget or the rate. The wallet must serialize checks for one grant, or reserve the estimated cost and the call slot before running a call and release or settle them afterwards.
- **Estimates are not a ceiling.** `budget-exceeded` compares the estimate; the real cost can be higher. A wallet that must guarantee a hard ceiling has to stop or cap the call at the provider, or keep a margin.
- **Local means local in fact.** For `localOnly` grants and `local-only` manifests, the wallet verifies where the call is actually processed, including whether a home server forwards it elsewhere.

## 8. Not covered yet

Receipts; budgets per hour and per day; routing rules for the privacy classes other than `local-only`; binding a grant to a requester's identity; skill parameters; minimum quality; streaming calls; turning the visitor's conditions in words into grant rules. Open questions are listed in [../docs/vision.md](../docs/vision.md), Section 12.

The vision lists `embed` as a skill; in this draft it is written `text.embed` so that every skill id has the `domain.action` form.
