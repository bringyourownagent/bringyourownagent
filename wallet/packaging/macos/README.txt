BYOA agent wallet {VERSION} for macOS (Apple silicon)
=====================================================

The agent wallet lets web pages and your own AI agent use YOUR OWN AI, within limits you set. The wallet holds no
model itself: it is a go-between. It passes a site's request to a program on your computer that runs a model (the
Llama app, LM Studio, Ollama, or any OpenAI-compatible server) or to one of your own agents (Claude Code, for
example), or to your own OpenRouter key as a paid fallback that asks you first. A site asks only after your click; you
see a short notice, then a consent window, and you allow or decline. A site that you decline or block hears only
"AI unavailable", exactly as if you had no wallet.

More: https://bringyourownagent.org/wallet
Source code: https://github.com/bringyourownagent/bringyourownagent/tree/main/wallet

Needs: macOS 12 or later on a Mac with Apple silicon (M1 and newer). Nothing else: the .NET runtime is included.
For pages that use the browser extension: Chrome or another Chromium browser (Edge, Brave, Vivaldi, Arc, Opera).
Sites that start the wallet with a bringyourownagent:// link need no extension at all.


Install
-------

1. Unzip the package and drag "Agent Wallet.app" into your Applications folder.
2. Open it once (double-click). The wallet lives in the menu bar (top right of the screen), with no Dock icon; a click on
   its icon opens the settings (Sites, My models, My agents, Agent guard, Limits, Activity, My MCP, Help).
   The first time the wallet keeps a key, macOS may ask whether it may use the Keychain: choose "Always Allow".
3. Optional, for sites that use the extension: in Chrome open chrome://extensions, turn on "Developer mode", press
   "Load unpacked" and choose the folder  ~/Library/Application Support/BYOA/extension  (in the file dialog press
   Command-Shift-G and type that path). The wallet puts a copy of the extension there each time it starts, and tells
   every browser you have installed where its native host is, so nothing else needs to be set up.
4. Add a model: install the Llama app (https://llama.app), let it load a model, then in the wallet open "My models"
   and press "Add a model". Optional: add an OpenRouter key for the paid fallback.

The wallet starts by itself when a site asks for it (a bringyourownagent:// link, or the extension). To have it start
when you log in: System Settings > General > Login Items > add "Agent Wallet".

Dictation and the check of selected text in every program ("Everywhere") work on Windows only for now; the page is
hidden on the Mac.

Gatekeeper: a copy that was signed and notarized by its maker opens without a warning. If macOS says it cannot check the
app for malicious software, compare the zip's SHA-256 with the one on https://bringyourownagent.org/wallet
(in Terminal:  shasum -a 256 ~/Downloads/byoa-wallet-mac-arm64.zip), and only then choose Open (or System Settings >
Privacy & Security > "Open Anyway").

To update: quit the wallet from its menu-bar icon (Quit), replace "Agent Wallet.app" in Applications with the new one,
open it.


Your agent can manage the wallet (My MCP)
-----------------------------------------

The app carries the bridge program byoa-mcp. The "My MCP" page of the wallet shows its path and the one command to add it
to Claude Code, Codex or OpenCode; the wallet asks you before an agent may manage it.
Inside the app the bridge is  Agent Wallet.app/Contents/MacOS/byoa-mcp.


What it keeps, and where
------------------------

  App:          /Applications/Agent Wallet.app
  Settings:     ~/Library/Application Support/BYOA/wallet/settings.json (sites, sources, routes, limits; no secrets)
  API keys:     ~/Library/Application Support/BYOA/wallet/secrets.dat, sealed under a key in your login Keychain
                (Keychain Access: "BYOA agent wallet: key of the secrets file"); never shown again
  Activity:     ~/Library/Application Support/BYOA/wallet/activity-*.jsonl (time, site, skill, source, cost; no audio, no text)
  Extension:    ~/Library/Application Support/BYOA/extension
  Sockets:      ~/Library/Application Support/BYOA/run (only you can open it)
  Browsers:     ".../NativeMessagingHosts/org.bringyourownagent.wallet.json" in the folder of each Chromium browser

The wallet never stores the audio or the recognised text, and a site never sees your keys.


Uninstall
---------

1. Quit the wallet from its menu-bar icon (Quit).
2. Drag "Agent Wallet.app" to the Trash. Nothing else is needed; the files above stay for you to delete yourself if you
   want them gone: the folder ~/Library/Application Support/BYOA, the Keychain item named above, and the file
   org.bringyourownagent.wallet.json in the NativeMessagingHosts folder of your browser.
3. Remove the extension in chrome://extensions, if you loaded it.


License
-------

Apache License 2.0 (LICENSE). Third-party components and their licenses: THIRD-PARTY-NOTICES.txt and the licenses folder.
