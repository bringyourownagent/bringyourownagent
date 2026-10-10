# BringYourOwnAgent (.NET)

This is an early draft implementation of **BYOA (Bring Your Own Agent)**, an open protocol that lets websites and apps use the visitor's own AI agents (their subscriptions, API keys, connected accounts, local models or home servers) within limits the visitor sets. Versions 0.0.x implement only the first rules: it checks a requester's **manifest**, and it decides whether one **call** may go ahead under the visitor's **grant**. The rules are a [draft for discussion](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md) and will change. Targets .NET 8 (no dependencies) and .NET Standard 2.0, for desktop apps on .NET Framework 4.6.2 or newer (depends on System.Text.Json).

## Install

```sh
dotnet add package BringYourOwnAgent
```

The package, the assembly and the namespace are all named `BringYourOwnAgent`.

## Example

With the manifest shown on the BYOA site ([`sdk/examples/manifest.json`](https://github.com/bringyourownagent/bringyourownagent/blob/main/sdk/examples/manifest.json): a family geography game, voice clips up to 15 seconds):

```csharp
using BringYourOwnAgent;

var manifest = File.ReadAllText("manifest.json");
Console.WriteLine(Manifest.Validate(manifest).Ok);                 // True

var grant = """{"byoa": "0.1-draft", "skills": ["audio.transcribe"], "budget": {"perSession": "0.20 USD"}, "expiresAt": "2026-10-10T13:00:00Z"}""";
var call = """{"skill": "audio.transcribe", "at": "2026-10-10T12:00:00Z", "provider": "local-model", "estimatedCost": "0 USD", "input": {"clipSeconds": 42}}""";
var result = Gate.CheckCall(manifest, grant, call, """{"spent": "0.04 USD"}""");
Console.WriteLine($"{result.Decision} {result.Reasons[0].Code}");  // Blocked clip-too-long
```

- Documents are JSON. Pass them as `JsonElement` (System.Text.Json) or as JSON text (`string`). Text that is not JSON makes that document invalid; it is reported, not thrown.
- `Manifest.Validate(manifest)` returns a `ValidationResult`: `Ok`, `Errors` and `Warnings`; each `Issue` has a stable `Code`, a JSON Pointer `Path` and a `Message`. Problems are returned as data, never thrown.
- `Gate.CheckCall(manifest, grant, call, state)` returns a `CheckResult`: a `Decision` (`Allowed`, `Warn`, `Ask`, `Blocked`) and `Reasons`, each with a stable `Code`. Invalid input blocks the call instead of throwing. A null `state` means nothing spent and no calls yet.
- `ToJson()` on either result gives it in the JSON shape of the Python and Node.js libraries (`{"decision": "blocked", "reasons": [...]}`). Messages are for people and may change; programs should use the codes.
- While a limit or warning needs a measurement, the call must state it: `input.clipSeconds` under a clip limit, `input.textChars` under a text limit or a "warn me if it sends text" condition. Use `0` when there is no such input; a missing value blocks the call.
- `CheckCall` only evaluates. Your wallet runs the call, then records its time and real cost in the state, and must not check two calls of one grant against the same state at once (see "Execution contract" in the rules).
- Money is handled as whole micro-units in a 64-bit integer, never as floating point: `"0.1 USD"` plus `"0.2 USD"` fits a budget of `"0.3 USD"` exactly.

## The purpose judge hook

The hard limits are checked by code. Judging whether a call fits the declared purpose in words needs a model, and is not part of this library. It plugs in as a `PurposeJudge`: a function of the manifest, the grant and the call that returns an `Opinion` (`Decision`, optional `Code` and `Message`) or `null`. An opinion can only make the decision stricter; a judge that throws gives `Ask`.

```csharp
var result = Gate.CheckCall(manifest, grant, call, state, judge: (m, g, c) => MySyncJudge(m, g, c));
```

A judge that calls a model is usually asynchronous. Check first, and ask the judge only if the call is not already blocked:

```csharp
async Task<CheckResult> DecideAsync(JsonElement manifest, JsonElement grant, JsonElement call, JsonElement? state)
{
    var result = Gate.CheckCall(manifest, grant, call, state);
    if (result.Decision == Decision.Blocked) return result;
    Opinion? opinion;
    try { opinion = await MyAsyncJudge(manifest, grant, call); }
    catch (Exception) { opinion = new Opinion(Decision.Ask, "purpose-judge-error", "the purpose judge failed"); }
    return Gate.ApplyOpinion(result, opinion);
}
```

If the model answers in JSON, pass it as it is: `Gate.ApplyOpinion(result, answer)` with a `JsonElement` reads `{"decision": ..., "code": ..., "message": ...}`, and anything that is not an opinion gives `Ask` with the code `purpose-judge-error`.

## Development

The tests are a small console program with no test framework. They run the shared test vectors in `sdk/test-vectors/`, which the Python and Node.js packages must pass identically, and some .NET-specific tests. From `sdk/dotnet/` in the repository:

```sh
dotnet run --project tests/BringYourOwnAgent.Tests -c Release -f net8.0                            # the net8.0 build
dotnet run --project tests/BringYourOwnAgent.Tests -c Release -f net8.0 -p:TestNetStandard=true    # the netstandard2.0 build
dotnet run --project tests/BringYourOwnAgent.Tests -c Release -f net48                             # netstandard2.0 on .NET Framework (Windows)
```

There is no command line tool in this package; the Python and Node.js packages include one.

## Links

- Website: <https://bringyourownagent.org>
- Repository: <https://github.com/bringyourownagent/bringyourownagent>
- Rules implemented here (draft): [spec/rules-0.1-draft.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md)
- The same rules in Python and Node.js: [`bringyourownagent` on PyPI](https://pypi.org/project/bringyourownagent/) and [on npm](https://www.npmjs.com/package/bringyourownagent)
- Changes between versions: [CHANGELOG](https://github.com/bringyourownagent/bringyourownagent/blob/main/sdk/CHANGELOG.md). Releases are published only from GitHub Actions through trusted publishing.

## License

[Apache License 2.0](https://github.com/bringyourownagent/bringyourownagent/blob/main/LICENSE).
