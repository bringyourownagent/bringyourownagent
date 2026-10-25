# BYOA specification

Version 0.1 is in progress; [spec-0.1-draft.md](spec-0.1-draft.md) is its first complete draft. Planned contents:

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

- [spec-0.1-draft.md](spec-0.1-draft.md): the overview of 0.1 and the obligations of a wallet and of a requester. Start here.
- [rules-0.1-draft.md](rules-0.1-draft.md): the machine-checkable rules (manifest checks and the call gate).
- [CONTRACT.md](CONTRACT.md): how a web page, a browser extension and a desktop wallet talk (`window.byoa`, the skills `audio.transcribe` and `text.generate`, the extension and native host messages).
- [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md): the second way, without an extension: a `bringyourownagent://` link that starts the wallet, then a channel from the wallet to the site's server.
- [CONTRACT-PAIR.md](CONTRACT-PAIR.md): other devices and the web wallet: a link handed to the wallet by hand, a hand-off URL, a second screen that shares a live pairing, and a wallet that runs in a browser tab.

## Which text binds

All four documents are drafts. Within a draft, each has a status:

| Document | Status | What binds |
|---|---|---|
| [spec-0.1-draft.md](spec-0.1-draft.md) | normative (draft): overview and obligations | The obligations of a wallet and of a requester, where it says "conforming". The sections that name no obligation explain and are informative. |
| [rules-0.1-draft.md](rules-0.1-draft.md) | normative (draft) | Every rule with a code. The shared test vectors in [`../sdk/test-vectors/`](../sdk/test-vectors/) are the executable form of these rules: the three libraries pass the same vectors, and a disagreement between text and vectors is a bug in one of them. |
| [CONTRACT.md](CONTRACT.md) | normative (draft) for the extension binding | The page API, the messages between the extension, the native host and the wallet, and the skills. The rules win for anything the rules cover. |
| [CONTRACT-DIRECT.md](CONTRACT-DIRECT.md) | normative (draft) for the direct link | The link, what happens before Allow, the channel and the error codes, and what a site has to do. |

**Informative, whatever document they are in:** how the reference wallet is installed and registered (CONTRACT.md section 4 "Install", CONTRACT-DIRECT.md
section 2 "Registration"), the examples, the sizes and times marked as "sensible limits", and every sentence that says what the reference wallet
currently does rather than what a wallet must do. Where a companion and the overview disagree, the companion wins.

A disagreement between these texts and the reference wallet or the libraries is a bug in one of them. Please open an issue and say which.
