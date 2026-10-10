# bringyourownagent (Node.js)

This is an early draft implementation of **BYOA (Bring Your Own Agent)**, an open protocol that lets websites and apps use the visitor's own AI agents (their subscriptions, API keys, connected accounts, local models or home servers) within limits the visitor sets. Version 0.0.1 implements only the first rules: it checks a requester's **manifest**, and it decides whether one **call** may go ahead under the visitor's **grant**. The rules are a [draft for discussion](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md) and will change. ES module, Node.js 18 or newer, zero dependencies, TypeScript types included.

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

## License

[Apache License 2.0](LICENSE).
