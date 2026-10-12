# BYOA specification

Version 0.1 is in progress. Planned contents:

1. Terms and roles: visitor, agent wallet, provider, requester.
2. Skills: names, parameters, quality tiers.
3. Messages: Manifest, Grant, Call, Receipt.
4. Declared purpose: hard limits, purpose in words, visitor's conditions, reactions.
5. Privacy classes and routing rules.
6. Budgets, metering and receipts.
7. Transport bindings: browser (v0.1), desktop apps, agent to agent.
8. Security considerations.

## Planned for later drafts

This list is approximate: items may be merged, split, reordered or dropped, and none of them is implemented in the draft libraries yet.

- Grants bound to the requesting origin, the session and the digest of the manifest they were given for.
- Replay protection and revocation.
- "Must-understand" limits: limits that a wallet has to refuse to run without, rather than ignore when it does not know them.
- Budget reservations, settlement and receipts, with estimated and actual cost shown separately.
- Capability negotiation and structured errors.
- A browser binding with a no-wallet fallback, and browser built-in AI as one more provider.
- A permission-difference screen (what changed since the last grant) and a cost-and-data preview in wallets.

The concept is described in [../docs/vision.md](../docs/vision.md).

A first draft of the machine-checkable rules (manifest checks and the call gate) is in [rules-0.1-draft.md](rules-0.1-draft.md). It is implemented by the early draft libraries in [../sdk/](../sdk/).

## Documents in this folder

- [rules-0.1-draft.md](rules-0.1-draft.md): the machine-checkable rules (manifest checks and the call gate).
- [CONTRACT.md](CONTRACT.md): how a web page, a browser extension and a desktop wallet talk (`window.byoa`, the skills `audio.transcribe` and `text.generate`, the extension and native host messages).
- [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md): the second way, without an extension: a `bringyourownagent://` link that starts the wallet, then a channel from the wallet to the site's server.
