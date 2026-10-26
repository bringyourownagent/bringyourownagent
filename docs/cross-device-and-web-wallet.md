# BYOA across devices, and a web wallet: design

Status: draft of 25 October 2026 (night run `p5e1c7d9`). Author: an agent working for the owner; the owner has not read it yet.
Normative text for the recommended pieces is in `spec/CONTRACT-PAIR.md` (branch `night/phone`). What was built tonight is in section 9.

## 0. The short version

**The question.** A visitor opens a BYOA site on a phone (or on any computer without a wallet). His wallet, his local models and his keys are on his home PC. Or he has no PC wallet at all. How does the site reach an AI of his?

**Three facts decide everything.**

1. **Only a link has to cross between devices, never a wallet connection.** The wallet always dials out to the site's server (`wss://`), and the site binds that connection to the page that asked for the one-time code (the "room"). The page can be on a phone; the wallet can be anywhere that can run the link. So the whole cross-device problem is "carry one 120-second link from device S to device W", plus "let the visitor answer the consent window".
2. **The consent window is the hard part, not the link.** A wallet at home asks its question on the home screen. A visitor who is away from home cannot answer it. Options A, A+ and B work only while he can reach the PC's screen (at home, or the PC is within reach). Only a standing pairing (F) or a remote consent channel (C) work when he is away.
3. **Custom-scheme links are second-class citizens.** `bringyourownagent://...` is not clickable in most messengers and mail clients, is not launched by a phone browser, and on iOS Safari shows an error alert when nothing handles it. An `https://` URL is clickable everywhere, can go through "send tab to your devices", and can be a QR code a phone camera understands.

**Recommendation and order of building** (details and honest effort in sections 3 and 8):

