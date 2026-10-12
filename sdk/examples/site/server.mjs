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

const server = createServer(async (req, res) => {
  const url = new URL(req.url, ORIGIN);
  // the page is told apart by a cookie; use your own session id in a real site
  const cookie = /(?:^|; )pk=([\w-]+)/.exec(req.headers.cookie ?? '');
  const pageKey = cookie?.[1] ?? randomBytes(12).toString('base64url');
  if (!cookie) res.setHeader('set-cookie', `pk=${pageKey}; Path=/; HttpOnly; SameSite=Strict`);
  try {
    if (req.method === 'GET' && FILES[url.pathname]) {
      const [file, type] = FILES[url.pathname];
      res.writeHead(200, { 'content-type': type });
      res.end(await readFile(new URL(file, import.meta.url)));
    } else if (req.method === 'GET' && url.pathname === '/byoa/manifest') {
      send(res, 200, MANIFEST);
    } else if (req.method === 'POST' && url.pathname === '/byoa/code') {
      const issued = relay.newCode(pageKey, { ip: req.socket.remoteAddress ?? '' });
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
      send(res, 200, answer.ok ? { ok: true, text: String(answer.output.text) } : { ok: false });
    } else {
      send(res, 404, { error: 'not found' });
    }
  } catch {
    send(res, 400, { error: 'bad request' });
  }
});

await relay.attach(server, '/byoa/ws');
server.listen(PORT, () => console.log(`open ${ORIGIN}`));
