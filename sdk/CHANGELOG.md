# Changelog

The libraries share one version number and one rule set. Dates are UTC.

## 0.0.7 (2026-11-02)

No rule and no frame changes: every manifest and every wallet the 0.0.6 libraries work with still works.

- The hand-off address of spec/CONTRACT-PAIR.md section 2, in the .NET relay (opt-in: `RelayOptions.HandOff = true`; off, the code answer is exactly the 0.0.6 one). A real link carries the site's manifest and is often over 3,000 characters, which no QR code holds (2,953 bytes at most). With the option on, `POST /byoa/code` also answers `handoff` (`https://<site host>/byoa/hand/<token>`, under 100 characters) and `short` (`K7MQ-3XPZ`), and the relay serves the page: a `GET` that reveals nothing (no link, no code, no token in it; `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag: noindex`, a Content-Security-Policy that allows only the page's own script and style by hash), a `POST` from a click that releases the link once and tells a second one it was used, and the page where a short code is typed. The token is 192 bits and dies with its code, after 120 seconds (`HandOffLifeSeconds`) or at its first use; the code stays valid so the wallet can still connect. More than 5 wrong short codes from one address in 10 minutes, or 60 from all addresses in a minute, are refused whatever the code; a wrong, a used and an expired short code get one answer. Nothing of a token, short code or link is logged by the library. The page's words are English; `HandOffLabels` and `HandOffLabelsFor` give a site its own. `DirectHub.Issue` is `NewCode` with the hand-off; `DirectHub.HandOffOpen` is the click.
- The hand-off page's button, on each device (written into section 2): on a phone it opens `<web wallet>#link=<link>` (the Android wallet claims that address as an App Link, section 5; the web wallet takes it otherwise); on a computer it opens the `bringyourownagent://` link, with a second, smaller button for the web wallet (`HandOffWebWallet`).
- Node.js `bringyourownagent/crossdevice`: given a `handoff` address the block's QR code carries it (the web wallet's address before), and a new `short` option shows the typed form with the page where it is entered. Without a hand-off address a link that is too long for any QR code now gets one plain line saying so (`labels.qrTooLong`) instead of nothing. The Node.js relay does not make hand-off addresses yet; a site that uses it makes its own.
- Spec only: `spec/CONTRACT-PAIR.md` section 5 (new; "Not specified here" is now section 6): a site SHOULD offer the QR code of the "Open on another device" block to a visitor on a phone or on a computer with no wallet, and a native phone wallet MAY claim the web wallet's address as an Android App Link (later an iOS universal link). A wallet that takes a link this way treats it as a handed link (check code, Allow held until the visitor ticks that the codes match) and takes only a `bringyourownagent://connect?` link from the fragment. The web wallet's origin publishes `/.well-known/assetlinks.json` for the project's Android wallet (`webwallet/.well-known/assetlinks.json`).

## 0.0.6 (2026-11-01)

- Rule change: `needs` has two conditions, `input` and `output`; `context` and `runs` are unknown keys now (a `needs-unknown` warning, never an error, so every manifest the 0.0.5 libraries accepted is still accepted). `kind` on `image.generate` may only be `model` (M23). The shared vectors say so in all three libraries.
- Pictures (wallet 0.37, `image.generate`, spec/CONTRACT.md section 3.7): the .NET relay's `job` takes `skill: "image.generate"` with `prompt`, `references`, `count`, `aspect`, `maxSide` and `maxBytes`, and `job/poll` always has `images` (null, or `[{ mime, size, sha256, data, reduced }]`); the Node.js relay's `wallet.call('image.generate', ...)` answers `output.images` the same way. Both join the wallet's picture parts and check them; anything that does not add up is a failed call.
- The relays' `can` verdicts are `input` and `output` only. The .NET relay takes a job request of up to 4,200,000 bytes (`MaxJobRequestBytes`), so reference pictures fit.

## 0.0.5 (2026-11-01)

- A one-time code now lives 10 minutes, not 120 seconds (CONTRACT-DIRECT.md §1 and §4; wallet 0.36.1 waits as long for the visitor). Node.js `CODE_LIFE_MS` and .NET `RelayOptions.CodeLifeSeconds` default to 600; the answer's `expiresIn` says it. `connectWithoutExtension` waits as long as `expiresIn` (else 10 minutes) unless `waitMs` is given, where it waited 90 seconds. A site that wants its codes shorter sets the option.

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
