# BYOA agent wallet 0.1 (Windows)

The first working agent wallet for [BYOA](../README.md): a tray app on your computer, a small native messaging host and a Chrome extension. A web page asks for your AI with `window.byoa`; the wallet shows a short notice, then a short consent window, and answers the page's calls with **your** models: local speech recognition on your own GPU or CPU, or your own API key as a paid fallback that asks first.

0.1 serves one skill, `audio.transcribe` (speech to text), from three sources:

| Source | Runs | Languages | Cost | Notes |
|---|---|---|---|---|
| GigaAM v3 (sherpa-onnx) | this PC, CPU | Russian only | no cloud bill | very fast and accurate for Russian; uses a model folder already on disk |
| Whisper large-v3, q5_0 (Whisper.net / whisper.cpp) | this PC, GPU through Vulkan, CPU fallback | 100 | no cloud bill | downloads once (1.1 GB) into `%LOCALAPPDATA%\BYOA\models` on first use, with progress shown |
| ElevenLabs Scribe v2 through OpenRouter | your API key | 99 | paid, about $0.00004 a second | the last fallback; asks before it runs |

The default route for a call: GigaAM when the page says the language is `ru`, Whisper for other languages and as the fallback, OpenRouter last. You can reorder the route, switch sources off and decide whether the paid one asks.

The design and the security notes are in [docs/wallet-0.1.md](../docs/wallet-0.1.md). The page API is the BYOA 0.1 contract: `window.byoa.request(manifest)`, `call(grantId, skill, input, params)`, `release(grantId)`.

