# BYOA agent wallet 0.1 (Windows and macOS)

The first working agent wallet for [BYOA](../README.md): a tray app on your computer, a small native messaging host and a Chrome extension. A web page asks for your AI with `window.byoa`; the wallet shows a short notice, then a short consent window, and answers the page's calls with **your** models: a model in a program on your computer, a compatible home server, or your own API key as a paid fallback that asks first.

**The wallet holds no model.** It is a go-between: a site asks, you allow, and the wallet passes the request to a program that runs a model (the [Llama app](https://llama.app), LM Studio, Ollama, or any server with an OpenAI-compatible `/chat/completions`). The wallet downloads, loads and bundles nothing of its own.

0.1 serves `audio.transcribe` (speech to text) and `text.generate`. Both are served by the same kind of source, which you add in **My models > Add a model**:

| Source | Runs | Skill | Cost | Notes |
|---|---|---|---|---|
| A model in the Llama app, LM Studio, Ollama or another OpenAI-compatible server | this PC, or a home server | `text.generate`, or `audio.transcribe` | no cloud bill | one added source serves one skill; add a model twice to serve both |
| ElevenLabs Scribe v2 through OpenRouter | your API key | `audio.transcribe` | paid, about $0.00004 a second | always in the wallet; the last fallback; asks before it runs |

Speech to text goes to the program as a chat request with the audio in it (`input_audio`), so it needs a model that hears audio. We tested Qwen3-ASR (1.7B) in the Llama app: about 0.15-0.9 s a clip on a GPU, good Russian and English. Run it in Llama, then add it with skill **Speech to text**. A model that cannot hear audio fails the test in the Add window with the program's own reason.

**No model yet?** The **Get started** card at the top of My models (and of Sites, while the wallet has no text model at all) offers three free ways in. A model a program on this PC already serves: the card lists what the scan found, with an Add per model; when nothing answers it shows a Get Llama link (it only opens llama.app). OpenRouter with a free account: the sign-in runs in your browser (OAuth PKCE), the key comes back to the wallet over a listener on this computer only, is stored like any other key and never shown or logged, and the card then offers a few of OpenRouter's free models to tick. And two free public demo models (LLM7.io and OVHcloud AI Endpoints) that answer with no key and no sign-up: labelled **demo**, confirmed once in plain words (a public service may keep what you send; slow and rate-limited; not for private text), counted as cloud for the privacy gate, counted as free for money, and off for sites until you tick one in that site's window. Help → Get started has the details; "Show all settings" at the bottom of the tab list unhides the rest of the tabs.

A new wallet's speech route holds only the paid fallback; a speech model you add goes before it. Text models are added to the text route, local ones before paid ones. You can reorder either route, switch sources off and decide whether the paid one asks. A source whose program is not running is skipped with the reason "not running", and the next stop answers.

The design and the security notes are in [docs/wallet-0.1.md](../docs/wallet-0.1.md). The page API is the BYOA 0.1 contract: `window.byoa.request(manifest)`, `call(grantId, skill, input, params)`, `release(grantId)`.

**Without the extension.** A site can also show a `bringyourownagent://connect?...` link. A click on it starts the wallet (or hands the link to the running one); the wallet reads the site's manifest from the link itself, goes straight to the consent window (no notice first; for a site whose manifest says `"connect": { "check": true }` it shows the four-character check code that the site's page shows too, and Allow waits for the visitor's tick that they match), and only after Allow contacts the site at all: it opens its own WebSocket to the site's server, which then sends its calls straight to the wallet. Same grants, limits and routing; the activity marks these "via link". Details: [docs/wallet-0.1.md, section 10](../docs/wallet-0.1.md#10-transports).

## Manage the wallet from your AI agent

The package includes `byoa-mcp.exe`, a small MCP server for Claude Code, Codex, OpenCode, and other MCP clients. Open **Settings → My MCP**, copy the command or config snippet, and keep the wallet running. The bridge uses a per-user named pipe tied to `BYOA_WALLET_HOME`; it does not start the wallet and it does not hold settings or secrets.

On the first connection the wallet shows the agent's announced name and the parent executable path observed by the wallet. **Allow** gives source, route, test and activity tools. **Full access** is a separate visitor choice, and it is off by default. It unlocks `list_sites`, `set_site_access`, `set_limits` and `set_api_key`, and it lets `enable_source` change a built-in source and `remove_source` remove a source with a saved key. No tool allows a site, approves a paid call or approves another agent. Settings → My MCP shows last use, the Full access switch, and Revoke. A program running as you can also start the bridge itself, so read the observed path before allowing it, and allow only the agents you trust. API keys are write-only: prefer `request_api_key_entry`, where the visitor types the key in the wallet. The wallet never sends a key back to the agent.

The MCP entry point is `help`; it returns the same embedded articles shown by the wallet's Help page. `status`, `list_runtimes`, `add_source`, `test_source`, `set_order`, and `activity` cover the normal “add my local model” path. If the wallet is not running, the bridge says so and never starts it.

Your own AI agent can also work for a site, not only for you: **My agents > Add an agent** adds Claude Code (or a launcher built on it) as a source that a site gets only when you tick it for that site. It runs one question at a time with web search and page reading only, and every request passes the **Agent guard** (a small yes-or-no model, Jev, behind your OpenRouter key) first; what looks wrong waits for you. See [docs/agent-source.md](../docs/agent-source.md).

The same registry backs My models, Help, and MCP. API keys are write-only: the preferred `request_api_key_entry` tool opens a field in the wallet so the visitor enters the key himself; the agent receives only a saved confirmation.

## Everywhere: dictation and a check of selected text

The **Everywhere** page (Settings) lets your own models work in every program, not only on sites. It has two features with a switch each, **both off** until you turn them on, because they listen for a hotkey in every program and send keystrokes to the program you are in (the first time you turn one on, the page explains it in two sentences).

- **Dictation**: press `Ctrl+Alt+Space` (changeable), speak, and the words are typed into the field that has the focus. *Hold to talk* records while the hotkey is held and types on release; *press to start, press to stop* toggles. Esc drops a recording; one recording lasts at most 60 seconds; the language is automatic or fixed. A small indicator says "Listening…", "Writing…" or what went wrong. The recording goes to your own speech model by your speech route, as a site's audio would.
- **The check**: select text anywhere and press `Ctrl+Alt+K` (changeable). The wallet asks your own text model (never an agent) to do one *action* on the selection and shows the answer next to the cursor with **Copy**, **Replace selection** and **Close**. The actions are yours to edit (a name and an instruction; "Check facts and wording" and "Translate to English" come with the wallet). A terminal window is not read, because Ctrl+C there would stop the program running in it.

These are the wallet's **own calls**: no site, no consent, but the same gate, routes, fallbacks and limits (they show in Activity as "Everywhere" and count in the day's limits), and no content in the log: never the sound, the words, the selected text or the answer. By default only models on this computer or a home server are used; **Let Everywhere use a paid source** allows a paid fallback, and the wallet asks before each paid call. A hotkey is checked before it is kept (not a shortcut Windows and most programs use, not the other feature's, not held by another program such as a second dictation program); one that clashes is reported and not listened to. The words are pasted (the clipboard is saved, kept clear of the clipboard history and put back; if it is busy the wallet types the characters instead). The keyboard hook is installed only while a feature is on. Windows only; settings under `everywhere` in `settings.json`; more in Help, Everywhere.

## Install from the installer

Needs Windows 10 or 11, 64-bit; nothing else (the installer carries its own .NET runtime). Chrome or another Chromium browser only for sites that use the extension: sites that start the wallet with a `bringyourownagent://` link need no extension.

1. Download `AgentWallet-win-Setup.exe` from <https://bringyourownagent.org/wallet> and compare its SHA-256 with the one on that page (PowerShell, in the folder where you saved it: `Get-FileHash .\AgentWallet-win-Setup.exe`).
2. Run it. It installs for your Windows user only (no administrator rights) into `%LOCALAPPDATA%\AgentWallet\current`, adds **Agent wallet** to the Start menu, registers the browser helper (Chrome and Chromium) and the `bringyourownagent://` links, and opens the wallet. The installer is Velopack's `Setup.exe`; it is not code-signed yet, so Windows may show "Unknown Publisher" or SmartScreen's "Windows protected your PC": check the SHA-256, then **Run** (or **More info**, **Run anyway**).
3. Optional, for sites that use the extension: `chrome://extensions`, **Developer mode**, **Load unpacked**, and choose `%LOCALAPPDATA%\AgentWallet\current\extension`.

**To update**, open the wallet and press **Check for updates** at the bottom of the tab list (or **Check for updates** in the tray menu). If the site has a newer version, press **Update and restart**: the wallet downloads it (usually a delta of a few hundred KB), closes for a few seconds and comes back as the new version, with its settings. The wallet asks `https://bringyourownagent.org/downloads/win/` only when you press the button, never by itself. A copy that Setup.exe did not install (a build from the source, the earlier zip) shows **Get the latest version**, which opens the download page. **Start with Windows** is a tick box in the same place.

**To remove it**: Settings, Apps, Installed apps, **Agent wallet**, Uninstall. It removes the program and the registry keys, and deletes none of your data (`%LOCALAPPDATA%\BYOA\wallet`: settings, keys, activity log).

Earlier releases came as a zip with `Install.cmd` (into `%LOCALAPPDATA%\BYOA\wallet-app`). Quit that wallet from its tray icon (right click, **Quit**), then run the installer once over it: the settings stay (they live in `%LOCALAPPDATA%\BYOA\wallet`), the registry keys now point to the new place, and the old folder is not used any more.

To build the installer yourself: `powershell -ExecutionPolicy Bypass -File wallet\packaging\make-package.ps1 -Installer [-OutDir <empty folder>]` (needs the .NET 10 SDK and the `vpk` tool: `dotnet tool install -g vpk`). It publishes the app, the host and the MCP bridge self-contained for win-x64 under the output folder (never into the repository's `bin` and `obj`), stages the package, packs it with Velopack into `<OutDir>\release` (`AgentWallet-win-Setup.exe`, the full package `AgentWallet-<version>-win-full.nupkg`, `releases.win.json` and, with `-WithDeltas`, a delta from the version on the site), and also zips the staged package with its SHA-256 (`<zip>.sha256`). The version is `<Version>` in `wallet\Directory.Build.props`; a release must have a higher one than the last.

## Install on a Mac (Apple silicon)

Needs macOS 12 or later on an M1 or newer; nothing else (the app carries its own .NET runtime). The wallet is the same program as on Windows, with the system parts replaced: it lives in the menu bar (no Dock icon), keeps its API keys sealed under a key in your login Keychain (the counterpart of Windows DPAPI), talks to the native host and the MCP bridge over Unix sockets in `~/Library/Application Support/BYOA/run` (only you can open that folder), opens `bringyourownagent://` links through `Info.plist`, and tells every Chromium browser you have installed where its native host is. **Everywhere** (dictation and the check of selected text) is Windows only for now; its page is hidden on the Mac.

1. Download the zip from <https://bringyourownagent.org/wallet> (the page names it `byoa-wallet-mac-arm64.zip`) and compare its SHA-256 with the one on that page (`shasum -a 256 ~/Downloads/byoa-wallet-mac-arm64.zip`).
2. Unzip it and drag **Agent Wallet.app** into Applications; open it once. The build is signed with a Developer ID and notarized by Apple, so it opens without a warning.
3. Optional, for sites that use the extension: `chrome://extensions`, **Developer mode**, **Load unpacked**, and choose `~/Library/Application Support/BYOA/extension` (Command-Shift-G in the file dialog). The wallet puts a copy of the extension there each time it starts. The extension ID is the same as on Windows.

To update, quit the wallet from its menu-bar icon and replace the app. To remove it, drag the app to the Trash; the data folder `~/Library/Application Support/BYOA`, the Keychain item and the host manifests stay for you to delete yourself (the README in the package lists them).

To build the package yourself, on a Mac with the .NET 10 SDK: `wallet/packaging/make-macos-package.sh --out <new folder> [--sign none|adhoc|identity] [--notarize]`. It publishes the three programs self-contained for osx-arm64 as single files, builds the bundle, checks it, signs it (with a Developer ID identity named by `BYOA_SIGN_IDENTITY`; `--notarize` also sends it to Apple and staples the ticket), zips it and prints the SHA-256. The tests: the unit tests run on a Mac too (`dotnet test`); `tests/e2e/mac_smoke.py` drives the built app and its real host and bridge, and `tests/e2e/mac_chrome_e2e.mjs` drives a real Chrome with the extension through the real host (both run on a data folder and a browser profile of their own).

## Install from the source code

Needs Windows 10 or 11, the .NET 10 SDK and Chrome (or another Chromium browser).

1. In PowerShell, from the repository root:

   ```powershell
   powershell -ExecutionPolicy Bypass -File wallet\install.ps1
   ```

   It publishes the wallet and the host into `%LOCALAPPDATA%\BYOA\wallet-app`, writes the host manifest there and registers the host for your user only (`HKCU\Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet` and the same under `Software\Chromium`), and the `bringyourownagent://` links (`HKCU\Software\Classes\bringyourownagent`). Add `-StartWithWindows` to put a shortcut into your Startup folder (off by default).
2. In Chrome open `chrome://extensions`, turn on **Developer mode**, press **Load unpacked** and choose the folder `wallet\extension` that install.ps1 prints. The extension ID is always `niobopdoiopjilmlonjcgllpmgjemhpk` (fixed by the `key` in its manifest), which is the only origin the host accepts.
3. Optional: add an OpenRouter key for the paid fallback in the wallet's **My models** page (or `Byoa.Wallet.exe --import-key openrouter <file with the key>`). It is stored with Windows DPAPI for your account and never shown again.
4. Open a page that uses BYOA (for example the Geo-1 game in its "my agent wallet" mode) and press its button. The wallet starts by itself when a page asks; the extension's toolbar button shows whether it is running and opens it.

To update, quit the wallet from its tray icon (right click, **Quit**) and run `install.ps1` again.

To remove it: `powershell -ExecutionPolicy Bypass -File wallet\uninstall.ps1` removes exactly the three registry keys (and the Startup shortcut, if any); `-WhatIf` shows what it would do. It deletes no files: it prints the folders the wallet used so that you can remove them yourself. Remove the extension in `chrome://extensions`.

## Where things are

| What | Where |
|---|---|
| App, host and MCP bridge | `%LOCALAPPDATA%\AgentWallet\current` (the installer; a source install or the earlier zip: `%LOCALAPPDATA%\BYOA\wallet-app`) (`Byoa.Wallet.exe`, `byoa-host.exe`, `byoa-mcp.exe`, `org.bringyourownagent.wallet.json`, `extension\`) |
| Settings | `%LOCALAPPDATA%\BYOA\wallet\settings.json` (sites, sources, routes, limits; no secrets) |
| API keys | `%LOCALAPPDATA%\BYOA\wallet\secrets.dat` (DPAPI, current user) |
| Pairings of direct links | `%LOCALAPPDATA%\BYOA\wallet\pairings.dat` (DPAPI, current user; only for sites with "decide on my own") |
| Activity | `%LOCALAPPDATA%\BYOA\wallet\activity-YYYY-MM-DD.jsonl` (no audio, no text) |
| Error log | `%LOCALAPPDATA%\BYOA\wallet\errors.log` (exception types and stacks only) |

`BYOA_WALLET_HOME` points the wallet (and the host it is started by) at a separate data folder and pipe; the tests use it so that they never touch your own settings.

## The windows

An extension request is marked verified because the browser vouches for its origin. A direct-link request says "came by a link: confirmed after you allow". A link frame without browser activation is held quietly, just like a page request without a click; a normal browser click keeps the notice and consent flow, also for a site with "decide on my own".

- **The notice**, above the tray (on a Mac, below the menu bar), only after a click on the page and only for a request through the extension: "*site* wants to use your AI" with **Show**, **Decline** and **Block this site**. It does not take the focus from the browser. A direct link has no notice: it opens the consent window at once.
- **The consent window**: the site (verified by the browser for extension requests; a direct link is marked as a link, shows the check code when the site's manifest asks for it, and its Allow is kept only once the site's welcome echoes the link), its purpose in its own words, one row per skill with the source the manager suggests for each language (and **why?**), the budget per session (**change**), "Next time, decide on my own within these limits", **Decline**, **Allow** and **Detailed settings…**. It takes the focus; Tab moves through it and Esc declines. When an allowed site asks for more, only the difference is shown.
- **Settings**, from the tray icon or **Detailed settings…**; at first only Sites, My models and Activity (and Everywhere on Windows), and **Show all settings** adds My agents, Agent guard, Skills & fallbacks, Limits, My MCP and Help: Sites (grants, pause, revoke, block or unblock, decide on its own; a dot per site: green when it used the wallet in the last 3 minutes, yellow within a day, grey before that), My models (the models you added and the programs they run in, the paid speech key), Skills & fallbacks (the route, ask before paid, the manager's reasons), Everywhere (dictation into any text field and the check of selected text, both off until you turn them on), Limits (per session, per site per day in money and calls, the whole wallet per day), Activity (every request and call: time, site, skill, the source that answered, the fallback route, cost; grouped by day, today and the days before it, 50, 100 or 200 entries a page).
- **Pause.** A site can be paused in Sites, and an allowed site that goes unused for a day (the time is set at the top of Sites: never, an hour, a day or a week) is paused by the wallet. A paused site keeps what you allowed, but nothing runs for it on its own: its open sessions end, it does not reconnect by itself, calls on its old grants are refused. Its next request, after your click, shows the notice and then the consent window with "You allowed this site before. Paused ... Resume it?" and **Resume**.
- **Paid fallback question**: "Use Scribe v2 (OpenRouter) for this clip? About $0.0001": **Allow once**, **Always for this site**, **No**.
- A request without a click shows nothing: the tray icon gets a quiet dot and its tooltip says a site asked without a click. A page may make 30 of them a minute; the rest are ignored and not written down.
- If `secrets.dat` cannot be read (made by another Windows account, or damaged), the wallet works as if it held no keys: local models need none. The first key you enter again keeps the unreadable file next to the new one as `secrets.dat.unreadable-<time>`.

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

It needs `patchright`, `uiautomation`, `pillow`, and for the samples `sherpa-onnx` and `numpy` (only to make the test clips with Piper voices; the wallet itself uses neither). The real-speech passes need a speech model running in the Llama app (or another OpenAI-compatible program) and added to the wallet. With an OpenRouter key in `%USERPROFILE%\.openrouter-key` it makes two paid calls (about $0.0001); `--no-paid` skips them.

The direct links have their own end-to-end test against small fake sites (`tests\e2e\fake_site.py`, aiohttp) on `ws://localhost` under the developer switch `BYOA_WALLET_DEV_LOCAL=1`: links opened as Windows opens them (`cmd /c start`), the real windows, real speech, a wallet restart with a stored pairing, decline, block, a bad session, revoke, and one click in a headed Chromium:

```powershell
python tests\e2e\direct_e2e.py --samples <samples> --shots <screenshots> --work <empty folder>
```

## Layout

```
core/Byoa.Wallet.Core        protocol (framing, messages, paths), the engine, settings, DPAPI secrets, WAV, routing,
                             the direct transport (links, address checks, the WebSocket channel, pairings),
                             Everywhere (hotkeys, the dictation and check state machines, the wallet's own calls)
core/Byoa.Wallet.Providers   the sources: a model in an OpenAI-compatible program (text and speech), OpenRouter; the runtime scanner
desktop/Byoa.Wallet.Desktop  the Avalonia tray app: notice, consent, settings, the pipe server; Everywhere's Windows parts
                             (keyboard hook, SendInput, clipboard, microphone, the indicator and the check window)
host/Byoa.Wallet.Host        byoa-host.exe, the native messaging host
mcp/Byoa.Wallet.Mcp          byoa-mcp.exe, the stdio MCP bridge for the visitor's own AI agent
extension/                   the MV3 extension: page.js (MAIN world), bridge.js (isolated world), background.js, popup
tests/                       unit tests and the end-to-end test
packaging/                   make-package.ps1 (the zip, and with -Installer the Velopack installer and update feed), Install.cmd, Uninstall.cmd, README.txt and
                             THIRD-PARTY-NOTICES.txt of the package
install.ps1, uninstall.ps1   install from the source, or (next to app\ in the package) from the package
```

Packages: Avalonia 11.3 (MIT), BringYourOwnAgent 0.0.2 (Apache-2.0), System.Security.Cryptography.ProtectedData (MIT); xunit (Apache-2.0) for the tests. The Barlow fonts are under the SIL Open Font License (`desktop/Byoa.Wallet.Desktop/Assets/Fonts/OFL.txt`). The wallet carries no model weights and no inference engine.
