# BYOA 0.1, second transport: the direct link (no extension)

> **Draft.** This is the binding description of the second way a web page can reach an agent wallet: a link that starts the wallet, followed by a channel from the wallet to the site's server. It sits next to the extension transport in [CONTRACT.md](CONTRACT.md), which stays as it is. Manifests, grants and the call gate are the same ([rules-0.1-draft.md](rules-0.1-draft.md)).

**Idea.**

- The page shows a link `bringyourownagent://connect?...`.
- Clicking it launches the wallet through a registered URL protocol.
- The wallet shows its notice and a short consent.
- Only after Allow does the wallet open a WebSocket to the site's server.
- From then on the server and the wallet talk directly. When the data a skill needs is already on the server (for instance audio the server has received), the server sends it to the wallet with no round trip through the browser.

## 1. The link

`bringyourownagent://connect?v=0.1&endpoint=<urlencoded wss URL>&manifest=<base64url of the manifest JSON>&session=<code>`

(The `manifest` parameter is a value, not a URL: see §7.)

- **`endpoint`.** A `wss://` URL. The site's identity, shown to the visitor and used for grants, is the origin `https://<that host>[:port]`. TLS proves it. Grants, limits, blocks and "decide on my own" are shared with the extension transport for the same origin.
- **`session`.** A one-time pairing code, at least 128 bits of randomness, URL-safe, valid for 120 seconds, single use. The site's server creates it for one browser page.
- **Allowed hosts.** The wallet refuses to act on a link whose endpoint is not `wss`, or whose host is an IP literal, `localhost`, a `.local` name, or resolves to a private, loopback or link-local address. The one exception is a developer switch for tests, the environment variable `BYOA_WALLET_DEV_LOCAL=1`, which allows `ws://localhost`.
- **Bad links.** Malformed or unknown links are ignored, with an entry in the wallet's activity log and no network access.

## 2. Registration

- The wallet's installer registers `HKCU\Software\Classes\bringyourownagent` as a "URL Protocol", with `shell\open\command` = `"<wallet exe>" --url "%1"`. The uninstaller removes exactly that key.
- If the wallet is already running, the new process hands the URL to the running one over its existing per-user pipe and exits.

## 3. Before Allow

