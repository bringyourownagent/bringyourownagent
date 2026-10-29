BYOA agent wallet {VERSION} for Windows (x64)
==========================================

The agent wallet lets web pages and your own AI agent use YOUR OWN AI, within limits you set. The wallet holds no
model itself: it is a go-between. It passes a site's request to a program on your computer that runs a model (the
Llama app, LM Studio, Ollama, or any OpenAI-compatible server) for speech to text and for text generation, or to
your own OpenRouter key as a paid fallback that asks you first. A site asks only after your click; you see a
short notice above the tray, then a consent window, and you allow or decline. A site that you decline or block
hears only "AI unavailable", exactly as if you had no wallet.

For speech to text, run a speech model in that program (Qwen3-ASR in the Llama app works well), then add it in the
wallet: Sources > Add a model > Speech to text.

More: https://bringyourownagent.org/wallet
Source code: https://github.com/bringyourownagent/bringyourownagent/tree/main/wallet

Needs: Windows 10 or 11, 64-bit. Nothing else: the .NET runtime is included.
For pages that use the browser extension: Chrome or another Chromium browser. Sites that start the wallet with a
bringyourownagent:// link need no extension at all.


Install
-------

1. Unzip the whole package (right click the zip file, "Extract All...").
2. Open the folder byoa-wallet-{VERSION}-win-x64 inside the folder that Extract All made (the zip holds one folder),
   and double-click Install.cmd in it.
   It copies the wallet into %LOCALAPPDATA%\BYOA\wallet-app, adds "Agent wallet" to the Start menu, registers, for
   your Windows user only:
     - the native messaging host for Chrome and Chromium (so the extension can reach the wallet),
     - the bringyourownagent:// links (so a site can start the wallet without the extension),
   and opens the wallet.
   To start the wallet with Windows as well, run in this folder:  Install.cmd -StartWithWindows
3. Optional, for sites that use the extension: open chrome://extensions, turn on "Developer mode", press
   "Load unpacked" and choose the folder the installer prints (%LOCALAPPDATA%\BYOA\wallet-app\extension).
4. Add a model: in the wallet open My models. "Sign in with OpenRouter" (a free account, many models), a free demo
   model, or a model on this PC (the Llama app https://llama.app, LM Studio or Ollama) with Add a model.

The wallet starts by itself when a site asks for it, and opens from the Start menu (Agent wallet). Its icon lives in
the tray; a click opens the settings
(Sites, Sources, Skills & fallbacks, Limits, Activity, Agents, and Connect your AI agent). The package also carries
byoa-mcp.exe. Add its path from the Connect page to Claude Code, Codex, or OpenCode; the wallet asks before an agent
can manage it.

Windows: this build is not code-signed yet. When you run Install.cmd, Windows may first show "Open File - Security
Warning" ("Unknown Publisher"): check the SHA-256, then click Run. Windows SmartScreen may also say "Windows
protected your PC" when you run Install.cmd or the wallet for the first time. Choose "More info", then "Run anyway". Before that, you can
compare the zip file's SHA-256 with the one on https://bringyourownagent.org/wallet
(PowerShell, in the folder where you saved the download: Get-FileHash .\byoa-wallet-win-x64.zip).

To update: quit the wallet from its tray icon (right click, Quit), unzip the new package, run its Install.cmd.


What it keeps, and where
------------------------

  App:          %LOCALAPPDATA%\BYOA\wallet-app
  Settings:     %LOCALAPPDATA%\BYOA\wallet\settings.json (sites, sources, routes, limits; no secrets)
  API keys:     %LOCALAPPDATA%\BYOA\wallet\secrets.dat (Windows DPAPI, your account only; never shown again)
  Activity:     %LOCALAPPDATA%\BYOA\wallet\activity-*.jsonl (time, site, skill, source, cost; no audio, no text)

The wallet never stores the audio or the recognised text, and a site never sees your keys.


Uninstall
---------

1. Quit the wallet from its tray icon (right click, Quit).
2. Run Uninstall.cmd (here, or in %LOCALAPPDATA%\BYOA\wallet-app). It removes the registry keys and the Startup
   shortcut the installer made, and deletes no files: it prints the folders above so that you can remove them
   yourself. Uninstall.cmd -WhatIf only shows what it would do.
3. Remove the extension in chrome://extensions, if you loaded it.


License
-------

Apache License 2.0 (LICENSE). Third-party components and their licenses: THIRD-PARTY-NOTICES.txt.