**Without the extension.** A site can also show a `bringyourownagent://connect?...` link. A click on it starts the wallet (or hands the link to the running one); the wallet reads the site's manifest from the link itself, shows the same notice and consent, and only after Allow contacts the site at all: it opens its own WebSocket to the site's server, which then sends its calls straight to the wallet. Same grants, limits and routing; the activity marks these "via link". Details: [docs/wallet-0.1.md, section 10](../docs/wallet-0.1.md#10-transports).

## Install for your own Chrome

Needs Windows 10 or 11, the .NET 10 SDK and Chrome (or another Chromium browser).

1. In PowerShell, from the repository root:

   ```powershell
   powershell -ExecutionPolicy Bypass -File wallet\install.ps1
   ```

   It publishes the wallet and the host into `%LOCALAPPDATA%\BYOA\wallet-app`, writes the host manifest there and registers the host for your user only (`HKCU\Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet` and the same under `Software\Chromium`), and the `bringyourownagent://` links (`HKCU\Software\Classes\bringyourownagent`). Add `-StartWithWindows` to put a shortcut into your Startup folder (off by default).
2. In Chrome open `chrome://extensions`, turn on **Developer mode**, press **Load unpacked** and choose the folder `wallet\extension` that install.ps1 prints. The extension ID is always `niobopdoiopjilmlonjcgllpmgjemhpk` (fixed by the `key` in its manifest), which is the only origin the host accepts.
3. Optional: add an OpenRouter key for the paid fallback in the wallet's **Sources** page (or `Byoa.Wallet.exe --import-key openrouter <file with the key>`). It is stored with Windows DPAPI for your account and never shown again.
4. Open a page that uses BYOA (for example the Geo-1 game in its "my agent wallet" mode) and press its button. The wallet starts by itself when a page asks; the extension's toolbar button shows whether it is running and opens it.

To update, quit the wallet from its tray icon (right click, **Quit**) and run `install.ps1` again.

To remove it: `powershell -ExecutionPolicy Bypass -File wallet\uninstall.ps1` removes exactly the three registry keys (and the Startup shortcut, if any); `-WhatIf` shows what it would do. It deletes no files: it prints the folders the wallet used so that you can remove them yourself. Remove the extension in `chrome://extensions`.

## Where things are

| What | Where |
|---|---|
| App and host | `%LOCALAPPDATA%\BYOA\wallet-app` (`Byoa.Wallet.exe`, `byoa-host.exe`, `org.bringyourownagent.wallet.json`) |
| Settings | `%LOCALAPPDATA%\BYOA\wallet\settings.json` (sites, sources, routes, limits; no secrets) |
| API keys | `%LOCALAPPDATA%\BYOA\wallet\secrets.dat` (DPAPI, current user) |
| Pairings of direct links | `%LOCALAPPDATA%\BYOA\wallet\pairings.dat` (DPAPI, current user; only for sites with "decide on my own") |
| Activity | `%LOCALAPPDATA%\BYOA\wallet\activity-YYYY-MM-DD.jsonl` (no audio, no text) |
| Downloaded models | `%LOCALAPPDATA%\BYOA\models` |
| Model folders searched | the models folder above, plus `<drive>:\AI-MODELS`, `<drive>:\models` and `%USERPROFILE%\models` when they exist; add more in **Sources** |

`BYOA_WALLET_HOME` points the wallet (and the host it is started by) at a separate data folder and pipe; the tests use it so that they never touch your own settings.

## The windows

- **The notice**, above the tray, only after a click on the page: "*site* wants to use your AI" with **Show**, **Decline** and **Block this site**. It does not take the focus from the browser.
- **The consent window**: the site (verified by the browser), its purpose in its own words, one row per skill with the source the manager suggests for each language (and **why?**), the budget per session (**change**), "Next time, decide on my own within these limits", **Decline**, **Allow** and **Detailed settings…**. It takes the focus; Tab moves through it and Esc declines. When an allowed site asks for more, only the difference is shown.
- **Settings**, from the tray icon or **Detailed settings…**: Sites (grants, revoke, block or unblock, decide on its own), Sources (local models found, model folders, the Whisper model and its download, API keys), Skills & fallbacks (the route, ask before paid, the manager's reasons), Limits (per session, per site per day in money and calls, the whole wallet per day), Activity (every request and call: time, site, skill, the source that answered, the fallback route, cost).
- **Paid fallback question**: "Use Scribe v2 (OpenRouter) for this clip? About $0.0001": **Allow once**, **Always for this site**, **No**.
- A request without a click shows nothing: the tray icon gets a quiet dot and its tooltip says a site asked without a click.

## Build and test

```powershell
cd wallet
dotnet build Byoa.Wallet.slnx
dotnet test tests\Byoa.Wallet.Tests          # routing, limits, the gate, WAV, framing and the host relay, DPAPI
```

The end-to-end test drives a real Chromium (patchright) with the unpacked extension, the registered host, the installed wallet, real speech clips and the wallet's windows through UI Automation (one pass with real mouse clicks). Run `install.ps1` first, then:

```powershell
python tests\e2e\make_samples.py <samples> --voice ru=<piper ru voice dir> --voice sk=<piper sk voice dir> [--recorded <ru WAVs with .txt>]
python tests\e2e\e2e.py --samples <samples> --shots <screenshots> --work <empty folder>
```

It needs `patchright`, `uiautomation`, `pillow`, and for the samples `sherpa-onnx` and `numpy`. With an OpenRouter key in `%USERPROFILE%\.openrouter-key` it makes two paid calls (about $0.0001); `--no-paid` skips them.

The direct links have their own end-to-end test against small fake sites (`tests\e2e\fake_site.py`, aiohttp) on `ws://localhost` under the developer switch `BYOA_WALLET_DEV_LOCAL=1`: links opened as Windows opens them (`cmd /c start`), the real windows, real speech, a wallet restart with a stored pairing, decline, block, a bad session, revoke, and one click in a headed Chromium:

```powershell
python tests\e2e\direct_e2e.py --samples <samples> --shots <screenshots> --work <empty folder>
```

## Layout

```
core/Byoa.Wallet.Core        protocol (framing, messages, paths), the engine, settings, DPAPI secrets, WAV, routing,
                             the direct transport (links, address checks, the WebSocket channel, pairings)
core/Byoa.Wallet.Providers   GigaAM (sherpa-onnx), Whisper (Whisper.net), OpenRouter; the model scanner
desktop/Byoa.Wallet.Desktop  the Avalonia tray app: notice, consent, settings, the pipe server
host/Byoa.Wallet.Host        byoa-host.exe, the native messaging host
extension/                   the MV3 extension: page.js (MAIN world), bridge.js (isolated world), background.js, popup
tests/                       unit tests and the end-to-end test
install.ps1, uninstall.ps1
```

Packages: Avalonia 11.3 (MIT), Whisper.net 1.9 (MIT), org.k2fsa.sherpa.onnx 1.13 (Apache-2.0), BringYourOwnAgent 0.0.2 (Apache-2.0), System.Security.Cryptography.ProtectedData (MIT); xunit (Apache-2.0) for the tests. The Barlow fonts are under the SIL Open Font License (`desktop/Byoa.Wallet.Desktop/Assets/Fonts/OFL.txt`). Model weights are not in the repository: Whisper's ggml files come from Hugging Face (MIT), GigaAM from the visitor's own folder.
