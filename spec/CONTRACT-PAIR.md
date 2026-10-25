# BYOA 0.1: other devices and the web wallet

> **Draft.** This document adds four small things to the direct link ([CONTRACT-DIRECT.md](CONTRACT-DIRECT.md)): a link that reaches the wallet by hand, a hand-off URL a site can offer, a second screen that shares a live pairing, and the rules of a wallet that runs in a browser tab. It changes no frame and no existing rule. Where this text says nothing, CONTRACT-DIRECT and [CONTRACT.md](CONTRACT.md) apply as they are. The reasons are in the design note that came with this draft.

**Principle.** A wallet only ever dials out, to the site's `wss://` endpoint, and only after the visitor's Allow. Reaching a wallet from another device therefore means carrying the **link** to a place where the wallet can take it, or letting the site serve a second page from a connection that already exists. It never means the wallet accepting a connection from the network.

## 1. A link handed to the wallet

A **handed link** is a `bringyourownagent://connect?...` link that reaches the wallet by a way other than the browser on this computer: the visitor pasted it into the wallet, typed it, or it came from another device.

- **Same link, same rules.** It is parsed and checked exactly as in CONTRACT-DIRECT sections 1 and 7: scheme, `wss` endpoint, host rules, manifest, session code, size limits. A bad link is refused with a plain reason and no network access. The wallet makes **no network request of any kind before Allow**, so a site cannot tell a handed link that was declined from a visitor with no wallet.
- **It is the visitor's own action.** The visitor pressed Connect in the wallet, so a handed link is treated as one with user activation (a notice-less, straight-to-consent link, CONTRACT-DIRECT section 3). The wallet's activity log marks it "pasted".
- **What the text may be.** Surrounding white space is ignored, and so is one pair of matching quotes or angle brackets around the whole link. White space inside the link (a messenger that wrapped a long line) is removed, because a link has none. The text must hold **exactly one** link: a text with the scheme twice, or other text around the link, is refused ("that is not a single link").
- **The consent MUST say where it came from.** The window states that this link did not come through the browser on this computer: it came from another device or was pasted by hand, and the visitor is to check the code. It MUST show the check code and, when the manifest has `connect.check`, keep Allow disabled until the visitor ticks that the code is the same as on the site's page (CONTRACT-DIRECT section 3a).
- **A site without `connect.check`.** The consent MUST then use a stronger warning than for a browser link: *this link came from outside the browser and the site gives no way to check it; Allow only if you started it yourself, just now.* A wallet MAY go further and refuse a handed link whose manifest has no `connect.check`.
- **Everything else is a link's.** A Decline is "not now" and stores nothing; a link always asks; the Allow is held until the welcome echoes the manifest; a repeated session code is ignored for 10 minutes; at most 4 links are acted on at once.

## 2. The hand-off URL (sites)

The raw link is poor at travelling: messengers and mail clients do not make a custom scheme clickable, a phone browser does not launch it, and some browsers show an error. A site that wants to be reachable from another device SHOULD offer an `https` URL that leads to the same link.

- **The code answer** (`POST /byoa/code` in the reference library) MAY carry two more fields next to `session`, `link` and `check`: `handoff`, an `https://` URL on the site's own host, and `short`, a typed form of it.
- **The URL** is `https://<site host>/byoa/hand/<token>`. `token` is at least 128 bits of the URL-safe alphabet, belongs to one code and the room that asked for it, and dies with the code (120 seconds) or at its first use.
- **`GET` reveals nothing.** The page answers a `GET` with a page that holds no link and no code: a short explanation and one button, **Open my wallet**. It sends `Cache-Control: no-store`, `Referrer-Policy: no-referrer` and `X-Robots-Tag: noindex`. Messengers, mail scanners and search robots fetch URLs to draw previews, and a `GET` that gave up the link would spend it.
- **A click releases the link.** The button makes a `POST` from the page with the token in the body. The server returns `{ "link": "..." }` once and marks the token used; the page then navigates to the link, which is a user-initiated launch of the wallet. A second `POST` gets "this link was already used". The code itself stays valid until its own expiry, so that the wallet can still connect.
- **The short code** is 8 characters from the check-code alphabet (`ABCDEFGHJKMNPQRSTUVWXYZ23456789`), typed case-insensitively with an optional hyphen (`K7MQ-3XPZ`), entered at `https://<site host>/byoa/hand`. It gives the same button. The server MUST refuse more than 5 wrong tries per visitor address in 10 minutes, MUST refuse bulk lookups by a global ceiling, and answers a wrong, used and expired code alike.
- **The hand-off page MUST NOT** try to find out whether the wallet started: the browser tells it nothing, and a site that guessed would learn that a wallet exists. "No wallet", "declined" and "not yet" look the same to the site.
- **The page that offers it** tells the visitor that the wallet on the other device will ask him, and shows the check code as in CONTRACT-DIRECT section 3a.

