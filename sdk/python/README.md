# bringyourownagent (Python)

This is an early draft implementation of **BYOA (Bring Your Own Agent)**, an open protocol that lets websites and apps use the visitor's own AI agents (their subscriptions, API keys, connected accounts, local models or home servers) within limits the visitor sets. Version 0.0.1 implements only the first rules: it checks a requester's **manifest**, and it decides whether one **call** may go ahead under the visitor's **grant**. The rules are a [draft for discussion](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md) and will change. Python 3.9 or newer, standard library only, no dependencies.

## Install

```sh
pip install bringyourownagent
```

The distribution, the package you import and the command are all named `bringyourownagent`.

## Example

With the manifest shown on the BYOA site ([`sdk/examples/manifest.json`](https://github.com/bringyourownagent/bringyourownagent/blob/main/sdk/examples/manifest.json): a family geography game, voice clips up to 15 seconds):

```python
import json
from bringyourownagent import check_call, validate_manifest

manifest = json.load(open("manifest.json"))
print(validate_manifest(manifest)["ok"])                 # True

grant = {"byoa": "0.1-draft", "skills": ["audio.transcribe"],
         "budget": {"perSession": "0.20 USD"}, "expiresAt": "2026-10-10T13:00:00Z"}
call = {"skill": "audio.transcribe", "at": "2026-10-10T12:00:00Z", "provider": "local-model",
        "estimatedCost": "0 USD", "input": {"clipSeconds": 42}}
result = check_call(manifest, grant, call, {"spent": "0.04 USD"})
print(result["decision"], result["reasons"][0]["code"])  # blocked clip-too-long
```

- `validate_manifest(manifest)` returns `{"ok", "errors", "warnings"}`; each item is `{"code", "path", "message"}` with a stable code and a JSON Pointer path. Problems are returned as data, never raised.
- `check_call(manifest, grant, call, state=None)` returns `{"decision", "reasons"}`. The decision is one of `allowed`, `warn`, `ask`, `blocked`; each reason has a stable code. Invalid input blocks the call instead of raising.
- While a limit or warning needs a measurement, the call must state it: `input.clipSeconds` under a clip limit, `input.textChars` under a text limit or a "warn me if it sends text" condition. Use `0` when there is no such input; a missing value blocks the call.
- `check_call` only evaluates. Your wallet runs the call, then records its time and real cost in the state, and must not check two calls of one grant against the same state at once (see "Execution contract" in the rules).
- Money is handled as `Decimal`, never as float: `"0.1 USD"` plus `"0.2 USD"` fits a budget of `"0.3 USD"` exactly.

## The purpose judge hook

The hard limits are checked by code. Judging whether a call fits the declared purpose in words needs a model, and is not part of this library. It plugs in as a judge that returns an opinion (`{"decision", "code", "message"}`) or `None`. An opinion can only make the decision stricter; a judge that fails, returns something else or is not callable gives `ask`.

```python
result = check_call(manifest, grant, call, state, judge=my_sync_judge)
```

A judge that calls a model is usually asynchronous. Check first, and ask the judge only if the call is not already blocked:

```python
from bringyourownagent import apply_opinion, check_call

async def decide(manifest, grant, call, state):
    result = check_call(manifest, grant, call, state)
    if result["decision"] == "blocked":
        return result
    try:
        opinion = await my_async_judge(manifest, grant, call)
    except Exception:
        opinion = {"decision": "ask", "code": "purpose-judge-error", "message": "the purpose judge failed"}
    return apply_opinion(result, opinion)
```

## Command line

```sh
bringyourownagent validate manifest.json
bringyourownagent check --manifest manifest.json --grant grant.json --call call.json [--state state.json]
bringyourownagent check --json ...      # machine-readable output
python -m bringyourownagent --help      # the same tool
```

Exit codes: `0` the manifest is valid or the call may proceed (`allowed`, `warn`); `1` the manifest is invalid or the call may not proceed (`ask`, `blocked`); `2` usage error or unreadable input. Example files are in [`sdk/examples/`](https://github.com/bringyourownagent/bringyourownagent/tree/main/sdk/examples).

## Development

The tests include the shared test vectors in `sdk/test-vectors/`, which the Node.js package must pass identically. From `sdk/python/` in the repository:

```sh
python -m unittest discover -s tests -t tests
```

## Links

- Website: <https://bringyourownagent.org>
- Repository: <https://github.com/bringyourownagent/bringyourownagent>
- Rules implemented here (draft): [spec/rules-0.1-draft.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md)
- Node.js package with the same rules: [`bringyourownagent` on npm](https://www.npmjs.com/package/bringyourownagent)

## License

[Apache License 2.0](LICENSE).
