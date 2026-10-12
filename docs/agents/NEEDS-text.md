# Needed on the desktop side for text.generate

Status: 12 October 2026, from the text.generate work (`docs/text-generate.md`). Everything below is additive; nothing the desktop has today breaks. Until item 1 lands, the wallet simply has no text sources — pages asking for `text.generate` are turned away at the consent window, and everything else keeps working.

## 1. Required — hand the text sources to the engine

`wallet/desktop/Byoa.Wallet.Desktop/WalletServices.cs`, the engine line, gains one argument:

```csharp
Engine = new WalletEngine(Store, Sources.All, Activity, Ui, textSources: Sources.Text);
```

`Sources.Text` is `IReadOnlyList<ITextSource>` on the existing `SourceSet` (`WalletServices.Sources`), stable for the engine's life — a settings change swaps its contents in place, no new engine needed. The optional `TimeProvider` argument is unchanged. Without it the engine defaults to an empty text list.

## 2. Required — show the text chain in Skills & fallbacks

`SettingsWindow.SkillsPage` resolves every chain stop against `_w.Sources.All` (speech sources only), so the `text.generate` chain is silently skipped: the route works, but the settings page shows nothing for the skill. Look the stop up in both lists, for example:

```csharp
var source = _w.Sources.All.FirstOrDefault(s => s.Id == stop.SourceId)
          ?? (object?)_w.Sources.FindText(stop.SourceId);
```

(`StopRow` and `Router.Plan` take `ISpeechSource`/source lists — the text sources implement the same `Name`/`Runs`/`Paid`/`StateText` surface through `ITextSource`, so a shared interface or a small adapter is a desktop-side choice; `Router.PlanText` already exists for planning a text chain.)

## 3. Not needed — settings model, scanner, chain order

- **No settings-model change.** `SourceSetting` already carries `Kind`, `Model`, `Label`, `Endpoint`, and `SourceSet` rebuilds the text sources on every settings change. A text source needs: `Kind` of `lmstudio`, `ollama`, `openai-compatible` (then `Endpoint` is required, e.g. `http://127.0.0.1:8080/v1`), or `openrouter` (paid, uses the OpenRouter key from the key page); `Model` is required for all four. Any `Skills` listing `text.generate` is enough for `SourceSet` to put it in the chain.
- **No scanner wiring.** `SourceSet` starts its own scan loop (every 30 s, LM Studio on `127.0.0.1:1234`, Ollama on `127.0.0.1:11434`, plus every added endpoint) and sets each source's reachable/loaded flags itself.
- **No chain seeding.** New text sources get a `text.generate` stop on their own, ahead of any paid stop.