## 3. The second screen (sites)

A visitor who connected a wallet to a site on one device (W) may let a second device (S) use that connection, by the site's own doing. The wallet is not told and does not change.

- **Adopt.** A page whose room owns a live pairing may ask the server for an **adopt token** (`POST /byoa/adopt`, from a click). The token is at least 128 bits, lives 120 seconds, works once, and is shown as an `https://<site host>/byoa/adopt/<token>` URL (and as a QR code). The device that opens it sends the token and its own room id (`POST /byoa/adopt/take`, from a click), and the server binds that room to the pairing.
- **Rooms.** A pairing has an owner room (the one that connected) and at most 2 more. Calls from any bound room go to the same wallet connection, and the site MUST count calls in flight across all rooms of a pairing against the wallet's `maxParallelCalls`.
- **Ending.** A room's own Disconnect removes only that room. The owner room's Disconnect, and the wallet's Revoke, Block and Pause, end the pairing for all rooms (the existing rules: `bye`, close, forget).
- **Declaring it.** A site that offers a second screen sets `"connect": { "devices": true }` in its manifest. A wallet SHOULD show, in the consent, *"This site may let your other devices use this connection."* The manifest travels in the link and the welcome echoes it, so a link made by somebody else cannot remove the flag.
- **Pairings that survive a restart.** A site that offers a second screen SHOULD keep pairings through its own restart, as a hash of each token (SHA-256) with its rooms and last use, so that a wallet that reconnects with its token after a deploy is welcomed instead of hearing `bad-session`. The tokens themselves are never stored.
- **The adopt token is a bearer secret** like the pairing: it MUST NOT be logged and MUST NOT be placed where third-party script on the page can read it.

## 4. The web wallet

A **web wallet** is a wallet whose code runs in a browser tab. It speaks CONTRACT-DIRECT as a wallet; the additions are these.

- **Name and kind.** The `hello` frame's `wallet` object carries `"kind": "web"`, and the name is "Agent wallet (web)": `{"name":"Agent wallet (web)","version":"0.1","kind":"web"}`. A site ignores a field it does not know.
- **The carrier.** A site may offer a link to a web wallet as `https://<wallet host>/#link=<the bringyourownagent link, URL-encoded>`. The fragment is never sent to a server. The web wallet reads it, removes it from the address bar and history (`history.replaceState`) at once, and treats the text as a handed link (section 1). A site SHOULD offer only web wallets it recommends: the wallet's page is handed the one-time code.
- **The site's side.** A browser always sends an `Origin` header on a WebSocket. The wallet's channel carries no cookies and no ambient authority, so a site SHOULD accept a connection to `/byoa/ws` whatever its `Origin`. A site that insists on an allow-list MUST include the origins of the web wallets it recommends.
- **A web wallet MUST:**
  - make no network request to the site before Allow: no WebSocket, no `fetch`, no image, no DNS prefetch of the site (the link, the check code and the consent come from the link alone);
  - show the same consent as a native wallet: the host from the endpoint, the site's purpose as the site's words, the skills, the models, the check code and its tick, and the notice of section 1;
  - serve only the privacy classes its sources can honour: a manifest whose `privacy` is `sensitive`, `personal-voice` or `local-only` cannot be served by a source that is a company's cloud, and Allow is not offered; `personal` carries a visible warning;
  - tell the visitor that his prompts go to a cloud service and on to the model's company (free models may be kept and used for training), and that the tab must stay open;
  - keep any key out of any origin that also serves other content, serve its page with a Content-Security-Policy that allows no inline script and no script from another origin, include no third-party script, font, image or frame, and refuse to be framed;
  - put a key in no URL, log, message, error text or DOM, and enforce the same limits and gate as a native wallet (CONTRACT.md section 3.4, the call gate of the rules);
  - answer a call that it cannot serve with `{"type":"result","id":N,"ok":false}`, like any wallet.
- **Lifetime.** A tab that is closed, frozen or put to sleep is a wallet that has gone away: the site sees a closed connection, as with any wallet, and uses its own path. A web wallet 0.1 keeps no pairing across tabs, so a returning page needs a new link.

## 5. Not specified here

A rendezvous service that swaps a short code for a link, and a relay through which a wallet at home answers a phone's consent, are discussed in the design note and are not part of this draft. Both would put a third party in the path of a one-time code, and both need their own document and a security review first.
