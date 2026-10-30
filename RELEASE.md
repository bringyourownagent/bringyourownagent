# Releasing

One gate for every release of the wallet package and the SDK packages. A step is not done until its check passes; write the
result (commit, size, SHA-256) into the release notes. The site and the game servers have their own gate, kept with their
code.

## 1. Before anything is built

- The change is on `main` and the CI runs are green: `SDK tests` and `Wallet tests` (the wallet's unit tests on Windows
  and Linux, the license check of the packaging script, and a package build from a clean checkout).
- `git diff <previous release>..HEAD` has been read for the files that ship: `wallet/`, `sdk/`, `spec/`.
- The texts that make promises (the window texts, `docs/wallet-0.1.md`, `wallet/README.md`, `spec/CONTRACT-DIRECT.md` section 3 and 4)
  say what the code does. A wording change goes with the code change that needs it, not after.
- The version is real: `wallet/Directory.Build.props` for the package, `sdk/*/` for the libraries. A fix to a published
  library version is a new version, never a republish of the same number.

## 2. The wallet installer (Windows x64)

1. Raise `<Version>` in `wallet\Directory.Build.props` (the updater only offers a higher version). Then
   `powershell -ExecutionPolicy Bypass -File wallet\packaging\make-package.ps1 -OutDir <empty folder> -Installer -WithDeltas`
   (`-WithDeltas` fetches the feed on the site first, so that the update is a small delta; leave it out for the first release).
   The script fails on a missing file, a package with no license file, or an app that is not self-contained. It writes
   `<OutDir>\release` (Setup.exe, the full and delta packages, `releases.win.json`) and the zip with `<zip>.sha256`.
2. Read the printed size and SHA-256 of `AgentWallet-win-Setup.exe` and keep them.
3. Install on a **clean** Windows machine or VM (no earlier install, no .NET), as a normal user: run Setup.exe. Check, by hand:
   - the wallet starts from the tray and from a `bringyourownagent://` link (the notice, then the consent window);
   - Decline from a link sends nothing to the site and stores nothing; a second link asks again;
   - an Allow from a link connects only after the site's welcome echoes the link's manifest;
   - Settings, Apps, Installed apps, Uninstall removes the program and the registry keys, and leaves the data folder.
4. Test the update: `wallet\tests\e2e\installer_update_ui.py` installs an older TEST build (`-PackId AgentWalletTest -Shortcuts None`)
   from its Setup.exe, serves the newer one over HTTP, presses Check for updates and Update and restart on a hidden desktop, and
   checks that the install folder and the running wallet are the new version and the registry of the real wallet was not touched.
   Before the first release with a new Velopack version, run it once with both builds made by that version.
5. Repeat the start on a processor **without AVX** (a VM with AVX off). The wallet must start and use the no-AVX build, and a
   crash of one model must not take the tray down.
6. Run `wallet\tests\e2e\direct_e2e.py` on that machine (needs the speech clips from `make_samples.py`). It drives the real
   windows; the unit tests do not cover them.
7. Only then publish, in this order, to the site's download folder (`/var/www/bringyourownagent.org/downloads`): the files of
   `<OutDir>\release` except `releases.win.json` into `win/` (a `.part` name first, then a rename), then `releases.win.json`
   (the wallets that check see the new version only from this file), and a copy of `AgentWallet-win-Setup.exe` to the top
   of the folder (the page links to it). The wallet page reads the size, the hash and the version from these files, so check
   it shows the numbers of step 2. Keep the last Setup.exe and feed of the old release before replacing them.

## 3. The wallet package (Linux x64)

1. Raise `<Version>` in `wallet/Directory.Build.props` (the updater only offers a higher version). Then run
   `wallet/packaging/make-linux-package.sh --out <empty folder>` (add `--version <x.y.z>` to build a version other than
   the one in the props file, for example a second package for the update test). The script publishes the three programs
   self-contained for linux-x64, stages `app/`, `extension/`, the scripts, the readme, the licenses and the notices, and
   fails on a missing file, a package with no license file, an app that is not self-contained, a file that is not an
   x86-64 ELF, or a package that holds `.pdb` files or macOS libraries. It writes
   `byoa-wallet-<version>-linux-x64.tar.gz`, its `.sha256` and `latest.json` into the folder; the archive has sorted
   names, owner 0 and a fixed time. Note that a clean build is not byte-identical to another clean build: the .NET
   regex source generator names its generated classes with a fresh value on each compilation, so
   `Byoa.Wallet.Core.dll` differs and the archive hash follows it. A rebuild that reuses the build's intermediate
   output is byte-identical; the tar flags add no variability of their own.
2. Read the printed size and SHA-256 and keep them.
3. Install on a **clean** Linux machine (no earlier install, no .NET), as a normal user, with a fresh `HOME`:
   `sha256sum -c byoa-wallet-<version>-linux-x64.tar.gz.sha256`, unpack, `cd` into the package and run `./install.sh`.
   Check, by hand:
   - the wallet starts from the applications menu and from a `bringyourownagent://` link, and
     `xdg-mime query default x-scheme-handler/bringyourownagent` answers `org.bringyourownagent.wallet.desktop`;
   - the desktop entry, the icons and the host manifest of each installed Chromium browser point into
     `~/.local/share/AgentWallet/current`, and the extension copy is `~/.local/share/BYOA/extension`;
   - `./uninstall.sh --dry-run` lists only the registrations it made, and the folders it prints are the ones that stay;
   - the key is in the Secret Service of the session, or the wallet says it keeps `secrets.key` when the session has none.
4. Test the update: build a second package whose `--version` is higher, serve its folder with `python3 -m http.server`,
   and run the first copy with `BYOA_WALLET_HOME` set to an empty folder and
   `BYOA_UPDATE_FEED=http://127.0.0.1:<port>`: `Byoa.Wallet --self-update-test`. It must print every state, end with
   `current` pointing at the new version, and print the new version from the restarted program. A package whose
   SHA-256 does not match must be refused (the unit tests cover that, path traversal and link escapes).
5. Only then publish, to the site's download folder (`/var/www/bringyourownagent.org/downloads`): the archive and its
   `.sha256` into `linux/` (a `.part` name first, then a rename), then `latest.json` (the wallets that check see the new
   version only from this file). Keep the old archive and feed before replacing them.

## 4. The SDK packages

- `sdk/CHANGELOG.md` lists the change under the new version.
- The three libraries pass the shared test vectors (`sdk-tests.yml`): Python, Node (18 and 22), .NET (net8.0, netstandard2.0, .NET Framework 4.8).
- The owner pushes the version tag; the publish workflows (`publish-*.yml`) use OIDC, no token is stored.
- After publishing, install the published version in an empty folder and run the library's own check once.

## 5. After the release

- The release notes name the commit, the package size and SHA-256, and what was and was not tested (for example "direct e2e
  not run: no speech clips").
- Anything found later goes into the changelog or a known-issues list, not into a rewritten release.
