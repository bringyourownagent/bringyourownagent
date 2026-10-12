# Registry integration notes

The registry branch is intentionally built against the public source contracts and does not edit provider or engine files.

## Engine test hook

The Sources page and MCP `test_source` need one tiny call through the same gate, limits, and routing as a real site call, without creating a fake site grant or showing a site consent window. The smallest engine API I would like is:

```csharp
Task<JsonNode?> TestSourceAsync(string sourceId, string skill, CancellationToken ct = default);
```

The engine should use its normal gate and route for a wallet-owned test request, with a built-in short sample for `audio.transcribe` and a one-line prompt for `text.generate`. It should return the same plain success/failure shape used by the registry adapter. Until that exists, the desktop adapter calls local speech sources directly and sends a one-line request to a local OpenAI-compatible endpoint; paid tests are refused instead of creating a bill.

## Added-source registration

The registry stores visitor-added `SourceSetting` records and the MCP/UI can manage them now. The current `SourceSet` and speech-only engine expose no factory or registration method for an added source. The smallest merge-time API would be:

```csharp
void RefreshAddedSources(IEnumerable<SourceSetting> settings);
```

or a constructor/factory that accepts `IReadOnlyList<ISpeechSource>` and `IReadOnlyList<ITextSource>` built from the settings. The registry must remain the state owner; the provider set should only materialise sources from its snapshot.

## Language-specific chains

`SkillChain.Language` and `WalletSettings.Chain(skill, language)` are now available. The current engine still reads `Chain(skill)` only, so language-specific orders are visible and editable through the registry/MCP but are not selected by real engine routing until the engine chooses the language-specific chain first.

## Model removal

The registry validates a downloaded model path against the active wallet home before it calls `IWalletModelManager.RemoveAsync`. The desktop adapter now deletes only a file recorded in the wallet's download ledger and inside its wallet-created model folder; no path outside the active wallet home may ever reach it.

## Direct-link consent integration

The direct transport needs the consent window to distinguish a link-origin request from an extension request. The smallest engine integration is the optional source parameter already used by this branch:

```csharp
ConsentModel BuildConsent(string origin, string host, bool secure, RequestedManifest manifest, SiteRecord site, string? via);
```

It sets `ConsentModel.FromLink` when `via == WalletEngine.ViaLink`; the desktop window then shows the link-origin wording and reserves `verified` for the extension path. This is a six-line engine change and should be carried when the engine branch is merged.
