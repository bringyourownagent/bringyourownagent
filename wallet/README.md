# BYOA agent wallet 0.1 (Windows)

The first working agent wallet for [BYOA](../README.md): a tray app on your computer, a small native messaging host and a Chrome extension. A web page asks for your AI with `window.byoa`; the wallet shows a short notice, then a short consent window, and answers the page's calls with **your** models: local speech recognition, local text generation, a compatible home server, or your own API key as a paid fallback that asks first.

0.1 serves `audio.transcribe` (speech to text) from built-in sources and `text.generate` from LM Studio, Ollama, another OpenAI-compatible endpoint, or OpenRouter. The same source registry manages both skills:

| Source | Runs | Languages | Cost | Notes |
|---|---|---|---|---|
| GigaAM v3 (sherpa-onnx) | this PC, CPU | Russian only | no cloud bill | very fast and accurate for Russian; uses a model folder already on disk |
| Whisper large-v3, q5_0 (Whisper.net / whisper.cpp) | this PC, GPU or CPU | 100 | no cloud bill | downloads once (1.1 GB) into the wallet's active `models\whisper` folder on first use, with progress shown |
| ElevenLabs Scribe v2 through OpenRouter | your API key | 99 | paid, about $0.00004 a second | the last fallback; asks before it runs |

The default route for speech is GigaAM for `ru`, Whisper for other languages and as the fallback, and OpenRouter last. Text sources are added to their own route when the visitor chooses them; local sources are tried before paid sources. You can reorder either route, switch sources off and decide whether the paid one asks.

The design and the security notes are in [docs/wallet-0.1.md](../docs/wallet-0.1.md). The page API is the BYOA 0.1 contract: `window.byoa.request(manifest)`, `call(grantId, skill, input, params)`, `release(grantId)`.

