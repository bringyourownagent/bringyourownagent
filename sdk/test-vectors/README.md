# BYOA shared test vectors

These JSON files are the contract between the BYOA implementations. The Python, Node.js and .NET libraries all load them and must produce the same results; any future implementation should load them too. The rules they test are in [`../../spec/rules-0.1-draft.md`](../../spec/rules-0.1-draft.md) (a draft for discussion).

| File | Function under test |
|---|---|
| [`validate-manifest.json`](validate-manifest.json) | `validateManifest(manifest)` / `validate_manifest(manifest)` |
| [`check-call.json`](check-call.json) | `checkCall(manifest, grant, call, state)` / `check_call(manifest, grant, call, state)`, hard limits only, without a purpose judge |
| [`purpose-judge.json`](purpose-judge.json) | the same gate with a purpose judge: `checkCall(..., { judge })` / `check_call(..., judge=...)` |

## Format

Each file has `defaults` (one document per input) and a list of `cases`. Each case has a `name`, its inputs and an `expect` object.

An input document of a case is built like this, for each of `manifest`, `grant`, `call` and `state`:

1. If the case has the key itself (for example `"grant"`), its value is used as is, even `null`.
2. Otherwise, if the case has `<name>Patch` (for example `"grantPatch"`), the patch is applied to the default document as a [JSON Merge Patch (RFC 7396)](https://www.rfc-editor.org/rfc/rfc7396): objects merge, `null` removes a key, anything else (arrays included) replaces.
3. Otherwise the default document is used.

`state: null` means "no state" (nothing spent, no calls yet).

### `validate-manifest.json`

```json
"expect": { "ok": false, "errors": [{ "code": "privacy-unknown", "path": "/privacy" }], "warnings": [] }
```

`errors` and `warnings` list `{ code, path }` in the exact order the implementation must report them. Messages are for people and are not compared.

### `check-call.json`

```json
"expect": { "decision": "blocked", "reasons": [{ "code": "grant-invalid", "path": "/expiresAt" }] }
```

`decision` is the overall decision. `reasons` lists the reasons in order. A reason has `path` exactly when the implementation reports one (the input-checking reasons `manifest-invalid`, `grant-invalid`, `call-invalid`, `state-invalid`); the comparison is exact, so an implementation may not add or drop paths.

### `purpose-judge.json`

Same documents and `expect` format as `check-call.json`, plus a `judge` key saying which judge the test passes:

| `judge` | The judge passed to the gate |
|---|---|
| absent or `null` | no judge (`null` / `None`) |
| `{ "returns": v }` | a function that returns a copy of `v` (`null` means no opinion) |
| `{ "throws": "message" }` | a function that throws / raises an error with that message |
| `{ "notCallable": v }` | `v` itself, which is not a function (a misconfigured judge) |

### Numbers in the vectors

Some cases use numbers beyond 2^53 - 1, such as `9007199254740993`, `1e400` (infinite as a double) and a 400-digit integer. Read the files with your language's ordinary JSON parser, and copy documents in a way that keeps such values: in JavaScript, `structuredClone`, not a `JSON.stringify` round trip, which would turn `Infinity` into `null`.

## Adding a case

Add it to the right file, run both test suites (see [`../README.md`](../README.md)), and fix whichever implementation disagrees with the spec. If the spec itself is unclear, change the spec first.