| # | Piece | Needs a new service? | Effort | Who it serves |
|---|---|---|---|---|
| 1 | **A. Take a link by hand**: the wallet's "Connect a site by link…" window; the page's "Open on another device" block (copy, Share, QR) | no | hours (built tonight) | a visitor sitting at his PC, with a site open on another device |
| 2 | **D. Web wallet** (static page, OpenRouter key by PKCE login, free models, consent in a tab) | static hosting only | days for a prototype, weeks to harden | everyone without a Windows PC: phones, Macs, Linux |
| 3 | **A+. Hand-off URL on the site itself** (`https://site/byoa/hand/<token>` plus a short typed code) | no (the site's own server) | days (relay library + page) | links that survive messengers; same-room PC and phone |
| 4 | **F. Second screen** (the site lets a second page of the same visitor use a pairing that is already live) | no | days, site side only; wallet unchanged | a visitor away from home, once he connected the home PC to that site |
| 5 | **C. Relay with end-to-end encryption** (the home wallet is reachable by the visitor's own devices; consent can be answered on the phone) | yes (a relay) | weeks plus a security review | the real "my phone uses my home PC from anywhere" |
| – | **B. Rendezvous by a short code** | yes | days | not recommended; A+ with a typed code gives the same without a third party |

The wallet side of A is the only change to the existing wallet. D is a separate static project (`webwallet/`). A+ and F are site-side and change no wallet. The wire format is not changed; everything is an addition.

## 1. What exists today, in the facts that matter here

- **The link** carries `endpoint` (a `wss://` URL), `manifest` (base64url, at most 4096 bytes), `session` (a 192-bit one-time code, 120 s, single use) (CONTRACT-DIRECT §1, §7). The wallet retries its connection with the code for 60 seconds after the link.
- **No contact before Allow.** The wallet reads the manifest from the link and asks the visitor; it does nothing on the network until Allow. A site cannot tell "declined" from "no wallet".
- **The check code** (§3a): four characters computed from `session`, shown by the site's page while it waits and by the wallet's consent window. The visitor Allows only if they match. It exists so that a link made for somebody else's page cannot pair his wallet with that stranger's session.
- **Pairing**: with "decide on my own", the site's `welcome` token lets the wallet reconnect later without a link. The token is a bearer secret of the site's server, tied to the visitor's browser session.
- **Site side**: the .NET relay library (`DirectHub`, `WalletChannel`) keeps codes and pairings in memory, one "room" per browser page (a 16 to 64 character random id the page makes), refuses a pairing that is not the room's. Nothing is stored on disk; a restart forgets every pairing.
- **The wallet is Windows only** and receives a link in three ways: the OS protocol handler (`--url`), the per-user pipe from a second process, and the native host. `DirectTransport.HandleLinkAsync(url, ct, userActivation)` is the single entry point for all of them.
- **The extension path** (`window.byoa`) is unaffected by anything here.

What this means for "phone plus home PC": the phone's page asks its site for a code and a link as always. The link is the thing to move.

## 2. The options, step by step

Names: **S** is the device with the site open (a phone). **W** is the device with the wallet. The *room* is S's browser page as the site's server knows it. "The check code" is the `K7MQ`-style code of CONTRACT-DIRECT section 3a.

### A. The page offers the link for another device; the wallet takes it by hand

*Needs no new service.* This is the minimum, and it is built tonight.

1. On S the visitor presses **Connect**. The page asks its server for a code (`POST /byoa/code`), gets `{ link, check }`, and shows the "Open on another device" block: the check code in large letters, the link as copy-able text, a **Share** button where `navigator.share` exists, and a QR code. S does **not** fire `bringyourownagent://` itself on a phone: nothing there handles it (iOS shows an error).
2. The visitor sends the link to W: Share to a messenger's "saved messages", mail to himself, Phone Link or Nearby Share, the clipboard sync of his phone, or reading it off. (A QR code helps only when the receiving device has a camera: see D.)
3. On W the visitor opens the wallet's **Sites → Connect a site by link…**, pastes, presses **Connect**. The text goes to `DirectTransport.HandleLinkAsync`, the same code as a link from the browser. Nothing leaves the wallet yet.
4. The wallet shows the consent window. It says plainly that this link did **not** come through the browser on this computer ("from another device or pasted by hand: check the code"), shows the check code, and keeps Allow disabled until the visitor ticks that the code is the same as on S's page. If the site's manifest has no `connect.check`, the window says the site gives no way to check, and tells him to Allow only if he started this a moment ago.
5. Allow: the wallet dials `wss://site/byoa/ws` and says `hello` with the session. The site binds the connection to S's room. S's page polls, sees `connected`, and the site works on S with W's models.

Who sends what to whom: S to the site (code request, polls); S to W through the visitor's chosen channel (the link, once); W to the site only after Allow. The channel the link travels through (a messenger, a mail server, a clipboard history) sees the link, and so can read a 120-second code. Section 4 says what that gives.

*Limit:* the visitor must be at W's screen when step 4 happens, within about a minute (the wallet stops retrying 60 s after the link, and the code dies at 120 s).

### A+. A hand-off URL on the site itself

*Needs no third party; a small addition to the site's server and page.* It repairs the weakest part of A, the custom-scheme link that no messenger will linkify.

1. As in A step 1, but the server's answer also carries `handoff`: `https://site.example/byoa/hand/<token>` (a 128-bit one-time token for that code, 120 s) and a short typed form of it (8 characters, `K7MQ-3XPZ`, shown only on the page).
2. The visitor sends the **https** URL to W. It is clickable in every messenger and mail client, works with Chrome's "send tab to your devices", and a QR code of it is read by any phone camera.
3. On W the visitor opens it in the browser where the wallet is registered. The site's page `hand/<token>` checks the token, answers **the same link** the code request made, and shows an **Open my wallet** button. The click launches `bringyourownagent://` on W through the browser's normal protocol prompt. (Or he types the short code at `https://site.example/byoa/hand` and gets the same button.)
4. From here it is the ordinary link flow on W: consent with the check code, Allow, dial, bind to S's room.

The hand-off page is loaded by W's *browser*, so the site learns that somebody opened the URL. It does not learn that a wallet exists or that the visitor declined: the button gives nothing back (the browser launches the protocol with no signal to the page). The token is spent on the first open, so a second opener gets "this link was already used".

### F. The second screen: the phone uses a pairing that is already live

*Site side only; the wallet does not change.* This is the only cheap way to serve a visitor who is **away from home**.

1. At home, at W, the visitor connects the site in the ordinary way and ticks "decide on my own". The wallet keeps a pairing and holds the connection (it renews its own grant silently every 12 hours while the PC is on and the wallet runs).
2. The page on W shows **Use it on my phone too** (a user click). The server issues an *adopt* token: one-time, 120 seconds, bound to W's live pairing. The page shows it as a QR code (an `https://site/byoa/adopt/<token>` URL) and as a short code.
3. The phone opens the URL. The server binds the phone's room to the same pairing (a pairing may have several rooms; one is the owner). The phone's page now sends its calls to the home wallet through the site's server.
4. Later, away from home, the phone opens the site, the site recognises its room cookie, and calls go to the home wallet as long as the pairing is live.

Honest limits: it works **per site** (the visitor repeats step 2 for every site); it needs the home wallet to stay connected; and a site that forgets pairings on restart (the reference keeps them in memory) loses it, after which the wallet needs a new link at W. Sites that offer F SHOULD keep pairings through a restart. The wallet's budget, limits, guard and log apply to calls from the phone exactly as to calls from W; the log cannot tell the two apart.

### B. Pairing by a short code through a rendezvous service

1. S posts its link to a small service `R` and gets a code (8 characters). S shows it.
2. At W the visitor opens the wallet's **Connect by code** and types it. The wallet asks `R` for the link, which `R` returns once and deletes (or after 2 minutes).
3. Ordinary consent with the check code, Allow, dial, bind.

The wallet's request to `R` is the visitor's own action, and `R` is not the site, so the site learns nothing: "no contact before Allow" holds *with respect to the site*. The wallet would contact `R` before Allow, which is new, and `R` would learn the visitor's address and the time.

What it costs: `R` has to be hosted by somebody (a Cloudflare Worker with a 2-minute TTL is enough, nearly free at low volume). **`R` sees every link**: the site, the manifest, the one-time code. A malicious or compromised `R` can use the code before the wallet and become "the wallet" of S's room (the site's prompts go to it; its answers are fed to the site), and it can keep a list of which sites each visitor connects. Short codes (8 characters, about 40 bits) are guessable unless the service rate-limits hard (per address and globally), so `R` must refuse bulk lookups. Encrypting the link under the code needs a PAKE (SPAKE2 or CPace), which is a real piece of cryptography to build and review.

**Recommendation: do not build B.** A+ with a typed short code gives the same convenience with the *site* as the rendezvous, which already knows the link. B would matter only if a wallet had to connect with no browser at all (a terminal, a TV); that is not the case now.

### C. Persistent registration of the home wallet with a relay

*The real "my phone uses my home PC from anywhere". Heavy.*

1. Once, at home, the wallet shows **Pair a device**: a QR code with a relay address, a random inbox id (128 bits) and a symmetric key. The phone's browser scans it and opens `https://wallet.<domain>/#pair=...`, a page of the web-wallet project that keeps the key and the inbox id in its own origin's storage.
2. The home wallet holds one outbound WebSocket to the relay under that inbox id, authenticated by a keypair it made; the relay holds nothing but ciphertext in flight and the inbox id.
3. Later the phone's site shows its link; the visitor opens it **in the phone-side wallet page** (the same `#link=` hand-over as D). That page encrypts the link under the key and sends it through the relay to the home wallet.
4. The home wallet decrypts it, builds the consent model, and sends **the consent model back through the relay**; the phone page shows the consent screen; the visitor taps Allow on the phone; the wallet dials the site from home and binds S's room.

What is new and dangerous: **the phone becomes a remote control for the wallet's consent**, so whoever holds the inbox key can allow sites. The key must be stored well, listed in the wallet under "paired devices" with a revoke button, and every action from a device must be capped (free or local models only, a daily budget, no key changes). Metadata (the inbox id, sizes, times, addresses) is visible to the relay operator. It needs its own spec (`CONTRACT-RELAY.md`), a hosted relay (or a self-host option) and an outside security review before release. Do it after D, A+ and F have shown that people want it.

### D. The web wallet: a wallet in a browser tab

*Nothing to install. Static files.* Full design in section 5.

1. The site's page (on S, a phone or a Mac) offers **Use the web wallet**. A click opens `https://wallet.<domain>/#link=<the bringyourownagent link, urlencoded>` in a new tab. The fragment never goes to any server; the page reads it, then removes it from the address bar and history.
2. First time only: **Sign in with OpenRouter** (OAuth PKCE, a free account). The key goes to the tab's own storage. The wallet lists `:free` models and the visitor ticks the ones to give.
3. The web wallet shows its consent screen from the link's manifest with the check code and a budget, **before any network request to the site**. Allow: it opens `wss://site/byoa/ws` from the browser, says `hello`, and answers `text.generate` calls by calling OpenRouter from the tab.
4. The page on S sees `connected`. The tab must stay open: it is the wallet.

The same hand-over works across devices: a site on a desktop with no wallet shows a **QR code** of the same URL; a phone camera opens the web wallet on the phone, and the phone serves the desktop's page.

### E. Anything better?

- **E1. The site as the carrier** is A+ and F above, the best finding of this design, because it needs no third party.
- **E2. A share target.** An installed web wallet can declare a Web Share Target (Android Chrome): the site's Share button then lists "Agent wallet", and the link goes straight into the web wallet. A small addition after D.
- **E3. A native phone wallet** (Android first: Avalonia runs there, and the scheme is an intent filter). It makes `bringyourownagent://` work on the phone itself and could reach LAN models. A product in its own right; not tonight, not soon.
- **E4. A wallet-side clipboard watcher** that offers to connect when a BYOA link is copied: rejected. Reading the clipboard in the background is a privacy cost the visitor did not ask for. Ctrl+V into the "Connect by link" window does the job.
- **E5. Local network pairing** (the phone's page talks to the PC over the LAN): rejected. An `https` page cannot reach a LAN service (mixed content, private-network rules), and a wallet that listens on the LAN is a new attack surface.
- **E6. "Send tab to your devices"** works for the A+ URL (it is `https`), not for the raw link. Another reason for A+.

## 3. The comparison, and the decision

| | A: link by hand | A+: hand-off URL | F: second screen | B: rendezvous | C: relay | D: web wallet |
|---|---|---|---|---|---|---|
| New service to host | none | none (the site's own server) | none | **yes** | **yes** | static files |
| Visitor must be at W's screen | **yes** | **yes** | no, after one setup at W | **yes** | no | n/a (consent is on S) |
| Works away from home | no | no | **yes** (per site) | no | **yes** | yes, without local models |
| Wallet change | a window | none | none | a window and a client | a large client | none (a different wallet) |
| Site change | the page block | server + page | server + page | none | none | none |
| Who sees the link | the carrier channel | the carrier channel | nobody (adopt token only) | **R's operator** | nobody (E2E) | nobody (fragment) |
| Honest effort | hours | days | days | days | weeks + review | days / weeks |

**Decision.**

1. **Build A now** (this night): it is the least a visitor needs to connect two of his own devices, it changes one window in the wallet, and it makes every later flow reuse the same entry point. Ship with the page block, since a wallet window nobody can feed is half a feature.
2. **Then D, the web wallet.** It is the only option that serves the majority of visitors, who have no Windows PC and no local models. It needs no change on any site that already speaks CONTRACT-DIRECT. What it gives is smaller than a native wallet (free OpenRouter models, no local privacy, a tab that must stay open), and it has to say so.
3. **Then A+** (the hand-off URL, site side), because the raw link does not survive messengers, and the same URL is what a QR code should carry.
4. **Then F**, for visitors who want their phone to use the home PC while they are away. It rides on the existing pairing and needs no wallet change; its price is per-site setup and sites that keep pairings through a restart.
5. **C only when people ask for it** (a phone that uses the home PC at any site without per-site setup). It is the only design in which the phone can answer the consent window, and also the one with the largest security surface.
6. **B is not recommended.** It adds a hosted service that sees every link, for a convenience A+ already gives.

## 4. The security model

### 4.1 Who can drive the visitor's wallet

The wallet never listens for anything from the network. It makes **outbound** connections only, to `wss://` endpoints of sites it has been told to serve, and only after the visitor's Allow. So in every flow below, "who can drive the wallet" means "who can send frames over a channel that the wallet opened":

| Flow | Who can make the wallet answer a call | What they need |
|---|---|---|
| Browser link (today) | the site's server, and any page the site binds to that connection | the visitor's Allow on that site; the room's cookie for a page |
| A, A+ | the same: the site, and the room that asked for the code (S) | the visitor's Allow after comparing the check code |
| F | the site and **every room bound to the pairing** (W's page and the phone) | a live pairing; an adopt token (120 s, once) for a new room |
| D | the site, the same way (the web wallet is a wallet that happens to run in a tab) | the visitor's Allow in the tab |
| C | the above, **and the holder of the inbox key**, who can answer consent remotely | the inbox key: as valuable as the wallet's own settings |

What no flow gives anybody: the visitor's keys, his models directly, his files, other sites' grants. The guarantees of the wallet (budget, limits, log, guard, "ask before paid") apply to each call whatever room it came from.

### 4.2 What the check code protects, and what it cannot

**It protects** the visitor against a link made for **somebody else's room** (a message, a printed QR code, a forwarded URL). The attacker presses Connect on a site, gets a link, and sends it to the visitor ("click to claim"). The wallet shows the attacker's code; the visitor, who did not press Connect on that site, has no page that shows it, and declines. Without the code the wallet would pair with the attacker's room, and the attacker's page would then use the visitor's AI within what he allowed. In the cross-device flows the code is also what binds the two screens: if somebody replaces the link on its way from S to W, W shows another code than S does.

**It cannot protect** against:

- A visitor who is **talked into it**. An attacker who also tells him "the code will be K7MQ, Allow it" defeats it. The wallet's instruction ("If you did not just press Connect on this site yourself, Decline") is the defence, and nothing technical can replace it.
- A visitor who ticks without reading.
- A **site that shows no code** (no `connect.check`): the window can only warn. For a link that was *handed over* (pasted, from another device) the warning is stronger than for a browser click, because a link that comes through a messenger is the usual form of a phishing attempt. A later version of the wallet could refuse a handed-over link whose manifest has no `connect.check`; this draft does not (section 8, question 1).
- Four characters are 31^4, about 20 bits. They are enough against a link that is wrong *by chance* (one in 920,000), and against an attacker who cannot see the visitor's page. They are not a secret.
- It does not prove the link was issued to this visitor's browser; it proves the visitor's *page* shows the same code as the wallet.

### 4.3 What a stolen link, token or code gives

| Thing | Lives | What a thief gets | What he does not get |
|---|---|---|---|
| The link (A) | 120 s, once | If he uses it **before** the wallet: he becomes "the wallet" of S's room. The site's calls (the visitor's prompts, maybe his voice) go to him, and he can answer anything. The real wallet then hears `bad-session` and the visitor sees "did not connect" | Access to the real wallet, keys, models |
| The hand-off URL (A+) | 120 s, once | The same as the link, one step earlier | The same |
| The typed short code (A+) | 120 s, once | The same; 8 characters (about 40 bits) so lookups are rate-limited | The same |
| The adopt token (F) | 120 s, once | A second room on the visitor's **live pairing**: he can use the visitor's AI at that site within the grant, until the visitor disconnects | Other sites; the wallet itself |
| The pairing token | until the site forgets it or the visitor revokes | As today: he becomes the wallet for that room (the site's prompts go to him) | The visitor's AI: a pairing lets a *wallet* answer a site, not the other way round. A page uses the visitor's AI through the site's server, which holds the pairing |
| The OpenRouter key of the web wallet | until revoked at OpenRouter | The visitor's OpenRouter credits and free-tier quota | Anything on the visitor's computer |

**Link previews.** Messengers and mail clients fetch an `https` URL to draw a preview. A hand-off URL that gave up its link or its token on a plain `GET` would be consumed by the preview robot. So a hand-off page MUST answer `GET` with a page that reveals nothing, and release the link only on a `POST` made by a click.

### 4.4 Rate limits and lifetimes

| What | Limit |
|---|---|
| One-time code, hand-off token, adopt token | 120 seconds, single use (as the existing code) |
| Codes asked for | per visitor address, as in CONTRACT-DIRECT section 5 (the relay library: 10 a minute) |
| Typed short code (A+): wrong tries | 5 per address per 10 minutes, then refused for 10 minutes; a global ceiling on lookups per minute |
| Rooms per pairing (F) | at most 3 |
| The wallet's link window | the text is at most `DirectLink.MaxLength` (12,288 characters); a link repeated within 10 minutes is ignored; at most 4 links are acted on at once (all existing) |
| Wallet retries after a link | 60 seconds (existing) |

### 4.5 What the visitor is told

- **Wallet, any link that was not launched by the browser on this computer** (pasted, or from another device): in the consent window, near the host: *"This link did not come through the browser on this computer. It came from another device or was pasted by hand. Check the code."* Then the check code and its tick as today. For a site with no `connect.check`: a stronger line, *"This link came from outside the browser and the site gives no way to check it. Allow only if you started it yourself, just now."*
- **Wallet, F** (a site that lets other devices use its connection): the manifest says `connect.devices` and the consent adds *"This site may let your other devices use this connection."* (Specified in CONTRACT-PAIR; not built tonight.)
- **Web wallet:** everything above, and the lines of section 5.6: that prompts go to OpenRouter and to the model's company, that free models may keep and train on them, and that a tab must stay open.
- **The site's page**, always: the check code in large letters with *"Your wallet will show this code. Allow only if it matches."*

## 5. The web wallet

### 5.1 What it is

Static files, no build step, one origin, ES modules: `index.html`, a few scripts, the vendored manifest and gate code of `sdk/js` (the same rules as the native wallet), and a stylesheet. It is a wallet like the others: it reads a link, shows a consent screen, dials the site, answers calls. It differs in four ways: it runs in a tab, its only source is OpenRouter (the visitor's own key), its storage is the browser's, and it has no operating-system process behind it.

```
site page (on S) ──opens──▶ https://wallet.<domain>/#link=…        (fragment: never sent to a server)
                              │ parse + validate (no network)
                              │ consent screen: host, check code, models, budget  ── Allow
                              ├──▶ wss://site/byoa/ws   hello(session, grant) / welcome / call / result
                              └──▶ https://openrouter.ai/api/v1/chat/completions   (the visitor's key)
```

### 5.2 Sign-in and where the key lives

- **OpenRouter OAuth PKCE** (read from OpenRouter's documentation, not exercised tonight). The page makes a random `code_verifier`, keeps it in `sessionStorage`, and sends the visitor to `https://openrouter.ai/auth?callback_url=<the wallet's own page>&code_challenge=<base64url SHA-256>&code_challenge_method=S256&state=<random>`. OpenRouter returns to the callback with `code`; the page checks `state`, then `POST https://openrouter.ai/api/v1/auth/keys` with `{code, code_verifier, code_challenge_method}` and receives `{key}`. A free account is enough. The documentation does not describe a spending limit for keys made this way, so the wallet uses **only `:free` models unless the visitor ticks a paid one**, and tells him he can set a credit limit on the key at OpenRouter.
- **The key** is kept in the tab's `sessionStorage` of the wallet's origin, so it is gone when the tab is closed; only when the visitor ticks "Remember my key in this browser" is it also written to `localStorage` (IndexedDB would not be safer: any script of the origin can read both). It is read only at the moment of a request, never put in the DOM, a URL, a log or a message. "Sign out" removes it from the browser; revoking it at OpenRouter ends it everywhere.
- **What it cannot be**: a native wallet's DPAPI file. The key is as exposed as the page's script. That is why section 5.3 matters.
- **Free-tier limits** (OpenRouter's documentation, 25 October 2026): 20 requests a minute and 50 a day with fewer than 10 credits ever bought, 1,000 a day with at least 10. The wallet shows an HTTP 429 as "the free quota is used up" and answers the site's call with `ok:false`.

### 5.3 Why a separate origin, a strict CSP and no third-party script

Browser storage is shared by **every script that runs in the origin**. The key is a bearer secret. One script from an analytics service, a widget, a CDN, an advertisement or a compromised dependency, anywhere on that origin, reads it. So:

- The wallet lives on **its own origin** (a dedicated domain or at least a dedicated host name that serves nothing but the wallet and sets no cookies), never on the project's website, a blog, or any site that embeds anyone else's code.
- **No third-party script, font, image or frame**, and no inline script. The CSP, as a response header (a `<meta>` fallback is in the prototype for hosts that cannot set headers): `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src https://openrouter.ai wss:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'`, with `require-trusted-types-for 'script'` where the host allows it. `connect-src` cannot name the sites (any site may be asked), so it allows `wss:` in general; the other directives are what stop injected script.
- The DOM is built with `textContent` and elements only, never `innerHTML`, because everything from the site (its words, the manifest, the answers) is untrusted text.
- **No framework and no build step** on purpose: what a visitor runs is the files he can read. Subresource integrity does not help the first load (the host serves the HTML too), so the project publishes the hash of every file with each release and the page shows its own version.
- `Referrer-Policy: no-referrer`, so the OAuth `code` and the link never leave in a Referer; `history.replaceState` removes `#link=` from the address bar as soon as the link is read.

### 5.4 What an XSS on the wallet page would cost

Everything the origin holds: the OpenRouter key (so the visitor's free quota and any credits), his per-site grants, and the ability to answer any site's calls with chosen text or to allow any site by itself. It would **not** reach the visitor's computer, files or other accounts. The mitigations are 5.3, free models by default, and the visitor's option to set a limit on the key. It is a real risk that a native wallet has less of, which is why the web wallet is offered as the no-install option and says so; a visitor who cares installs the native wallet.

### 5.5 Consent and budgets without a native process

- **Consent** is a screen of the wallet's own top-level page, which the site cannot overlay (the site is in another tab, not in a frame; the wallet refuses to be framed). It shows the host from the link's endpoint, the purpose in the site's words, the skills it asks for and the models the visitor gives it, the check code and its tick, and Allow or Decline. A page opened by a site (a `rel=noopener` link) must also be told by the visitor to press Allow himself: the consent never auto-submits and ignores programmatic clicks (`event.isTrusted`).
- **Rules** are the native wallet's: the page runs the vendored `validateManifest` on the link's manifest and `checkCall` on every call (limits, rate, text size), and the same local limits (`maxTextChars` 100,000, `maxAnswerTokens` 20,000, calls a minute, calls in parallel). A manifest whose `privacy` is `sensitive`, `personal-voice` or `local-only` cannot be served (every source here is a company's cloud), and Allow is not offered; `personal` carries a warning.
- **Budget**: per site, per session, in the tab. Free models cost nothing, so the default budget is 0 and the wallet serves only `:free` models. A paid model must be ticked by the visitor, and its cost is counted from OpenRouter's reply (`usage.cost` when present, else an estimate from the price list) against the site's budget; the call that would pass the budget is refused. The count is the tab's honesty, not a hard cap: the hard cap is the credit limit on the key.
- **Activity** is a list in the tab: time, site, skill, model kind, cost; never text.
- **Allow is remembered per site** in `localStorage` (the skills and the budget) so that the next link shows "you allowed this site before" and asks only for what changed. A link still asks every time, as in CONTRACT-DIRECT section 3.

### 5.6 What it can never promise

- **No local-only privacy.** Every prompt goes to OpenRouter and on to the company that runs the model. Free models may be logged and trained on by their providers. The consent screen says so in one line, and a site that needs better does not get this wallet.
- **No background service.** The tab is the wallet. Closed, put to sleep by a phone, or frozen in the background, it is gone; the site sees a disconnect and uses its own path. The wallet asks the screen to stay awake (`navigator.wakeLock`) while connected, retries with backoff while the tab lives, and tells the visitor to keep the tab open. A pairing is not kept across tabs in 0.1.
- **No local models, no agents, no audio** in 0.1: `text.generate` only (`audio.transcribe` through an OpenRouter speech model is possible later).
- **A weaker root of trust**: the code is fetched from the host at each visit, so the host (and its certificate and deployment pipeline) is trusted every time, not once at install.
- **Sites must accept it.** A wallet in a browser sends an `Origin` header on its WebSocket; a site that refuses unknown origins refuses it. The wallet's channel carries no cookies, so a site loses nothing by accepting (CONTRACT-PAIR section 4).

## 6. Protocol deltas

Written as normative text in `spec/CONTRACT-PAIR.md`, a new file so that CONTRACT-DIRECT, which other branches are editing, is not touched. Every existing guarantee stays. Nothing in the wire format changes; everything is an addition that an older wallet or site ignores.

| Section | What it adds | Who it binds |
|---|---|---|
| 1. A link handed to the wallet | A link that did not come from the browser on this computer (pasted, from another device) is still a link: the same checks, the same consent, no contact before Allow. The consent MUST say where it came from and MUST show the check code. A site with no `connect.check` gets a stronger warning. The text is tolerated if it has a trailing space, quotes or angle brackets; it must hold exactly one link | wallets |
| 2. The hand-off URL | `handoff` next to `session`, `link` and `check` in the code answer. `GET` reveals nothing; the link is released by a `POST` after a click; one use, 120 s. A short typed form | sites (SHOULD, if they offer other devices) |
| 3. The second screen | An adopt token bound to a live pairing; at most 3 rooms per pairing; the owner room is the one that connected; Disconnect on any room ends the pairing for all; pairings SHOULD survive a restart; `connect.devices: true` in the manifest | sites that offer it; wallets SHOULD show `connect.devices` |
| 4. The web wallet | `wallet.kind: "web"` in `hello`; a site SHOULD accept the `Origin` of a web wallet; the link may travel as `https://<wallet host>/#link=<urlencoded link>`; a web wallet MUST NOT contact the site before Allow, MUST keep its key out of any origin that serves other content, and MUST serve only the privacy classes its sources can honour | web wallets; sites (the Origin) |

Not specified yet, only designed here: the rendezvous (B) and the relay (C). Both would need their own documents.

## 7. Effort and risks, honestly

| Piece | Effort | Main risks |
|---|---|---|
| A. Wallet window + page block | hours; done tonight | The visitor must be at the PC; a messenger may alter a long link (the page offers a copy button and a checksum-free form; a damaged link fails the wallet's checks and says so in plain words). QR of a long link is dense: see below |
| QR of the raw link | included | A link of 1,000 to 2,000 characters is a version 25 to 40 QR code; a phone camera reads it from a big screen but not from a small one. The block offers it only for a phone that scans, and prefers the short A+ URL once the site has one |
| D. Web wallet prototype | days (tonight: the protocol logic and a prototype, not a hosted product) | OpenRouter's CORS, key options and free-model availability are read from documentation only and are **not tried**; mobile browsers suspend background tabs; a hosted wallet origin needs a domain, TLS and a release process; the key sits in browser storage |
| D. Web wallet, hardened | weeks | The trust in the host at every visit; an XSS review; accessibility; browser quirks (Safari WebSocket in the background; Android Chrome tab freezing) |
| A+. Hand-off URL and typed code | days (relay library, one page, tests) | Link-preview robots (handled by `POST`); typed-code guessing (rate limits); another page on the site to keep safe |
| F. Second screen | days | The site must persist pairings or the home wallet falls back to needing a link after each deploy; every extra room widens what a stolen cookie gives; wallet shows nothing about other rooms in 0.1 |
| C. Relay with E2E | weeks to months, plus an outside review | A hosted service (cost, uptime, abuse, legal contact), key management on a phone, the phone as a remote consent channel, the metadata the relay sees |
| B. Rendezvous | days | The operator sees every link and can race the code; abuse as a link-delivery service; guessable short codes. Not recommended |

**Two risks that cross all pieces.** (1) *Consent at a screen the visitor cannot see* is the real obstacle to "my phone uses my home PC", and only F (after one setup) and C remove it. A and A+ must say plainly, in the page block, that the PC's window waits for him. (2) *Links that pass through other people's systems* (messengers, mail, clipboards) are readable for 120 seconds. That is enough to be raced, never enough to drive the wallet; the check code and the short life are the defence.

## 8. Questions for the owner

1. **Refuse a handed-over link whose site shows no check code?** This draft only warns, more strongly. Refusing is safer and would make `connect.check` the price of being reachable from another device. It breaks nothing that works today (such a site cannot offer another-device links in practice). Recommended for the next version of the wallet.
2. **A domain for the web wallet.** *Decided by the owner on 26 October 2026: `wallet.bringyourownagent.org`* (that host serves nothing else and sets no cookies; a separate registrable domain would be safer still).
3. **A web wallet's name.** *Decided by the owner on 26 October 2026: "Agent wallet (web)"*, so a site and a visitor can tell it from the native one.
4. **Who maintains the free-model list.** The web wallet reads OpenRouter's public model list at run time; nothing is shipped.

## 9. What was built tonight

All on branch `night/phone` of the repository; test results are in the DONE.md of the run folder (`phone2`).

| Piece | Where | State |
|---|---|---|
| Normative text for A, A+, F and the web wallet | `spec/CONTRACT-PAIR.md` | written, committed |
| **A, wallet side**: "Connect a site by link..." (Sites page button, a small window, the same code path as an operating-system link, consent that says the link came by hand and shows the check code) | `wallet/` (new files, one line in `SettingsWindow.cs`) | built, 30 unit tests, full wallet suite green (760) at the time; merged into `main` |
| **A, site side**: the "Open on another device" block (copy, Web Share, QR) with an own dependency-free QR encoder | `sdk/js/src/site/qr.js`, `crossdevice.js` | built; the encoder's output decodes exactly in zxing-cpp for all 322 symbols tried (versions 1 to 40, levels L/M/Q/H); 33 unit tests; merged into `main` |
| **D, web wallet prototype**: sign-in by PKCE, link intake (pasted or `#link=`), consent with the check code and a model list, the direct-link connection, calls answered from OpenRouter's free models, strict CSP | `webwallet/` | built; 59 Node tests against a pretend site; **the page was never opened in a browser** and OpenRouter was never contacted |
| A+, F, C, B | none | designed only |

Where the prototype differs from the design above, on purpose: the answer ceiling is 4,000 tokens (not 20,000: a free tier of 20 requests a minute is slow to wait for in a tab); the consent screen offers free models only, so the budget is always 0 (the connection code already counts a budget for paid models, but nothing lets the visitor set one yet); the CSP has no `img-src` (the page has no images, so `default-src 'none'` covers it) and the `<meta>` copy lacks `frame-ancestors`, which only a header can set (it is in `_headers`); Trusted Types are not required yet.

What the Node tests cannot show: the sign-in round trip with OpenRouter (its CORS and key options are read from documentation only), a real browser's WebSocket and `Origin` behaviour, a phone's tab suspension, the page's layout.
