# BYOA agent wallet for Android

The agent wallet of [BYOA](../../README.md) on a phone: the same engine, settings, routing, limits and direct connections as the
desktop wallet (the core is shared, not copied), with screens made for a phone. Package `org.bringyourownagent.wallet`, minimum Android 8
(API 26), portrait only, English.

**The wallet holds no model.** A site asks, you allow, and the wallet passes the site's question to a model you named: one on a server you
run (a Llama app, LM Studio, Ollama or a home server, by its address), or one behind your own OpenRouter key. The key and everything else
secret are sealed with a key in the Android Keystore of the phone (AES-256-GCM, one Keystore key for each purpose of a file, and the purpose
is covered by the seal); the key cannot be taken out of the phone, and the app does not allow backups.

## How a site reaches the wallet

There is no browser extension on a phone. A site shows a `bringyourownagent://connect?...` link ([CONTRACT-DIRECT](../../spec/CONTRACT-DIRECT.md)).
Android hands it to the wallet (an intent filter on the `bringyourownagent` scheme, whether the app is running or not), the wallet reads the
site's manifest from the link and asks you on a full screen: the site, what it is for, one block for each skill it asks for, the models, the
limits, and a four-character **check code** that the page where you pressed Connect shows too (Allow waits until you tick that they are the
same). A link that did not come through the browser (pasted under **Connect by link**, scanned, or sent from another device) is marked as such.

**A site's QR code.** A phone camera does not open a custom scheme, so the code that a site's library draws carries
`https://wallet.bringyourownagent.org/#link=<the link, as it is>`, the address of the web wallet. The app is registered for that address (an
App Link: an intent filter with `autoVerify`, https, that host, the path `/`). Where Android has verified it (the domain's
`/.well-known/assetlinks.json` names this package and the release certificate) the camera, a browser or a messenger opens the app; where it is
not verified, or the app is not installed, the address opens the web wallet in the browser, which needs nothing from us. The app takes the link out
of the fragment, accepts only a `bringyourownagent://connect?...` link there (`WalletAddress`, with unit tests), and hands it to the direct
transport as a **handed** link: the consent says it did not come through the browser on this phone, shows the check code and waits for the tick. An
address with no usable link (no fragment, another fragment, a bad link) opens the home screen with one plain line and contacts no site.

