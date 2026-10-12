// A complete little site that uses a visitor's agent wallet, with no AI of its own and no AI bill.
//
//   npm install ws                       (in sdk/js; the relay needs it)
//   node sdk/examples/site/server.mjs    then open http://localhost:8787
//
// It asks the visitor's own model yes/no questions through the skill text.generate. The page offers both ways to
// reach the wallet: the browser extension, and "connect without the extension" (a bringyourownagent:// link that
// starts the wallet, which then connects to this server directly).
//
// A wallet refuses links that point to localhost unless BYOA_WALLET_DEV_LOCAL=1 is set for it (tests only), so for a
// real try put this behind your own https host and a reverse proxy that forwards /byoa/ws as a WebSocket.

import { randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { createRelay } from '../../js/src/site/relay.js';

const PORT = Number(process.env.PORT ?? 8787);
const ORIGIN = process.env.PUBLIC_ORIGIN ?? `http://localhost:${PORT}`;
const ENDPOINT = ORIGIN.replace(/^http/, 'ws') + '/byoa/ws';

// What this site asks the wallet for (spec/rules-0.1-draft.md). It travels, as it is, inside every link.
export const MANIFEST = {
  byoa: '0.1-draft',
  purpose: {
    summary: 'Answer yes or no to questions about a secret thing in a guessing game',
    limits: { maxTextChars: 4000, maxCallsPerMinute: 30 },
  },
  skills: { 'text.generate': { languages: ['en'], required: true } },
  privacy: 'public',
  expectedCost: { perSession: '0.00 USD' },
};

const relay = createRelay({ manifest: MANIFEST, endpoint: ENDPOINT });
relay.on('connected', (wallet) => console.log('wallet connected:', wallet.wallet?.name ?? '?'));
relay.on('disconnected', () => console.log('wallet disconnected'));

const readJson = async (req) => {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) { size += chunk.length; if (size > 16_384) throw new Error('too big'); chunks.push(chunk); }
  return chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {};
};
const send = (res, status, body) => {
  res.writeHead(status, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
};
const FILES = {
  '/': ['index.html', 'text/html; charset=utf-8'],
  '/browser.js': ['../../js/src/site/browser.js', 'text/javascript; charset=utf-8'],
};

const server = createServer((req, res) => {
  handle(req, res).catch(() => send(res, 400, { error: 'bad request' }));
});

async function handle(req, res) {
  // only plain "/path?query" targets are served (not "http://host/..." or "//host/...")
  if (!/^\/(?!\/)/.test(req.url ?? '')) { send(res, 400, { error: 'bad request' }); return; }
  const url = new URL(req.url, ORIGIN);
  // the page is told apart by a cookie; use your own session id in a real site. It names the page, it is not a secret.
  const cookie = /(?:^|; )pk=([\w-]+)/.exec(req.headers.cookie ?? '');
  const pageKey = cookie?.[1] ?? randomBytes(12).toString('base64url');
  if (!cookie) {
    res.setHeader('set-cookie', `pk=${pageKey}; Path=/; HttpOnly; SameSite=Strict${ORIGIN.startsWith('https:') ? '; Secure' : ''}`);
    // without the cookie the next request would be a different page: the flow cannot work, so say so
    if (req.method === 'POST') { send(res, 400, { error: 'this demo needs cookies' }); return; }
  }
  // a foreign page must not be able to spend this visitor's codes: only same-origin requests change anything
  if (req.method === 'POST' && (req.headers['sec-fetch-site'] ?? 'same-origin') !== 'same-origin') {
    send(res, 403, { error: 'forbidden' });
    return;
  }
  {
    // the wallet's pairing is a secret of your server's: it is given to the page that connected, and never logged
    if (req.method === 'GET' && FILES[url.pathname]) {
      const [file, type] = FILES[url.pathname];
      res.writeHead(200, { 'content-type': type });
      res.end(await readFile(new URL(file, import.meta.url)));
    } else if (req.method === 'GET' && url.pathname === '/byoa/manifest') {
      send(res, 200, MANIFEST);
    } else if (req.method === 'POST' && url.pathname === '/byoa/code') {
      // the address: here the connection's own; behind a reverse proxy use the header your proxy sets, never one the visitor can send
      const issued = relay.newCode(pageKey, { ip: req.socket.remoteAddress ?? 'unknown' });
      issued ? send(res, 200, issued) : send(res, 429, { error: 'too many codes, wait a minute' });
    } else if (req.method === 'GET' && url.pathname === '/byoa/state') {
      send(res, 200, relay.codeState(pageKey, url.searchParams.get('code') ?? ''));
    } else if (req.method === 'POST' && url.pathname === '/byoa/cancel') {
      send(res, 200, { ok: relay.cancel(pageKey, (await readJson(req)).code) });
    } else if (req.method === 'POST' && url.pathname === '/ask') {
      // the server asks the visitor's model; the wallet's answer is user input, never trusted
      const { pairing, question } = await readJson(req);
      const wallet = relay.walletFor(pairing);
      if (!wallet || typeof question !== 'string') { send(res, 200, { ok: false }); return; }
      const answer = await wallet.call('text.generate', {
        messages: [
          { role: 'system', content: 'You answer yes/no questions about a secret thing: a cat. Answer only yes, no or unclear.' },
          { role: 'user', content: question.slice(0, 300) },
        ],
      }, { maxTokens: 4, temperature: 0, language: 'en', choices: ['yes', 'no', 'unclear'] });
      // only one of the three words we asked for is accepted; anything else the wallet sends is dropped
      const text = answer.ok ? String(answer.output.text) : '';
      send(res, 200, ['yes', 'no', 'unclear'].includes(text) ? { ok: true, text } : { ok: false });
    } else {
      send(res, 404, { error: 'not found' });
    }
  }
}

await relay.attach(server, '/byoa/ws');
server.listen(PORT, () => console.log(`open ${ORIGIN}`));