- **No network contact of any kind.** The wallet reads the manifest from the link (§7), shows the notice and consent from it, and does nothing else. Without a click the browser will not launch the protocol, so "only after a click" is enforced by the browser. That guarantee covers links a browser launches. A link handed to the wallet by anything else (another program on the visitor's computer, a command line) is treated like a click: the visitor still sees the notice with the site's origin and can decline, and a program running as the visitor could do worse anyway.
- **Decline and Block.** The wallet never connects. The site cannot tell this from "no wallet installed": it simply never sees a connection. Block also makes later links for that origin do nothing (an activity entry only).
- **Already granted.** If the origin already has a grant covering the manifest with "decide on my own" ticked, the wallet connects without a notice.
- **Asking for more.** The consent shows only the difference, as in [CONTRACT.md](CONTRACT.md).

## 4. The channel

JSON text frames over a WebSocket, at most 4 MB per frame.

Wallet to server, first frame:

```json
{"type":"hello","v":"0.1","session":"<code>","pairing":null,"wallet":{"name":"Agent wallet","version":"0.1"},"grant":{"skills":["audio.transcribe"],"expiresAt":"..."}}
```

Server to wallet:

- `{"type":"welcome","pairing":"<opaque token>","manifest":"<the same base64url string as in the link>"}`, or `{"type":"error","code":"bad-session"}` followed by a close.
- `pairing` is a token of 128 bits or more (at least 22 characters of the URL-safe alphabet `A-Z a-z 0-9 - _`) that the wallet stores per origin **only** if "decide on my own" is ticked. It lets the wallet reconnect later (after a wallet restart, or when the page reloads) with `{"type":"hello","session":null,"pairing":"<token>",...}` and no new link.
- **The pairing is a bearer secret of the site's own server.** Whoever presents it becomes the wallet of that pairing, and the newest connection with a pairing replaces the older one. A site SHOULD therefore keep it on the server, tied to the visitor's session (for example by an `HttpOnly` cookie), and MUST NOT put it where script on the page can read it if that page can run third-party script.

Calls:

- Server to wallet: `{"type":"call","id":N,"skill":"audio.transcribe","input":{...},"params":{...}}` (the input and params of the skill, see [CONTRACT.md](CONTRACT.md) §3).
- Wallet to server: `{"type":"result","id":N,"ok":true,"output":{...},"receipt":{"cost":"0.000 USD","provider":"local"}}` or `{"type":"result","id":N,"ok":false}`.

Rules for calls and the connection:

- The wallet applies exactly the same gate, limits, budget, routing, fallbacks, "ask before paid" and activity log as for extension calls.
- **Calls in flight.** The site MUST keep at most 4 calls in flight to one wallet. It MAY queue more, but only a bounded number, and the waiting time of a call counts from the moment of the call, not from the moment it is sent. The wallet is the backstop: it answers any call beyond 4 in flight with `ok:false`.
- Either side may send `{"type":"bye"}` and close.
- Revoking the site in the wallet closes the socket and forgets the pairing.
- The wallet sends a ping every 30 seconds.
- On a disconnect, the wallet reconnects with backoff (1, 2, 5, 10, 30 seconds) while the grant is valid **and** the pairing is stored. Otherwise it stays disconnected.
- A frame over 4 MB, in either direction, ends the connection. A site MUST NOT send one, and MUST bound the size of what it accepts back.

**What ends what**

| Thing | It ends when |
|---|---|
| A one-time code | it is used once, or 120 seconds pass, or the page cancels it |
| The wallet's retries with a code | 60 seconds after the link (the wallet gives up; the code itself lives 120 s) |
| A `bad-session` error | the wallet forgets any pairing it was trying and stops; the visitor needs a new link |
| A pairing | the visitor revokes or blocks the site, the manifest the site echoes differs from the link's, the site forgets it (idle for 24 hours or more is a sensible limit), or the site restarts (a site that keeps pairings in memory forgets them all) |
| A connection | either side says `bye`, a frame breaks a rule, it is silent too long, or the site lets it go (a long-lived connection may be let go with `bye`; the wallet comes back by itself with its pairing) |

## 5. The site's side (what a site has to do)

- **Offer both ways when it can.** "Connect through the extension", shown only when `window.byoa` exists, and "Connect without the extension", always shown.
- **Connecting without the extension.**
  - The click asks the server for a pairing code bound to this browser page, then navigates to the `bringyourownagent://` link inside the same click handler.
  - The page shows a waiting state with a cancel button, and after 90 seconds a plain "the wallet did not connect" (no other detail).
  - When the wallet's `hello` arrives, the server binds that wallet connection to the page and tells the page it is connected.
- **Reload.** The page stores the pairing token that the server sends it on `welcome` (for example in `localStorage`). On reload it tells the server; if the wallet connection with that pairing is live, the server binds it again.
- **Calls.** The server sends calls straight to the bound wallet connection. It SHOULD use timeouts, a longer first call to allow for model warm-up (a timeout during warm-up is not a failure), and give up on the wallet after repeated failures (three in a row is a sensible limit): there is no other AI, so the site continues on its own path.
- **Trust.** The wallet's answer is user input. A site validates the shape it expects, caps the size of what it accepts, and never renders it as markup.
- **The endpoint** is `wss://<host>/<path>` for the channel. A site may also publish its manifest at an `https://` URL for humans and tools; the wallet never fetches it.
- **The page's three HTTP calls** are the site's own business, but they must not tell a "no" from a "no wallet". The state query answers only `waiting`, `connected` (with the pairing) or `expired`. The reference JavaScript library uses `POST /byoa/code` returning `{ code, link }` (an older site used the name `session` for `code`), `GET /byoa/state?code=` returning `{ state, pairing? }`, and `POST /byoa/cancel` with `{ code }`. The code identifies the page that asked for it, not the visitor: the site ties it to its own page or session id.
- **Rate limits** are per visitor address and per page. A site behind a reverse proxy takes the address from a header its own proxy sets, never from one the visitor can send.
- **Server limits.**
  - Codes are single use, expire after 120 seconds, and are rate-limited per IP.
  - At most one wallet connection per pairing.
  - Frames up to 4 MB.
  - Unknown frames are ignored; malformed JSON closes the connection.
  - The server sends nothing before a valid `hello`.

## 6. The scheme name

The scheme is `bringyourownagent://`, never `byoa://`. "BYOA" is a generic term already used by other products, and on Windows the last application to register a scheme receives its links: a rival `byoa://` handler would receive our session codes. Checked on 11 October 2026: neither name is in the IANA URI scheme registry. Registering `bringyourownagent` there provisionally (RFC 7595) is a later step.

## 7. The manifest travels in the link; nothing is fetched before Allow

Fetching a manifest from the site before consent would let the site learn that a wallet is installed (and that the visitor then declined), which breaks the rule that those cases look alike. So:

- The link carries the manifest itself: `manifest=<base64url, no padding, of the manifest JSON bytes, at most 4096 bytes decoded>`.
- The wallet makes **no** network request of any kind before Allow (or before a silent "decide on my own" grant). It parses and validates the manifest from the link and shows the consent from it.
- The site MUST keep **the exact string** it put in the link and echo that string: the comparison is over bytes, and the same manifest serialised again (another key order, other escaping, another process) is a different string. A site that builds links on one server and accepts wallets on another keeps the string where both can read it.
- After Allow, `hello` as in §4. The server's `welcome` must echo the manifest it put in the link: `{"type":"welcome","pairing":"...","manifest":"<the same base64url string>"}`. The wallet compares it byte for byte with the link's; on a mismatch it sends `bye`, closes, forgets any pairing, and records "the site's manifest differs from its link" in its activity log (the grant is revoked).
- On a reconnect with a pairing (no link), `welcome.manifest` is the site's current manifest; if it asks for more than the stored grant covers, the wallet sends `bye`, closes, and needs a new link and a new consent (which shows only the difference).