**Scan a QR code** (on the home screen and under Connect by link) opens Google's code scanner (Google Play services, through Microsoft's
`Xamarin.GooglePlayServices.Code.Scanner` binding): its own camera screen, so the wallet asks for no camera permission and sees no picture. A site's
QR code (the web wallet's address), a raw `bringyourownagent://connect?` link and a site's hand-off address (`https://<site>/byoa/hand/...`, which
goes to the browser: it is the site's page) are taken; anything else is said plainly not to be a site's code.

After Allow the wallet keeps a connection to the site and answers its calls with your models. While a site is connected a **foreground
service** shows a notification, "Agent wallet is serving N sites", with a **Stop** button: Stop ends the connections now, as quitting the
desktop wallet does, and changes nothing a site was allowed. Between Allow and the connection (the site's models may be getting ready for a
minute) the notification says "Connecting to <site>" and the home screen shows the site with the same Stop: Stop cancels that link too, the models
are no longer waited for, and the site never gets a hello. A site that has a stored pairing comes back when the wallet starts serving again
(the app is opened, or a new link arrives). A question that comes while the app is not on the screen is a notification that opens it.

## What is in the app

| Screen | What it does |
|---|---|
| Home | the sites connected now, **Stop serving**, the last lines of activity, and the doors below |
| Connect by link | paste a link |
| My models | your models (on/off, order, think, default set, Test, Edit, Remove), your OpenRouter key (paste, replace, remove after a question), Add a model on a server, Add a model from OpenRouter (the catalog behind your key: search, All/Cheap/Free, tick, test, add), "check the models when a site connects", "never start a model for me" |
| Sites | every site and your answer, its models, limits, Pause/Resume, Revoke, Block, the list of blocked sites with Unblock, "decide on my own", pause after a time unused |
| Limits | money and calls for the wallet, for every site and for each site, and the text limits, with the desktop's rules |
| Activity | the whole log, newest first, a page at a time |
| Help | the wallet's own articles (the ones about Everywhere, agents over MCP and the guard are not here: they are desktop-only) |

The consent also asks, as on the desktop: a paid fallback (**Allow once / Always for this site / No**) and a raise of a limit that a call has
reached. Pictures (`image.generate`) are shrunk with the desktop wallet's own Skia code, compiled into this project.

Left out on purpose, because a phone has no use for them: the pipes and the native messaging host (no extension, no MCP bridge), My agents,
the agent guard, Everywhere (dictation and the check of selected text), the updater and the registrations.

## Build, test, package

Needs the .NET 10 SDK with the `android` workload, the Android SDK (platform and build-tools) and a JDK. Build from this folder; the APK is
`bin/Debug/net10.0-android/android-arm64/org.bringyourownagent.wallet-Signed.apk` (signed with the .NET debug key):

```
dotnet build -c Debug -r android-arm64 -m:4 -p:EmbedAssembliesIntoApk=true -p:AndroidSdkDirectory="C:\Program Files (x86)\Android\android-sdk"
```

> **A plain `dotnet build -c Release` does not make a release.** It signs with the same .NET **debug** key, and the file it leaves in
> `bin/Release/net10.0-android/android-arm64/org.bringyourownagent.wallet-Signed.apk` has the Android Debug certificate: do not ship it, and do not
> hand it to anyone as the wallet (the build says so with warning `BYOA0001`). A release APK is made **only** by
> `wallet/packaging/make-android-package.ps1` (see Package below), which signs it again with the release key and checks the certificate.

**Unit tests.** The core hook for the Keystore (`SecretProtectors.UsePlatform`) and the words for the device (`ThisDevice`: "this phone") are
tested with the rest of the core, on any machine, without the Android workload:

```
dotnet test wallet/tests/Byoa.Wallet.Tests -c Release -m:4
```

**On a phone.** `wallet/tests/e2e/android_e2e.py` drives the wallet on a phone connected with adb, the way a person does (it finds the controls
in the `uiautomator` dump: Avalonia shows its control class and text there, but not the automation id). It builds or takes the Debug APK, installs it
(replacing the wallet's own package only), starts a fake site and a fake OpenAI-compatible model server (and a fake OpenRouter, for the paid
steps) on the computer, reaches them through `adb reverse`, connects the way a visitor does, and saves a screenshot of every step. It never
calls a real model or a paid API and touches nothing on the phone but the wallet's package. Run it with a Python that has `aiohttp`:

```
python wallet/tests/e2e/android_e2e.py --adb <adb.exe> --serial <phone> --shots <folder for the screenshots> --build
```

`android_release_e2e.py` is the shorter run for a Release APK (it has none of the Debug-only doors, see below). `android_qr_e2e.py` is the run for
the release-signed APK and the QR code: Android's verification of the web wallet's address, the address opened by the system with no package named
(the consent of a handed link, with the check code), the plain line for a bad address, and the scanner opening. Step 10 of `android_e2e.py` does the
same with a Debug build (the activity named, since the system has not verified the address for a Debug key) through to Allow and the site's hello.
That the wallet sends no HTTP or WebSocket request to a link's site before Allow is asserted only there, with the fake site as a witness: every address of
the step names it, and its counters are read before the consent, after the consent has been up, once more right before the tap on Allow, and around each bad
address. (A name lookup or a bare TCP connect would not move those counters.) A Release build refuses
ws:// and local hosts, so no link can name a witness in the release-signed run, and that run claims nothing about contact.

**Package.** `wallet/packaging/make-android-package.ps1` builds the Release APK (arm64, and x86_64 with `-IncludeX64`), signs it with the
owner's release key (the defaults name the keystore `C:\Tools\byoa-android-key\byoa-android-release.jks`, the alias `byoa-android` and the password
file next to it, which is handed to `apksigner` as `file:<path>` and never read, printed or copied by the script), verifies the signature with
`apksigner`, checks that the certificate's SHA-256 is the release certificate that `assetlinks.json` names (`-ExpectedCertSha256`), and prints the
SHA-256 and the versionCode. The APK reaches `-OutDir` only after that check. With `-DebugKey` it signs with the .NET debug key and says loudly that
the APK is **for testing only**; an APK with another key cannot update the release one (uninstall first), and the system does not verify the web
wallet's address for it. The script does not create a release key.

The Android project is **not** in `Byoa.Wallet.slnx` on purpose: the CI builds that solution (`dotnet build wallet/Byoa.Wallet.slnx`) on machines
that have no Android workload, and a project that needs it would break that build. Build the Android project by its own command, above.

### Debug-only doors

A Debug build (and no Release build) has a few doors for the end-to-end test, none of which answers a question of the wallet or decides anything:
links to a site on the device itself (`ws://localhost`, reached through `adb reverse`) are accepted; the notification of a question waiting is
silent; two intents (`DEBUG_RECREATE` makes the activity again, `DEBUG_COPY` puts a text on the clipboard so that the test can press the wallet's
own "Paste from the clipboard"); and a file `debug-openrouter.txt` in the app's own folder makes OpenRouter's models and list come from that address
(only an `http` address on the device itself), so that a paid model can be a fake. A Release build has the secure behaviour only.

## Not done, or not tested

- **SkiaSharp and 16 KB pages.** The Release build warns (XA0141) that `libSkiaSharp.so`, which comes with Avalonia, is not aligned for 16 KB
  pages, which Android 16 will require of apps that target it. It is not changed here (no package version is touched); it needs a newer SkiaSharp.
- Other providers' keys (the desktop's Add a model also knows Z.ai and some others), the "can do" editor of a model, My agents, the guard.
- A consent that is waiting lives in the memory of the process: if Android ends the process while the consent is on the screen or in its notification,
  the question is gone with it, and so is the site's waiting request (the site gives up). A new process cancels the old notification and says on the
  home screen that the question was from before the wallet restarted; the site has to ask again. This is not fixed.
- Landscape and rotation (the app is locked to portrait; a re-created activity is tested through the Debug intent, not by turning the phone). Google's
  scanner screen is not ours and turns with the phone.
- A real QR code read by the real camera: no code is shown on a screen in the tests (nothing may appear on the owner's screen). The scanner's opening
  and what the wallet does with an address are tested; the reading of a code is Google's.
- The scanner needs Google Play services on the phone (its module is downloaded on first use); without it the wallet says so in one line and the link
  can still be pasted.
- The Release APK is trimmed by nothing (no linker): it is larger than it could be. It is tested only in the short Release run.
- Pictures are shrunk by tested code (the desktop's `PictureShrinkerTests`), but a picture request from a site was not run on the phone.
- Adding an OpenRouter model costs a fraction of a cent for its test; that step is run against a fake OpenRouter only.
