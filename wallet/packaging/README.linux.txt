BYOA agent wallet {VERSION} for Linux (x64)
============================================

The agent wallet lets web pages and your own AI agent use YOUR OWN AI, within limits you set. The wallet holds no
model itself: it is a go-between. It passes a site's request to a program on your computer that runs a model (the
Llama app, LM Studio, Ollama, or any OpenAI-compatible server) or to one of your own agents (Claude Code, for
example), or to your own OpenRouter key as a paid fallback that asks you first. A site asks only after your click; you
see a short notice, then a consent window, and you allow or decline. A site that you decline or block hears only
"AI unavailable", exactly as if you had no wallet.

More: https://bringyourownagent.org/wallet
Source code: https://github.com/bringyourownagent/bringyourownagent/tree/main/wallet

Needs: an x86-64 Linux desktop (Ubuntu 24.04 and 26.04 were tested; glibc 2.35 or later), with X11 or Wayland (the
window uses XWayland when the session is Wayland) and fontconfig. Nothing else: the .NET runtime is included. For
pages that use the browser extension: Chrome or another Chromium browser (Edge, Brave, Vivaldi, Opera, or the snap
of Chromium). Sites that start the wallet with a bringyourownagent:// link need no extension at all.


Install
-------

1. In the folder where you downloaded the package, check the hash and unpack it:

     sha256sum -c byoa-wallet-{VERSION}-linux-x64.tar.gz.sha256
     tar -xzf byoa-wallet-{VERSION}-linux-x64.tar.gz
     cd byoa-wallet-{VERSION}-linux-x64

2. Run ./install.sh        (add --start-at-login to start the wallet when you log in). It installs for your user only
   and never asks for root: the programs go to ~/.local/share/AgentWallet/versions/{VERSION}, a link named current
   beside them points at that folder (an update points it at the new folder and keeps the old one), and the wallet
   itself writes the browser host manifests, the extension copy, the desktop entry for bringyourownagent:// links and
   the icons, and tells the desktop about them. The script prints what it wrote. Keep the package folder: it holds
   uninstall.sh.

3. Optional, for sites that use the extension: in Chrome open chrome://extensions, turn on "Developer mode", press
   "Load unpacked" and choose the folder  ~/.local/share/BYOA/extension  (the installer prints the exact path). The
   wallet puts a copy of the extension there each time it starts, and tells every Chromium browser you have installed
   where its native host is, so nothing else needs to be set up.

4. Add a model: install the Llama app (https://llama.app), let it load a model, then in the wallet open "My models"
   and press "Add a model". Optional: add an OpenRouter key for the paid fallback.

The wallet starts by itself when a site asks for it (a bringyourownagent:// link, or the extension). "Start at login"
is a tick box at the foot of its settings, and the same entry can be written by ./install.sh --start-at-login.

The tray icon is a StatusNotifierItem. Many GNOME sessions show no such icons: with no tray host the wallet still runs,
and starting it again from the applications menu brings its window back; closing the window leaves the wallet running
in the background. GNOME needs the "AppIndicator and KStatusNotifierItem Support" extension to show the icon.

Dictation and the check of selected text in every program ("Everywhere") work on Windows only for now; the page is
hidden on Linux.


Updating
--------

Open the wallet and press "Check for updates" at the foot of the tab list (or "Check for updates" in the tray menu).
If the site has a newer version, press "Update and restart": the wallet downloads the package to
~/.local/share/AgentWallet/downloads, checks its SHA-256 against the one in the update feed
(https://bringyourownagent.org/downloads/linux/latest.json), unpacks it into a new folder under versions, points
current at it in one step, and starts itself again. The old version stays. The wallet asks the site only when you
press the button, never by itself. A copy that this installer did not install (a build from the source) shows "Get the
latest version" instead, which opens the download page.

The same update can be made from this package: unpack the newer one and run ./install.sh again. It is the same
layout, and the older versions stay in ~/.local/share/AgentWallet/versions.


Your agent can manage the wallet (My MCP)
-----------------------------------------

The package carries the bridge program byoa-mcp. The "My MCP" page of the wallet shows its path and the one command
to add it to Claude Code, Codex or OpenCode; the wallet asks you before an agent may manage it. Installed, the bridge
is  ~/.local/share/AgentWallet/current/byoa-mcp  (the same path across updates).


What it keeps, and where
------------------------

  Program:      ~/.local/share/AgentWallet/versions/{VERSION}  (current beside it points at the newest)
  Downloads:    ~/.local/share/AgentWallet/downloads  (a downloaded package and its .part while an update runs)
  Settings:     ~/.local/share/BYOA/wallet/settings.json  (sites, sources, routes, limits; no secrets)
  API keys:     ~/.local/share/BYOA/wallet/secrets.dat, sealed under a random key. The key lives in the Secret
                Service of your desktop session (GNOME Keyring, KWallet and the like: the item "BYOA agent wallet: key
                of the secrets file"); when the session has no Secret Service, it lives in
                ~/.local/share/BYOA/wallet/secrets.key (mode 0600, readable only by you). The wallet says which of the
                two it uses at the top of My models.
  Activity:     ~/.local/share/BYOA/wallet/activity-YYYY-MM-DD.jsonl  (time, site, skill, source, cost; no audio, no text)
  Extension:    ~/.local/share/BYOA/extension
  Sockets:      $XDG_RUNTIME_DIR/byoa  (or ~/.local/share/BYOA/run when the session has no runtime folder; only you can open it)
  Desktop:      ~/.local/share/applications/org.bringyourownagent.wallet.desktop
                ~/.local/share/icons/hicolor/*/apps/org.bringyourownagent.wallet.png
  Browsers:     ".../NativeMessagingHosts/org.bringyourownagent.wallet.json" in the folder of each Chromium browser
  Start at login: ~/.config/autostart/org.bringyourownagent.wallet.desktop

The wallet never stores the audio or the recognised text, and a site never sees your keys.

If the key file exists from an earlier session without a Secret Service and a Secret Service appears later, the
wallet imports that key into the service and keeps the file; it never makes a new key over it. If both have a key and
they differ, the Secret Service is tried first; when it cannot open the wallet's files, the file's key is tried and,
when that opens them, stored in the Secret Service instead, so no sealed file becomes unreadable while either store
has the right key. A damaged secrets.key is refused rather than replaced.


Removing it
-----------

1. Quit the wallet: right click the tray icon, "Quit" (or close its window and end the process from your desktop's
   process list).
2. In the package folder run  ./uninstall.sh --dry-run  to see what it would do, then  ./uninstall.sh  . It removes
   only the registrations of this installation: the browser host manifests that point into
   ~/.local/share/AgentWallet, the bringyourownagent:// desktop entry, the wallet's lines in the MIME defaults, and
   the autostart entry. It deletes no files.
3. Remove the folders you no longer want yourself: ~/.local/share/AgentWallet, ~/.local/share/BYOA,
   ~/.local/share/applications/org.bringyourownagent.wallet.desktop (if it is still there), the wallet's icons under
   ~/.local/share/icons/hicolor, and the Secret Service item named above if you want the sealed keys gone too.
4. Remove the extension in chrome://extensions, if you loaded it.


License
-------

Apache License 2.0 (LICENSE). Third-party components and their licenses: THIRD-PARTY-NOTICES.txt and the licenses folder.