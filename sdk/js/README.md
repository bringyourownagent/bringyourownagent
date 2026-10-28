# bringyourownagent (Node.js)

This is an early draft implementation of **BYOA (Bring Your Own Agent)**, an open protocol that lets websites and apps use the visitor's own AI agents (their subscriptions, API keys, connected accounts, local models or home servers) within limits the visitor sets. Versions 0.0.x implement only the first rules: it checks a requester's **manifest**, and it decides whether one **call** may go ahead under the visitor's **grant**. The rules are a [draft for discussion](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md) and will change. ES module, Node.js 18 or newer, zero dependencies, TypeScript types included.

## Install

```sh
npm install bringyourownagent
```

The package and its command are both named `bringyourownagent`.

## Example

With the manifest shown on the BYOA site ([`sdk/examples/manifest.json`](https://github.com/bringyourownagent/bringyourownagent/blob/main/sdk/examples/manifest.json): a family geography game, voice clips up to 15 seconds):

```js
import { readFileSync } from 'node:fs';
import { validateManifest, checkCall } from 'bringyourownagent';

const manifest = JSON.parse(readFileSync('manifest.json', 'utf8'));
console.log(validateManifest(manifest).ok);               // true

const grant = { byoa: '0.1-draft', skills: ['audio.transcribe'],
  budget: { perSession: '0.20 USD' }, expiresAt: '2026-10-10T13:00:00Z' };
const call = { skill: 'audio.transcribe', at: '2026-10-10T12:00:00Z', provider: 'local-model',
  estimatedCost: '0 USD', input: { clipSeconds: 42 } };
const result = checkCall(manifest, grant, call, { spent: '0.04 USD' });
console.log(result.decision, result.reasons[0].code);    // blocked clip-too-long
```

- `validateManifest(manifest)` returns `{ ok, errors, warnings }`; each item is `{ code, path, message }` with a stable code and a JSON Pointer path. Problems are returned as data, never thrown.
- `checkCall(manifest, grant, call, state?)` returns `{ decision, reasons }`. The decision is one of `allowed`, `warn`, `ask`, `blocked`; each reason has a stable code. Invalid input blocks the call instead of throwing.
- While a limit or warning needs a measurement, the call must state it: `input.clipSeconds` under a clip limit, `input.textChars` under a text limit or a "warn me if it sends text" condition. Use `0` when there is no such input; a missing value blocks the call.
- `checkCall` only evaluates. Your wallet runs the call, then records its time and real cost in the state, and must not check two calls of one grant against the same state at once (see "Execution contract" in the rules).
- Money is handled as BigInt micro-units, never as floating point: `'0.1 USD'` plus `'0.2 USD'` fits a budget of `'0.3 USD'` exactly.

## The purpose judge hook

The hard limits are checked by code. Judging whether a call fits the declared purpose in words needs a model, and is not part of this library. It plugs in as a judge that returns an opinion (`{ decision, code?, message? }`) or `null`. An opinion can only make the decision stricter; a judge that throws, returns something else or is not a function gives `ask`.

```js
const result = checkCall(manifest, grant, call, state, { judge: mySyncJudge });
```

A judge that calls a model is usually asynchronous. Check first, and ask the judge only if the call is not already blocked:

```js
import { applyOpinion, checkCall } from 'bringyourownagent';

async function decide(manifest, grant, call, state) {
  const result = checkCall(manifest, grant, call, state);
  if (result.decision === 'blocked') return result;
  let opinion;
  try {
    opinion = await myAsyncJudge(manifest, grant, call);
  } catch {
    opinion = { decision: 'ask', code: 'purpose-judge-error', message: 'the purpose judge failed' };
  }
  return applyOpinion(result, opinion);
}
```

## Command line

```sh
npx bringyourownagent validate manifest.json
npx bringyourownagent check --manifest manifest.json --grant grant.json --call call.json [--state state.json]
npx bringyourownagent check --json ...      # machine-readable output
```

Exit codes: `0` the manifest is valid or the call may proceed (`allowed`, `warn`); `1` the manifest is invalid or the call may not proceed (`ask`, `blocked`); `2` usage error or unreadable input. Example files are in [`sdk/examples/`](https://github.com/bringyourownagent/bringyourownagent/tree/main/sdk/examples).

## For sites: reach the visitor's agent wallet (draft)

Besides the rules, the package has the two halves of a site that asks the visitor's own agent wallet for AI. They follow [spec/CONTRACT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT.md) and [spec/CONTRACT-DIRECT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT-DIRECT.md). A complete example site is in [`sdk/examples/site/`](https://github.com/bringyourownagent/bringyourownagent/tree/main/sdk/examples/site).

**In the page** (`bringyourownagent/browser`, no dependencies, an ES module):

```js
import { hasExtension, requestWithExtension, callWithExtension, connectWithoutExtension } from 'bringyourownagent/browser';

button.onclick = async () => {              // both need a click
  if (hasExtension()) {
    const grant = await requestWithExtension(manifest);          // null: AI unavailable, use your own path
    const answer = await callWithExtension(grant, 'text.generate', { messages }, { choices: ['yes', 'no'] });
  } else {
    const r = await connectWithoutExtension({                     // starts the wallet with a bringyourownagent:// link
      onIssued: ({ link, check }) => showWaiting(check, link),    // show the check code big; the wallet shows the same
    });
    // r = { state: 'connected', pairing } | { state: 'timeout' | 'expired' | 'cancelled' | 'error' }
  }
};
```

While the page waits it shows the **check code** in large letters, with a line like "Your wallet will show this code. Allow only if it matches." The wallet shows the same four characters before Allow, so a link that somebody else sent to the visitor cannot pair his wallet with their page (spec/CONTRACT-DIRECT.md section 3a).

**On your server** (`bringyourownagent/relay`, needs `npm install ws`):

```js
import { createRelay } from 'bringyourownagent/relay';

const relay = createRelay({ manifest, endpoint: 'wss://your-host/byoa/ws', connectCheck: true });   // the manifest is validated now
await relay.attach(httpServer, '/byoa/ws');                                      // the wallet connects here

// your routes: POST /byoa/code      -> relay.newCode(pageKey, { ip })     { code, link, check }   (ip: the connection's, or your proxy's header)
//              GET  /byoa/state     -> relay.codeState(pageKey, code)     { state, pairing? }
//              POST /byoa/cancel    -> relay.cancel(pageKey, code)

const wallet = relay.walletFor(pairing);                                          // what the page kept after connecting
const answer = await wallet?.call('text.generate', { messages }, { maxTokens: 8, choices: ['yes', 'no'] });
// { ok: true, output: { text }, receipt } or { ok: false }; never an exception. The answer is user input.
```

The relay keeps codes (192 random bits, 120 s, single use, rate limited per address and per page) and pairings in memory, sends nothing before a valid `hello`, echoes the manifest in `welcome`, allows 4 calls in flight per wallet (a bounded queue behind them; the wait counts from the call), gives the first call after a connect a longer wait, drops a silent or over-old connection, caps open sockets, and cuts what a wallet hands back down to what the skill defines (a receipt of two short strings, an `output` under 64 KB). `wallet.on('failing')` tells you when three calls in a row failed. The pairing is a secret of your server: keep it server-side, not in script the page can read. Behind a reverse proxy, forward the WebSocket path (Apache: `ProxyPass /byoa/ws ws://127.0.0.1:PORT/byoa/ws`).

**For a visitor on a phone, or on a computer with no wallet** (`bringyourownagent/crossdevice`, no dependencies, an ES module): a phone browser does not launch the wallet's link, so offer the link for another device instead. After `POST /byoa/code` has answered `{ link, check }`:

```js
import { createCrossDeviceBlock, isMobileBrowser, CROSS_DEVICE_CSS } from 'bringyourownagent/crossdevice';

if (isMobileBrowser()) {                       // nothing on a phone handles bringyourownagent://, so do not fire the link there
  waitingArea.append(createCrossDeviceBlock({ link, check }));   // the check code, the link to copy, Share (where there is one), a QR code
}
// webWallet: 'https://wallet.example/'  your own web wallet instead of the project's (https://wallet.bringyourownagent.org/, "Agent wallet (web)"),
//                                       which the block offers by default: a button, and the QR code opens it with the link. `webWallet: null` offers none.
// handoff:   'https://your-host/byoa/hand/<token>'  offers an https address instead of the raw link (spec/CONTRACT-PAIR.md section 2)
```

The block is built with the DOM and everything is set as text, never as markup. The QR code is made here (a small encoder, no network). See [spec/CONTRACT-PAIR.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT-PAIR.md); in the wallet the visitor opens Sites, chooses **Connect a site by link…** and pastes the link.

## Development

The tests include the shared test vectors in `sdk/test-vectors/`, which the Python package must pass identically. From `sdk/js/` in the repository:

```sh
npm test        # node --test
```

## Links

- Website: <https://bringyourownagent.org>
- Repository: <https://github.com/bringyourownagent/bringyourownagent>
- Rules implemented here (draft): [spec/rules-0.1-draft.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md)
- Python package with the same rules: [`bringyourownagent` on PyPI](https://pypi.org/project/bringyourownagent/)
- Changes between versions: [CHANGELOG](https://github.com/bringyourownagent/bringyourownagent/blob/main/sdk/CHANGELOG.md). Releases are published only from GitHub Actions through trusted publishing.

## License

[Apache License 2.0](LICENSE).
