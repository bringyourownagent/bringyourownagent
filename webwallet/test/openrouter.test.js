import assert from 'node:assert/strict';
import { test } from 'node:test';
import { authUrl, challengeOf, chat, exchangeCode, listModels, makeVerifier, readModels, SourceError } from '../src/openrouter.js';

const KEY = 'sk-or-v1-secret-key-for-the-tests';
const json = (body, status = 200) => ({ ok: status < 400, status, json: async () => body });

function streamOf(...parts) {
  const encoder = new TextEncoder();
  return { ok: true, status: 200, body: new ReadableStream({ start(c) { for (const p of parts) c.enqueue(encoder.encode(p)); c.close(); } }) };
}

test('PKCE: the S256 challenge of the RFC 7636 example verifier', async () => {
  assert.equal(await challengeOf('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'), 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
});

test('a verifier is 43 URL-safe characters and is never the same twice', () => {
  const a = makeVerifier();
  assert.match(a, /^[A-Za-z0-9_-]{43}$/);
  assert.notEqual(a, makeVerifier());
});

test('the sign-in address carries the challenge, the method, the state and this page as the callback', () => {
  const url = new URL(authUrl({ callbackUrl: 'https://wallet.example/', challenge: 'CH', state: 'ST' }));
  assert.equal(url.origin + url.pathname, 'https://openrouter.ai/auth');
  assert.equal(url.searchParams.get('callback_url'), 'https://wallet.example/');
  assert.equal(url.searchParams.get('code_challenge'), 'CH');
  assert.equal(url.searchParams.get('code_challenge_method'), 'S256');
  assert.equal(url.searchParams.get('state'), 'ST');
});

test('the code is swapped for a key with the verifier, no cookies and no referrer', async () => {
  let seen;
  const fetch = async (url, init) => { seen = { url, init }; return json({ key: KEY }); };
  assert.deepEqual(await exchangeCode({ code: 'C1', verifier: 'V1', fetch }), { key: KEY });
  assert.equal(seen.url, 'https://openrouter.ai/api/v1/auth/keys');
  assert.deepEqual(JSON.parse(seen.init.body), { code: 'C1', code_verifier: 'V1', code_challenge_method: 'S256' });
  assert.equal(seen.init.credentials, 'omit');
  assert.equal(seen.init.referrerPolicy, 'no-referrer');
});

test('a failed exchange says the kind and the status, nothing else', async () => {
  await assert.rejects(exchangeCode({ code: 'C', verifier: 'V', fetch: async () => json({ error: 'secret words' }, 403) }), (e) => e instanceof SourceError && e.kind === 'key' && !e.message.includes('secret'));
  await assert.rejects(exchangeCode({ code: 'C', verifier: 'V', fetch: async () => { throw new Error('offline'); } }), (e) => e.kind === 'network');
  await assert.rejects(exchangeCode({ code: 'C', verifier: 'V', fetch: async () => json({}) }), (e) => e.kind === 'provider');
});

test('the model list keeps text models with a readable price; free means both prices are zero', async () => {
  const body = {
    data: [
      { id: 'a/free-one:free', name: 'Free One', context_length: 8000, pricing: { prompt: '0', completion: '0' }, architecture: { input_modalities: ['text'], output_modalities: ['text'] } },
      { id: 'b/paid', name: 'Paid', pricing: { prompt: '0.000001', completion: '0.000002' } },
      { id: 'c/image-out', pricing: { prompt: '0', completion: '0' }, architecture: { output_modalities: ['image'] } },
      { id: 'd/no-price', pricing: { prompt: 'x', completion: '0' } },
      { id: 'e/half-free', pricing: { prompt: '0', completion: '0.000001' } },
      { name: 'no id' },
    ],
  };
  const models = readModels(body);
  assert.deepEqual(models.map((m) => [m.id, m.free]), [['b/paid', false], ['e/half-free', false], ['a/free-one:free', true]].sort((x, y) => models.findIndex((m) => m.id === x[0]) - models.findIndex((m) => m.id === y[0])));
  assert.equal(models.length, 3);
  assert.equal(models.find((m) => m.id === 'b/paid').pricePerMillion.prompt, 1);
  assert.equal((await listModels({ fetch: async () => json(body) })).length, 3);
  await assert.rejects(listModels({ fetch: async () => json({}, 500) }), (e) => e.kind === 'provider');
});

test('the model list is asked for without a key', async () => {
  let init;
  await listModels({ fetch: async (url, i) => { init = i; return json({ data: [] }); } });
  assert.equal(init.headers, undefined);
  assert.equal(init.credentials, 'omit');
});

test('a chat call: the key only in the header, the answer and the cost back', async () => {
  let seen;
  const fetch = async (url, init) => { seen = { url, init }; return json({ choices: [{ message: { content: 'Paris' } }], usage: { cost: 0.0004 } }); };
  const r = await chat({ key: KEY, model: 'm/x', messages: [{ role: 'user', content: 'Capital of France?' }], maxTokens: 50, temperature: 0.2, fetch });
  assert.deepEqual(r, { text: 'Paris', costUsd: 0.0004 });
  assert.equal(seen.url, 'https://openrouter.ai/api/v1/chat/completions');
  assert.equal(seen.init.headers.authorization, `Bearer ${KEY}`);
  assert.equal(seen.init.body.includes(KEY), false);
  const body = JSON.parse(seen.init.body);
  assert.equal(body.model, 'm/x');
  assert.equal(body.max_tokens, 50);
  assert.equal(body.stream, false);
});

test('a streamed chat call gives the pieces as they come, also when an event is split between reads', async () => {
  const pieces = [];
  const fetch = async () => streamOf(
    ': OPENROUTER PROCESSING\n\n',
    'data: {"choices":[{"delta":{"content":"Hel"}}]}\n\ndata: {"choices":[{"del',
    'ta":{"content":"lo"}}]}\r\n\r\n',
    'data: {"choices":[],"usage":{"cost":0.001}}\n\ndata: [DONE]\n\n',
  );
  const r = await chat({ key: KEY, model: 'm/x', messages: [], stream: true, onDelta: (p) => pieces.push(p), fetch });
  assert.deepEqual(pieces, ['Hel', 'lo']);
  assert.deepEqual(r, { text: 'Hello', costUsd: 0.001 });
});

test('chat failures: the kind by status, and never the key or the provider\'s words', async () => {
  const kinds = { 401: 'key', 402: 'credits', 429: 'quota', 503: 'provider', 400: 'refused' };
  for (const [status, kind] of Object.entries(kinds)) {
    await assert.rejects(chat({ key: KEY, model: 'm', messages: [], fetch: async () => json({ error: { message: 'the prompt was: private' } }, Number(status)) }), (e) => e.kind === kind && !e.message.includes('private') && !e.message.includes(KEY));
  }
  await assert.rejects(chat({ key: KEY, model: 'm', messages: [], fetch: async () => { throw new Error(`failed with ${KEY}`); } }), (e) => e.kind === 'network' && !e.message.includes(KEY));
});

test('an abort passes through as an abort', async () => {
  const abort = new AbortController();
  const fetch = async (url, init) => new Promise((resolve, reject) => init.signal.addEventListener('abort', () => reject(Object.assign(new Error('x'), { name: 'AbortError' }))));
  const pending = chat({ key: KEY, model: 'm', messages: [], signal: abort.signal, fetch });
  abort.abort();
  await assert.rejects(pending, (e) => e.name === 'AbortError');
});
