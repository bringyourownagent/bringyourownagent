# Agent wallet (web): a prototype

A wallet in a browser tab. Nothing to install: static files, no build step, no dependencies. It holds the visitor's own OpenRouter key (from OpenRouter's sign-in, a free account is enough), speaks the BYOA direct link ([`spec/CONTRACT-DIRECT.md`](../spec/CONTRACT-DIRECT.md), [`spec/CONTRACT-PAIR.md`](../spec/CONTRACT-PAIR.md)), asks for consent, and serves `text.generate` from OpenRouter's models (free ones by default; paid ones only with a budget the visitor sets). It works on a phone, a Mac, anywhere a browser runs. The price: it cannot reach local models, and it works only while its tab is open. The design and the reasons are in the design notes of this work (section 5, "The web wallet").

It lives at `https://wallet.bringyourownagent.org/` (name and address decided by the owner on 26 October 2026). The protocol logic is tested with Node against a pretend site; the page itself (`src/app.js`) was driven in headless Chromium through the consent screen against OpenRouter's real public model list (free and paid models, search, the budget). The OpenRouter sign-in round trip has not been tried with a real account.

## What it does

1. **Sign in.** OAuth with PKCE: the page sends the visitor to `openrouter.ai/auth` with a challenge, gets a code back in its own address, swaps it for a key. The key is kept in the tab's `sessionStorage`, so it is gone when the tab is closed; it goes into `localStorage` as well only when the visitor ticks "Remember my key in this browser". It goes only into the `Authorization` header of a request to OpenRouter.
2. **Take a link.** The visitor pastes the link a site gave him, or opens `<this page>#link=bringyourownagent://connect?...` (what a site's QR code can carry). The same checks as the native wallet make are applied (`src/link.js`), and the link leaves the address bar at once.
3. **Consent.** The screen shows the site, its summary, the check code, a list of free models to tick (paid ones too when the visitor ticks "Also show paid models", each with its price; a paid model can be given only with a budget between 0.01 and 5 USD that the connection stops at), and what the data does (it goes to OpenRouter and the model's company). Allow is held until the visitor ticks that the check code is the same as on the site's page, and works only on a real click. A site that needs a skill this wallet cannot do (audio, files) or that handles data a cloud must not see cannot be allowed.
4. **Serve.** After Allow the page opens `wss://` to the site's endpoint, says `hello`, checks that the `welcome` echoes the link's manifest, and answers `call` frames by asking OpenRouter. A free model costs nothing; a paid one is served only within the budget the visitor set, and the call gate refuses what would pass it. The site's own limits and the wallet's are enforced by the same call gate as in the SDK (`vendor/byoa`, byte for byte the SDK's files).

## Files

| File | What |
|---|---|
| `index.html`, `style.css` | The page; strict CSP in a `<meta>`, no inline script or style |
| `_headers` | The same CSP plus `frame-ancestors 'none'` and other headers, for a static host that reads this format (Netlify, Cloudflare Pages) |
| `src/link.js` | Link parsing, pasted-text clean-up, the check code |
| `src/consent.js` | What the consent screen shows and when Allow is possible (no DOM) |
| `src/wallet.js` | The connection: hello, welcome, calls, limits, budget, streaming (no DOM, injected socket, fetch and clock) |
| `src/openrouter.js` | Sign-in, model list, one chat call |
| `src/store.js` | Key and per-site memory in `localStorage` |
| `src/app.js` | The screens |
| `vendor/byoa/` | The SDK's call gate and manifest validation, copied (a test checks they are identical) |
| `test/` | `node --test` |

## Run the tests

```
cd webwallet
node --test
```

Node 20 or newer. No install step.

## Serve it

Any static host, on **its own origin that serves nothing else and sets no cookies** (the key sits in that origin's storage, so another page on the same origin could read it). For a look on your own machine: `npx serve webwallet` or `python -m http.server` in this folder, and open `http://localhost:8000/`. `wss://` endpoints work from a `localhost` page; OpenRouter's sign-in needs the page's address as the callback, which `localhost` also satisfies.

## What is missing

- The sign-in round trip with a real OpenRouter account and a call to a real site have not been tried. Expect small things in `app.js` (layout, focus).
- No QR scanner: a phone user pastes a link or opens it from the site's hand-off address. A scanner needs the camera and `getUserMedia`, which the CSP and `Permissions-Policy` here switch off on purpose; add it deliberately, not by default.
- The budget is per connection: a new connection starts a new budget, and nothing adds them up across connections (OpenRouter's own key limits are the visitor's to set there).
- No activity log that survives a reload, no revoke list: only `forgetSite` in `store.js`.
- No test against the real relay library (it needs the `ws` package, which `sdk/js` does not have installed here).
- A tab sleeps in the background on a phone. The page asks for a screen wake lock while a site is served; it cannot do more.
