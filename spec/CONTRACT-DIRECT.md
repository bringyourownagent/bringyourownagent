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

- **No network contact of any kind.** The wallet reads the manifest from the link (§7), shows the notice and consent from it, and does nothing else. Without a click the browser will not launch the protocol, so "only after a click" is enforced by the browser.
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
- `pairing` is a long-lived token, 128 bits or more, that the wallet stores per origin **only** if "decide on my own" is ticked. It lets the wallet reconnect later (after a wallet restart, or when the page reloads) with `{"type":"hello","session":null,"pairing":"<token>",...}` and no new link.

Calls:

- Server to wallet: `{"type":"call","id":N,"skill":"audio.transcribe","input":{...},"params":{...}}` (the input and params of the skill, see [CONTRACT.md](CONTRACT.md) §3).
- Wallet to server: `{"type":"result","id":N,"ok":true,"output":{...},"receipt":{"cost":"0.000 USD","provider":"local"}}` or `{"type":"result","id":N,"ok":false}`.

Rules for calls and the connection:

- The wallet applies exactly the same gate, limits, budget, routing, fallbacks, "ask before paid" and activity log as for extension calls.
- At most 4 calls in flight per connection; extra calls get `ok:false`.
- Either side may send `{"type":"bye"}` and close.
- Revoking the site in the wallet closes the socket and forgets the pairing.
- The wallet sends a ping every 30 seconds.
- On a disconnect, the wallet reconnects with backoff (1, 2, 5, 10, 30 seconds) while the grant is valid **and** the pairing is stored. Otherwise it stays disconnected.

## 5. The site's side (what a site has to do)

- **Offer both ways when it can.** "Connect through the extension", shown only when `window.byoa` exists, and "Connect without the extension", always shown.
- **Connecting without the extension.**
  - The click asks the server for a pairing code bound to this browser page, then navigates to the `bringyourownagent://` link inside the same click handler.
  - The page shows a waiting state with a cancel button, and after 90 seconds a plain "the wallet did not connect" (no other detail).
  - When the wallet's `hello` arrives, the server binds that wallet connection to the page and tells the page it is connected.
- **Reload.** The page stores the pairing token that the server sends it on `welcome` (for example in `localStorage`). On reload it tells the server; if the wallet connection with that pairing is live, the server binds it again.
- **Calls.** The server sends calls straight to the bound wallet connection. It should use timeouts, a longer first call to allow for model warm-up, and give up on the wallet after repeated failures.
- **Trust.** The wallet's answer is user input.
- **Endpoint.** `wss://<host>/<path>` for the channel. A site may also publish its manifest at an `https://` URL for humans and tools; the wallet never fetches it.
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
- After Allow, `hello` as in §4. The server's `welcome` must echo the manifest it put in the link: `{"type":"welcome","pairing":"...","manifest":"<the same base64url string>"}`. The wallet compares it byte for byte with the link's; on a mismatch it sends `bye`, closes, forgets any pairing, and records "the site's manifest differs from its link" in its activity log (the grant is revoked).
- On a reconnect with a pairing (no link), `welcome.manifest` is the site's current manifest; if it asks for more than the stored grant covers, the wallet sends `bye`, closes, and needs a new link and a new consent (which shows only the difference).
