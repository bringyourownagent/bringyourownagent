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
- **Decline and Block.** The wallet never connects. The site cannot tell this from "no wallet installed": it simply never sees a connection.
- **What a link's Decline or Block keeps.** A Decline or Block chosen on a page's own request (through the extension) is stored, as before: it silences later requests and links for that origin. A Decline or Block chosen on a link is **not stored**: it means "not now" for that link, and the next link or request asks again. A site can prove a link only by connecting, and the wallet makes no contact with a site it was told to block, so a link cannot switch a real site off for good. The visitor blocks a site for good in the wallet's Sites list: the notice of a link offers **Not now** and **Block in Sites…**, which opens that list.
- **An Allow from a link is provisional.** The wallet keeps the visitor's Allow (budget, skills, "decide on my own", and the site's summary and privacy) in memory only. It stores them, and keeps the pairing, only when the site's `welcome` echoes the link's manifest (§7), which shows that the site answers with the manifest the link carries. Until then the summary shown in the consent window comes from the link, and the window says so ("came by a link: confirmed after you allow"). The held answer is dropped when the connection ends first: an `error`, a timeout, a failed connection, a manifest that differs, a model that fails to load, or the wallet closing. A newer Allow from a link for the same site drops it too, since the newer decision is the later one. It is also dropped when the visitor decides about the site in the wallet meanwhile (a Decline, an Allow in a page's window, "decide on my own" switched): the visitor's later decision stands, and only the link connections that wait for that answer end. The site's stored pairing and its other connections are kept. A Decline of a page's window (never a link's) that was shown while the site was not yet allowed, and was opened before the welcome, ends the site's connections and forgets the pairing, even when the welcome has meanwhile stored the link's answer. A Decline of a window that only asks for more keeps what the site already has. A revoke, a block or a pause ends all of the site's connections and forgets the pairing (§4).
- **A link always asks.** Even if the origin already has a grant covering the manifest with "decide on my own" ticked, a link shows the notice and the consent, as for any link. The consent shows what is already allowed, and Allow is held until the welcome echoes the link (the bullet above). Decline is "not now": nothing is stored, and the existing grant and pairing stay. "Decide on my own" grants silently only a reconnect with the stored pairing (§4: no link, no click) and a page's request through the browser extension.
- **Asking for more.** The consent shows only the difference, as in [CONTRACT.md](CONTRACT.md).

### 3a. The check code: proof that the visitor started the connection

A link can be sent to a visitor by somebody else (a message, a redirect). Without a further check, a wallet that opens a link made for another person's page would pair with that person's session, and the stranger's page would then drive the visitor's wallet within what the visitor allowed. The check code closes this, in the manner of a device-authorization flow: the visitor sees the same short code in two places, on the page where he pressed Connect and in the wallet's consent window, and Allows only if they match. A visitor who did not press Connect on that site a moment ago has no page showing the code, and declines.

- **The code.** `check` is four characters from the alphabet `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (no `I`, `L`, `O`, `0`, `1`). Let `d = SHA-256(UTF-8("BYOA-CHECK-1:" + session))`; character `i` (0 to 3) is `alphabet[d[i] mod 31]`. The wallet computes it from the link's `session`; the site computes the same from the `session` it issued. It is written as one word of four characters (`K7MQ`).
- **The site shows it.** When the page asks for a code, the server's answer carries `check` next to `session` and `link`. While the page waits for the wallet it shows the code in large letters, with a line like "Your wallet will show this code. Allow only if it matches." A site that shows the check code sets `"connect": { "check": true }` in its manifest ([CONTRACT.md](CONTRACT.md) section 2). The manifest travels in the link and the welcome echoes it (§7), so a link made by somebody else cannot remove the flag.
- **The wallet shows it.** For a link whose manifest has `connect.check`, the consent window shows the code and a plain instruction ("The page where you pressed Connect shows the same code. If you did not just press Connect on this site yourself, Decline."); Allow stays disabled until the visitor ticks "The code is the same on the site's page". For a link without the flag, the window says the site gives no way to check that the visitor started this connection, and tells him to Allow only if he pressed Connect on that site's page himself a moment ago. The code is never shown for, or required of, a page's request through the extension: there the browser has already tied the request to the page.
- **No new traffic.** The code is derived from data already in the link; the wallet makes no contact before Allow, as before (§3).

## 4. The channel

JSON text frames over a WebSocket. The wallet accepts a frame of up to 4 MB from the site (a larger one ends the connection). A site MUST accept at least 1 MB per frame from a wallet and MAY refuse more. A site may say what it accepts with the manifest limit `purpose.limits.maxFrameBytes` (a whole number of bytes; default 1,048,576, at most 8,388,608): the wallet then sends frames of at most the smaller of that and its own setting. The extension transport has no such key — its messages are capped by the browser at 1 MB, so its picture parts are always at most 512 KB of base64 (wallet 0.37). The wallet's frames stay below the site's minimum: the largest is a `result` of `text.generate`, at most 128,000 characters escaped as JSON (about 768 KB in the worst case); a `chunk` carries at most 16,000 characters; an `image` frame carries at most 512 KB of base64 (wallet 0.37); `hello` is a few hundred bytes, plus the models the site was given if it asked for several; and the wallet never sends the manifest.

Wallet to server, first frame:

```json
{"type":"hello","v":"0.1","session":"<code>","pairing":null,"wallet":{"name":"Agent wallet","version":"0.1"},"grant":{"skills":["audio.transcribe"],"expiresAt":"..."}}
```

For a site that works with text, `grant` also carries `limits` (what the site was held to: `maxTextChars`, `maxAnswerTokens`, `maxCallSeconds`, `maxParallelCalls`, `maxCallsPerMinute`; the wallet's picture limits too from wallet 0.37, [CONTRACT.md](CONTRACT.md) section 3.4a), `capabilities` (wallet 0.36: the feature names of the closed list `needs`, `can`, `kind`, `cancel`, `image.generate`, `images-in`, `parts`, `frame-limit` that this wallet supports; an older wallet sends none) and, if the site asked for models, `models` (the lists it was given, one per skill, every skill and not only `text.generate`, each entry with `can` and an agent entry with `abilities` when the site asked by properties or for abilities, [CONTRACT.md](CONTRACT.md) sections 3.3 to 3.6).

Server to wallet:

- `{"type":"welcome","pairing":"<opaque token>","manifest":"<the same base64url string as in the link>"}`, or `{"type":"error","code":"bad-session"}` followed by a close.
- **Error codes.** An `error` frame carries a `code` and is followed by a close.

  | Code | Retry later? | What the wallet does |
  |---|---|---|
  | `bad-session` | no | The code is unknown, spent or expired. The wallet stops, forgets the pairing it was trying (if it was resuming one) and tries no more; the visitor needs a new link. |
  | `rate-limited`, `busy` | yes | The server did not look at the code or at the pairing. The wallet keeps both, closes this connection and tries again by its backoff (1, 2, 5, 10, 30 seconds): with a code for up to 60 seconds after the link, with a pairing for as long as the grant is valid. |
  | any other code, or none | no | Treated like `bad-session`. |

  A site that is restarted SHOULD answer a flood of returning wallets with `rate-limited`, not with `bad-session`: the first costs a wallet a few seconds, the second costs its visitor the pairing.
- `pairing` is a token of 128 bits or more (at least 22 characters of the URL-safe alphabet `A-Z a-z 0-9 - _`) that the wallet stores per origin **only** if "decide on my own" is ticked. It lets the wallet reconnect later (after a wallet restart, or when the page reloads) with `{"type":"hello","session":null,"pairing":"<token>",...}` and no new link.
- **The pairing is a bearer secret of the site's own server.** Whoever presents it becomes the wallet of that pairing, and the newest connection with a pairing replaces the older one. A site SHOULD therefore keep it on the server, tied to the visitor's session (for example by an `HttpOnly` cookie), and MUST NOT put it where script on the page can read it if that page can run third-party script.

Calls:

- Server to wallet: `{"type":"call","id":N,"skill":"audio.transcribe","input":{...},"params":{...}}` (the input and params of the skill, see [CONTRACT.md](CONTRACT.md) §3).
- Server to wallet, **cancel** (wallet 0.36): `{"type":"cancel","id":N}` for a call still running. The wallet stops the call (an agent's whole process tree is killed; nothing more is spent on it) and answers `{"type":"result","id":N,"ok":false}`. A wallet that does not know the frame ignores it, so an old wallet behaves as today and the site's own timeout still applies. In the browser binding there is no cancel in this release.
- Wallet to server: `{"type":"result","id":N,"ok":true,"output":{...},"receipt":{"cost":"0.000 USD","provider":"local"}}` or `{"type":"result","id":N,"ok":false}`. For a call that named one of the site's models the receipt also has `"model":"m2"`. A call answered by an agent that lacked an ability the site asked about also carries `"lacks":["web.read"]` next to `output` (wallet 0.36, [CONTRACT.md](CONTRACT.md) §3.6).
- **Streaming.** A `text.generate` call with `"params":{"stream":true}` is answered with `{"type":"chunk","id":N,"text":"..."}` frames, in order, as the model writes (the wallet gathers them, at most one frame every 80 ms), and then the `result`, which has the whole answer. The pieces are a preview: the `result` replaces them, and an `ok:false` result discards them. A site that does not stream ignores `chunk` frames (unknown frames are ignored), and a call without `stream` gets no `chunk` at all.
- **Pictures** (wallet 0.37). The answer of an `image.generate` call travels as `image` frames before the `result`, never inside it: `{"type":"image","id":N,"index":i,"seq":j,"of":k,"mime":"image/png","data":"<base64, at most 512 KB>"}`, then the `result`, whose `output` carries only each picture's metadata ([CONTRACT.md](CONTRACT.md) §3.7). The frames of one call all precede its `result`, and are sent only when the call succeeded: parts already received are discarded by the site when the `result` is `ok:false` or the connection closes — parts are void without their `result`. A site that does not know the frame ignores it, as any unknown frame.
- **Changes.** When the visitor changes the models or the limits of the site (in the wallet's Sites page), the wallet sends `{"type":"grant","models":{...},"limits":{...}}`: the same two objects as in `hello`, whole (models for every skill, with their `can`). A site that does not read it keeps the old ones until its next connection.

Rules for calls and the connection:

- The wallet applies exactly the same gate, limits, budget, routing, fallbacks, "ask before paid" and activity log as for extension calls.
- **Calls in flight.** The site MUST keep at most as many calls in flight to one wallet as `grant.limits.maxParallelCalls` says (8 unless the visitor or the site said otherwise; 4 when the wallet's `hello` has no `limits`). It MAY queue more, but only a bounded number, and the waiting time of a call counts from the moment of the call, not from the moment it is sent. A call may wait for its own limit (`maxCallSeconds`, up to 600 seconds): a site's timeout is not shorter than that. The wallet is the backstop: it answers any call beyond the limit with `ok:false`.
- Either side may send `{"type":"bye"}` and close.
- Revoking, blocking or pausing the site in the wallet closes the socket and forgets the pairing.
- The wallet sends a ping every 30 seconds.
- On a disconnect, the wallet reconnects with backoff (1, 2, 5, 10, 30 seconds) while the grant is valid **and** the pairing is stored. Otherwise it stays disconnected.
- A frame over 4 MB from the site ends the connection. A site MUST NOT send one, and MUST bound the size of what it accepts from the wallet (at least 1 MB, see above).

**What ends what**

| Thing | It ends when |
|---|---|
| A one-time code | it is used once, or 120 seconds pass, or the page cancels it |
| The wallet's retries with a code | 60 seconds after the link (the wallet gives up; the code itself lives 120 s) |
| A `bad-session` error | the wallet forgets any pairing it was trying and stops; the visitor needs a new link |
| A pairing | the visitor revokes, blocks or pauses the site, declines a page's window that was opened before a link's welcome (§3), a reconnect's welcome asks for more than the grant covers, the site forgets it (idle for 24 hours or more is a sensible limit), or the site restarts (a site that keeps pairings in memory forgets them all) |
| A visitor's Allow for a link, held | the site's welcome echoes the link's manifest (then the answer is stored, §3), or the connection ends first, or the visitor decides about the site in the wallet meanwhile, or a newer Allow from a link replaces it (§3) |
| A connection | either side says `bye`, a frame breaks a rule, it is silent too long, or the site lets it go (a long-lived connection may be let go with `bye`; the wallet comes back by itself with its pairing) |

## 5. The site's side (what a site has to do)

- **Offer both ways when it can.** "Connect through the extension", shown only when `window.byoa` exists, and "Connect without the extension", always shown.
- **Connecting without the extension.**
  - The click asks the server for a pairing code bound to this browser page, then navigates to the `bringyourownagent://` link inside the same click handler.
  - The page shows a waiting state with a cancel button, and after 90 seconds a plain "the wallet did not connect" (no other detail).
  - When the wallet's `hello` arrives, the server binds that wallet connection to the page and tells the page it is connected.
- **The indicator.** The page shows whether the wallet is connected in its header and lets the visitor disconnect it at any moment, with one click: the page tells its server, the server says `bye` to the wallet, closes the connection and forgets the pairing (the same as the wallet's own Revoke). See CONTRACT.md §5.
- **Reload.** The page stores the pairing token that the server sends it on `welcome` (for example in `localStorage`). On reload it tells the server; if the wallet connection with that pairing is live, the server binds it again.
- **Calls.** The server sends calls straight to the bound wallet connection. It SHOULD use timeouts, a longer first call to allow for model warm-up (a timeout during warm-up is not a failure), and give up on the wallet after repeated failures (three in a row is a sensible limit): there is no other AI, so the site continues on its own path.
- **Trust.** The wallet's answer is user input. A site validates the shape it expects, caps the size of what it accepts, and never renders it as markup.
- **The endpoint** is `wss://<host>/<path>` for the channel. A site may also publish its manifest at an `https://` URL for humans and tools; the wallet never fetches it.
- **The page's three HTTP calls** are the site's own business, but they must not tell a "no" from a "no wallet". The state query answers only `waiting`, `connected` (with the pairing) or `expired`. The reference JavaScript library uses `POST /byoa/code` returning `{ code, link }` (an older site used the name `session` for `code`), `GET /byoa/state?code=` returning `{ state, pairing? }`, and `POST /byoa/cancel` with `{ code }`. The code identifies the page that asked for it, not the visitor: the site ties it to its own page or session id.
- **Rate limits** are per visitor address and per page. A site behind a reverse proxy takes the address from a header its own proxy sets, never from one the visitor can send.
- **Server limits.**
  - Codes are single use, expire after 120 seconds, and are rate-limited per IP.
  - A code the server did not issue, or one already used or expired, gets `error` with `bad-session`. This is a MUST: the wallet's proof that the server issued a link rests on it (§7).
  - At most one wallet connection per pairing.
  - Frames from the wallet: at least 1 MB each. The server MAY refuse more, and then closes the connection. A server that can take bigger frames says so with the manifest limit `maxFrameBytes` (§4); the pictures of one call still come as parts of at most 512 KB each (wallet 0.37).
  - Unknown frames are ignored; malformed JSON closes the connection.
  - The server sends nothing before a valid `hello`.

## 6. The scheme name

The scheme is `bringyourownagent://`, never `byoa://`. "BYOA" is a generic term already used by other products, and on Windows the last application to register a scheme receives its links: a rival `byoa://` handler would receive our session codes. Checked on 11 October 2026: neither name is in the IANA URI scheme registry. Registering `bringyourownagent` there provisionally (RFC 7595) is a later step.

## 7. The manifest travels in the link; nothing is fetched before Allow

Fetching a manifest from the site before consent would let the site learn that a wallet is installed (and that the visitor then declined), which breaks the rule that those cases look alike. So:

- The link carries the manifest itself: `manifest=<base64url, no padding, of the manifest JSON bytes, at most 4096 bytes decoded>`.
- The wallet makes **no** network request of any kind before Allow (or before the silent "decide on my own" grant of a reconnect). It parses and validates the manifest from the link and shows the consent from it.
- The site MUST keep **the exact string** it put in the link and echo that string: the comparison is over bytes, and the same manifest serialised again (another key order, other escaping, another process) is a different string. A site that builds links on one server and accepts wallets on another keeps the string where both can read it.
- After Allow, `hello` as in §4. The server's `welcome` must echo the manifest it put in the link: `{"type":"welcome","pairing":"...","manifest":"<the same base64url string>"}`. The wallet compares it byte for byte with the link's. On a match it stores the visitor's answer to the link (§3) and the pairing, if the site has "decide on my own". On a mismatch it sends `bye`, closes, drops the held answer, and records "the site's manifest differs from its link" in its activity log. It does not revoke the site or forget its pairing: a link from anywhere can name any site, so a link must not be able to switch one off.
- On a reconnect with a pairing (no link), `welcome.manifest` is the site's current manifest; if it asks for more than the stored grant covers, the wallet sends `bye`, closes, and needs a new link and a new consent (which shows only the difference).

**What the echo proves.** The echo shows that the server answers with the manifest the link carries, so a substituted manifest is caught. It does not prove by itself that the server issued the code to this visitor's browser: a site's manifest is public, and anyone who has seen a link can copy it. The proof that the code was issued comes from the server accepting the code, which is why a site MUST answer `bad-session` for any code it did not issue (§5).