**Without the extension.** A site can also show a `bringyourownagent://connect?...` link. A click on it starts the wallet (or hands the link to the running one); the wallet reads the site's manifest from the link itself, shows the same notice and consent, and only after Allow contacts the site at all: it opens its own WebSocket to the site's server, which then sends its calls straight to the wallet. Same grants, limits and routing; the activity marks these "via link". Details: [docs/wallet-0.1.md, section 10](../docs/wallet-0.1.md#10-transports).

## Manage the wallet from your AI agent

The package includes `byoa-mcp.exe`, a small MCP server for Claude Code, Codex, OpenCode, and other MCP clients. Open **Settings → Connect your AI agent**, copy the command or config snippet, and keep the wallet running. The bridge uses a per-user named pipe tied to `BYOA_WALLET_HOME`; it does not start the wallet and it does not hold settings or secrets.

On the first connection the wallet shows the agent's announced name and executable path. **Allow** gives source, model, route, test, download, and activity tools. **Full access** is a separate visitor choice for site access, limits, paid approvals, and approving other agents; it is off by default. Settings → Agents shows last use, the Full access switch, and Revoke. API keys are write-only: prefer `request_api_key_entry`, where the visitor types the key in the wallet. The agent never receives it.

The MCP entry point is `help`; it returns the same embedded articles shown by the wallet's Help page. `status`, `list_runtimes`, `add_source`, `test_source`, `set_order`, and `activity` cover the normal “add my local model” path. If the wallet is not running, the bridge says so and never starts it.

`remove_model` is also available after Allow. It removes immediately on the visitor's request, without another prompt, but only a model that this wallet downloaded into its own active `models` folder. A model found in another folder, or a path outside the wallet home, is never removed. The action appears in Activity with the agent's name.

The same registry backs Sources, Help, and MCP. API keys are write-only: the preferred `request_api_key_entry` tool opens a field in the wallet so the visitor enters the key himself; the agent receives only a saved confirmation.

## Install from the ready package

Needs Windows 10 or 11, 64-bit; nothing else (the package carries its own .NET runtime). Chrome or another Chromium browser only for sites that use the extension: sites that start the wallet with a `bringyourownagent://` link need no extension.

1. Download `byoa-wallet-<version>-win-x64.zip` from <https://bringyourownagent.org/wallet> and compare its SHA-256 with the one on that page (PowerShell: `Get-FileHash .\byoa-wallet-0.1.0-win-x64.zip`).
2. Unzip the whole package (right click, **Extract All...**) and double-click **Install.cmd** in the unzipped folder (`Install.cmd -StartWithWindows` from a command prompt also adds a Startup shortcut). It runs the same `install.ps1` as below in its package mode: no build, it copies `app\` (the wallet, the host and the runtime) into `%LOCALAPPDATA%\BYOA\wallet-app`, the extension into `%LOCALAPPDATA%\BYOA\wallet-app\extension`, and the uninstaller next to them, then registers the same three keys for your user. The copies lose the "downloaded from the internet" mark, so Windows does not ask again each time a site starts the wallet. The unzipped folder is not needed afterwards.
3. The build is not code-signed yet, so Windows SmartScreen may say "Windows protected your PC" for Install.cmd or the wallet: choose **More info**, then **Run anyway**.
4. Optional, for sites that use the extension: `chrome://extensions`, **Developer mode**, **Load unpacked**, and choose the `extension` folder the installer prints.

To update, quit the wallet from its tray icon and run the new package's Install.cmd. To remove it, run `%LOCALAPPDATA%\BYOA\wallet-app\Uninstall.cmd` (or the one in the package): the same `uninstall.ps1` as below, which removes only the registry keys and the Startup shortcut and deletes no files.

To build the package yourself: `powershell -ExecutionPolicy Bypass -File wallet\packaging\make-package.ps1 [-OutDir <empty folder>]` (needs the .NET 10 SDK). It publishes the app and the host self-contained for win-x64 under the output folder (never into the repository's `bin` and `obj`), stages the package, zips it and prints its size and SHA-256 (also written to `<zip>.sha256`).

## Install from the source code

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
| App, host and MCP bridge | `%LOCALAPPDATA%\BYOA\wallet-app` (`Byoa.Wallet.exe`, `byoa-host.exe`, `byoa-mcp.exe`, `org.bringyourownagent.wallet.json`; from the package also `extension\`, `uninstall.ps1` and `Uninstall.cmd`) |
| Settings | `%LOCALAPPDATA%\BYOA\wallet\settings.json` (sites, sources, routes, limits; no secrets) |
| API keys | `%LOCALAPPDATA%\BYOA\wallet\secrets.dat` (DPAPI, current user) |
| Pairings of direct links | `%LOCALAPPDATA%\BYOA\wallet\pairings.dat` (DPAPI, current user; only for sites with "decide on my own") |
| Activity | `%LOCALAPPDATA%\BYOA\wallet\activity-YYYY-MM-DD.jsonl` (no audio, no text) |
| Downloaded models | `%LOCALAPPDATA%\BYOA\wallet\models` (under the active `BYOA_WALLET_HOME`) |
| Error log | `%LOCALAPPDATA%\BYOA\wallet\errors.log` (exception types and stacks only) |
| Model folders searched | the models folder above, plus `<drive>:\AI-MODELS`, `<drive>:\models` and `%USERPROFILE%\models` when they exist; add more in **Sources** |

`BYOA_WALLET_HOME` points the wallet (and the host it is started by) at a separate data folder and pipe; the tests use it so that they never touch your own settings.

## The windows

An extension request is marked verified because the browser vouches for its origin. A direct-link request says "came by a link: confirmed after you allow". A link frame without browser activation is held quietly, just like a page request without a click; a normal browser click keeps the notice and consent flow.

- **The notice**, above the tray, only after a click on the page: "*site* wants to use your AI" with **Show**, **Decline** and **Block this site**. It does not take the focus from the browser.
- **The consent window**: the site (verified by the browser for extension requests; direct links are marked as links until Allow), its purpose in its own words, one row per skill with the source the manager suggests for each language (and **why?**), the budget per session (**change**), "Next time, decide on my own within these limits", **Decline**, **Allow** and **Detailed settings…**. It takes the focus; Tab moves through it and Esc declines. When an allowed site asks for more, only the difference is shown.
- **Settings**, from the tray icon or **Detailed settings…**: Sites (grants, pause, revoke, block or unblock, decide on its own; a dot per site: green when it used the wallet in the last 3 minutes, yellow within a day, grey before that), Sources (local models found, model folders, the Whisper model and its download, API keys), Skills & fallbacks (the route, ask before paid, the manager's reasons), Limits (per session, per site per day in money and calls, the whole wallet per day), Activity (every request and call: time, site, skill, the source that answered, the fallback route, cost; grouped by day, today and the days before it, 50, 100 or 200 entries a page).
- **Pause.** A site can be paused in Sites, and an allowed site that goes unused for a day (the time is set at the top of Sites: never, an hour, a day or a week) is paused by the wallet. A paused site keeps what you allowed, but nothing runs for it on its own: its open sessions end, it does not reconnect by itself, calls on its old grants are refused. Its next request, after your click, shows the notice and then the consent window with "You allowed this site before. Paused ... Resume it?" and **Resume**.
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
mcp/Byoa.Wallet.Mcp          byoa-mcp.exe, the stdio MCP bridge for the visitor's own AI agent
extension/                   the MV3 extension: page.js (MAIN world), bridge.js (isolated world), background.js, popup
tests/                       unit tests and the end-to-end test
packaging/                   make-package.ps1 (the ready-to-install zip), Install.cmd, Uninstall.cmd, README.txt and
                             THIRD-PARTY-NOTICES.txt of the package
install.ps1, uninstall.ps1   install from the source, or (next to app\ in the package) from the package
```

Packages: Avalonia 11.3 (MIT), Whisper.net 1.9 (MIT), org.k2fsa.sherpa.onnx 1.13 (Apache-2.0), BringYourOwnAgent 0.0.2 (Apache-2.0), System.Security.Cryptography.ProtectedData (MIT); xunit (Apache-2.0) for the tests. The Barlow fonts are under the SIL Open Font License (`desktop/Byoa.Wallet.Desktop/Assets/Fonts/OFL.txt`). Model weights are not in the repository: Whisper's ggml files come from Hugging Face (MIT), GigaAM from the visitor's own folder.
