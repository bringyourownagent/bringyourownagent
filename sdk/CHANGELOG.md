# Changelog

The libraries share one version number and one rule set. Dates are UTC.

## Unreleased (the next shared version)

- Node.js and .NET relays: when a wallet sends no `capabilities` (wallet 0.35 and older) the page gets `null`, not an empty list, so an older wallet is told apart from one that understands none (`grant.capabilities` / `capabilities` in `wait` and `status`).

## 0.0.4 (2026-10-31)

- Rule change: a skill's `models` block may state `needs` (conditions on `input`, `output`, `context`, `runs`), `kind` (`model`, `agent`, `any`), `onMismatch` (`drop`, `show`) and `abilities`, and a block without `count` is read like any other (rules M22–M28). `purpose.limits.maxFrameBytes` is a known limit. Python, Node.js and .NET implement all of it and pass the same 200 shared test vectors; every manifest the 0.0.3 libraries accept is still accepted.
- The Node.js and .NET relays pass the grant's `capabilities`, every skill's models (`modelsBySkill`; `models` stays the `text.generate` list, so a page written for 0.0.3 keeps working) and their `can`/`abilities` verdicts on to the page, and the .NET relay's job cancel tells the wallet with the `cancel` frame (wallet 0.36).
- A failed call (`ok: false`) may carry `reason`, one of `timeout`, `cancelled`, `limit`, `failed` (wallet 0.36). The .NET relay hands it to the page in `job/poll` and treats any other word as `failed`; it never says how the visitor's setup is made.

## 0.0.3 (2026-10-28)

The site's side of the wallet connection, released. No change to any rule: the shared test vectors are the same 173. The Python package moves to 0.0.3 only to keep the one shared version number; nothing in it changed.

- Node.js: `bringyourownagent/browser`, `bringyourownagent/relay` and `bringyourownagent/crossdevice` are in the package (they were in the repository only). The relay's new option `connectCheck` puts `"connect": { "check": true }` into the links' manifest and adds `check`, the four-character connect check code of spec/CONTRACT-DIRECT.md section 3a, to every `newCode` answer; `connectCheck(session)` computes it on its own. The page helper `connectWithoutExtension` takes `onIssued({ code, link, check })` to show the code (and the link) while it waits. `bringyourownagent/crossdevice` offers the link for another device to a phone or a computer with no wallet: the check code, copy, Share, a QR code made on the page, and the project's web wallet. The example site in `sdk/examples/site/` shows the check code.
- .NET: the first release of `BringYourOwnAgent.Relay`, the same site's side for ASP.NET Core (net10.0): `AddByoaRelay`, `MapByoaRelay`, one-time codes, the check code (`ConnectCheck`), the wallet's WebSocket, and a job relay a page polls. `BringYourOwnAgent` itself only moves to 0.0.3 with it.

## Earlier, unreleased (Node.js)

- Added the site's side of the wallet connection, no change to any rule: `bringyourownagent/browser` (page helpers for the extension, and "connect without the extension" with a `bringyourownagent://` link) and `bringyourownagent/relay` (a small server part: one-time codes, pairing tokens, the wallet's WebSocket and calls to it, as in `spec/CONTRACT-DIRECT.md`). The relay needs the optional peer dependency `ws`. A runnable example site is in `sdk/examples/site/`. Each site runs its own relay; there is no shared one. Reviewed independently before release: bounded call queue, caps on sockets, connection age and what a wallet may return, per-address (IPv6 /64) and per-page code limits, an address that must be given, honest typings.
- The relay follows wallets that work with text (spec/CONTRACT-DIRECT.md section 4, CONTRACT.md sections 3.3 and 3.4), no change to any rule: a connection reports the limits and the models the visitor gave the site (`connection.grant.limits`, `.models`, and a `grant` event when the visitor changes them), keeps as many calls in flight as the wallet's `maxParallelCalls` says (the new option `maxInFlight` can only lower it; 4 for a wallet that says nothing), hands streamed pieces to `call(..., { onChunk })`, and lets a receipt carry the `model` that answered. The three new limit names (`maxAnswerTokens`, `maxCallSeconds`, `maxParallelCalls`) still pass `validateManifest` with a `limit-unknown` warning, as the spec says for 0.0.2 libraries.

## 0.0.2 for .NET (2026-10-10)

- First release of the .NET library, NuGet package `BringYourOwnAgent`, with the same rules as the Python and Node.js 0.0.2: `Manifest.Validate`, `Gate.CheckCall` and `Gate.ApplyOpinion`. It passes the same 173 shared test vectors. Targets .NET 8 (no dependencies) and .NET Standard 2.0 (depends on System.Text.Json).
- Published only from GitHub Actions, through NuGet trusted publishing.
- No change to any rule or code, or to the Python and Node.js packages.

## 0.0.2 (2026-10-10)

- Releases now come only from GitHub Actions: npm through trusted publishing with provenance, PyPI through trusted publishing. No registry tokens exist anywhere.
- Added this changelog and links to it from both packages.
- No change to behaviour or to any rule code.

## 0.0.1 (2026-10-10)

First release, to claim the package names and to put the first draft rules into code.

- `validate_manifest` / `validateManifest`: checks a requester's manifest against the draft rules.
- `check_call` / `checkCall`: decides whether one call may go ahead under the visitor's grant (`allowed`, `warn`, `ask` or `blocked`), with reason codes.
- `apply_opinion` / `applyOpinion`: merges the opinion of an optional purpose judge.
- The `bringyourownagent` command line tool.
- 173 shared test vectors that both libraries pass identically.
