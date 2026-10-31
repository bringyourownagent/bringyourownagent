import assert from 'node:assert/strict';
import { test } from 'node:test';
import { effectiveLimits } from '../src/consent.js';
import { grantedModels, pickChoice, SiteConnection, usd } from '../src/wallet.js';
import { parseLink } from '../src/link.js';
import { FakeSite, makeLink, MANIFEST, SESSION } from './helpers.js';

const KEY = 'sk-or-v1-secret-key-for-the-tests';
const FREE = { id: 'a/one:free', name: 'One', free: true };
const PAID = { id: 'b/paid', name: 'Paid', free: false, pricePerMillion: { prompt: 1, completion: 2 } };
const noTimers = { setTimeout: (f, ms) => setTimeout(f, ms), clearTimeout: (t) => clearTimeout(t), setInterval: () => 0, clearInterval: () => {} };

function setup({ models = [FREE], manifest = MANIFEST, chat, budgetUsd = 0, now, showNames = true, getKey = () => KEY } = {}) {
  const site = new FakeSite();
  const link = parseLink(makeLink({ manifest })).link;
  const events = [];
  const calls = [];
  const connection = new SiteConnection({
    link,
    models: grantedModels(models, { showNames }),
    limits: effectiveLimits(manifest),
    budgetUsd,
    getKey,
    createSocket: site.createSocket,
    chat: chat ?? (async (call) => { calls.push(call); return { text: 'Paris', costUsd: 0 }; }),
    now,
    timers: noTimers,
    onEvent: (e) => events.push(e),
  });
  return { site, link, connection, events, calls };
}

async function connect(env) {
  const started = env.connection.start();
  env.site.open();
  env.site.say({ type: 'welcome', pairing: 'p', manifest: env.link.manifest });
  assert.deepEqual(await started, { ok: true });
}

const call = (id, extra = {}) => ({ type: 'call', id, skill: 'text.generate', input: { messages: [{ role: 'user', content: 'Capital of France?' }] }, params: {}, ...extra });

test('nothing is opened before start()', () => {
  const { site } = setup();
  assert.deepEqual(site.urls, []);
});

test('hello: the session, no pairing, the wallet\'s name, the grant, and the models with their refs', async () => {
  const env = setup({ models: [FREE, { id: 'c/two:free', name: 'Two', free: true }] });
  const started = env.connection.start();
  env.site.open();
  const hello = env.site.frames('hello')[0];
  assert.equal(hello.v, '0.1');
  assert.equal(hello.session, SESSION);
  assert.equal(hello.pairing, null);
  assert.deepEqual(hello.wallet, { name: 'Agent wallet (web)', version: '0.1', kind: 'web' });
  assert.deepEqual(hello.grant.skills, ['text.generate']);
  assert.deepEqual(hello.grant.capabilities, []);   // none of the closed list: no needs, no can, no cancel, no pictures
  assert.match(hello.grant.expiresAt, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
  assert.deepEqual(hello.grant.models['text.generate'].map((m) => [m.ref, m.name, m.id, m.runs]), [['m1', 'One', 'a/one', 'api-key'], ['m2', 'Two', 'c/two', 'api-key']]);
  env.connection.close();
  await started;
  assert.deepEqual(env.site.urls, ['wss://quiz.example.org/byoa/ws']);
});

test('model names stay hidden unless the visitor lets the site see them', async () => {
  const env = setup({ showNames: false });
  const started = env.connection.start();
  env.site.open();
  const [m] = env.site.frames('hello')[0].grant.models['text.generate'];
  assert.equal(m.name, 'Model A');
  assert.equal(m.id, null);
  env.connection.close();
  await started;
});

test('a welcome that echoes the manifest connects; a different one makes the wallet say bye and leave', async () => {
  const ok = setup();
  await connect(ok);
  assert.equal(ok.connection.state, 'connected');
  ok.connection.close();

  const other = setup();
  const started = other.connection.start();
  other.site.open();
  other.site.say({ type: 'welcome', pairing: 'p', manifest: `${other.link.manifest}x` });
  assert.deepEqual(await started, { ok: false, reason: 'manifest-differs' });
  assert.equal(other.site.frames('bye').length, 1);
  assert.ok(other.events.some((e) => e.type === 'manifest-differs'));
});

test('an error before the welcome ends it with the site\'s code; a stranger frame ends it too', async () => {
  const a = setup();
  const started = a.connection.start();
  a.site.open();
  a.site.say({ type: 'error', code: 'bad-session' });
  assert.deepEqual(await started, { ok: false, reason: 'bad-session' });

  const b = setup();
  const startedB = b.connection.start();
  b.site.open();
  b.site.say({ type: 'call', id: 1, skill: 'text.generate' });
  assert.equal((await startedB).ok, false);
  assert.equal(b.calls.length, 0);
});

test('a call is answered with the text and a receipt; the key goes only to the chat function', async () => {
  const env = setup();
  await connect(env);
  env.site.say(call(7));
  await env.site.until(() => env.site.frames('result').length === 1);
  const [result] = env.site.frames('result');
  assert.equal(result.id, 7);
  assert.equal(result.ok, true);
  assert.equal(result.output.text, 'Paris');
  assert.deepEqual(result.receipt, { cost: '0.000 USD', provider: 'api-key' });
  assert.equal(env.calls[0].key, KEY);
  assert.equal(env.calls[0].model, 'a/one:free');
  assert.equal(JSON.stringify(env.site.sent).includes(KEY), false);
  assert.deepEqual(env.events.filter((e) => e.type === 'call').map((e) => e.ok), [true]);
  env.connection.close();
});

test('events say that a call happened, never what was asked or answered', async () => {
  const env = setup();
  await connect(env);
  env.site.say(call(1));
  await env.site.until(() => env.site.frames('result').length === 1);
  const text = JSON.stringify(env.events);
  assert.equal(text.includes('Capital of France'), false);
  assert.equal(text.includes('Paris'), false);
  env.connection.close();
});

test('a call that names a model gets that model, and a model not given is refused', async () => {
  const env = setup({ models: [FREE, { id: 'c/two:free', name: 'Two', free: true }] });
  await connect(env);
  env.site.say(call(1, { params: { model: 'm2' } }));
  env.site.say(call(2, { params: { model: 'm9' } }));
  await env.site.until(() => env.site.frames('result').length === 2);
  assert.equal(env.calls.length, 1);
  assert.equal(env.calls[0].model, 'c/two:free');
  const byId = Object.fromEntries(env.site.frames('result').map((r) => [r.id, r]));
  assert.equal(byId[1].ok, true);
  assert.equal(byId[1].receipt.model, 'm2');
  assert.equal(byId[2].ok, false);
  env.connection.close();
});

test('without a name, models are tried in the visitor\'s order until one answers', async () => {
  const tried = [];
  const chat = async ({ model }) => { tried.push(model); if (model === 'a/one:free') { const e = new Error('x'); e.kind = 'quota'; throw e; } return { text: 'Rome', costUsd: 0 }; };
  const env = setup({ models: [FREE, { id: 'c/two:free', name: 'Two', free: true }], chat });
  await connect(env);
  env.site.say(call(1));
  await env.site.until(() => env.site.frames('result').length === 1);
  assert.deepEqual(tried, ['a/one:free', 'c/two:free']);
  assert.equal(env.site.frames('result')[0].output.text, 'Rome');
  env.connection.close();
});

test('a call is refused, with no text, for a bad skill, bad messages, too much text, an unknown parameter value', async () => {
  const env = setup();
  await connect(env);
  env.site.say(call(1, { skill: 'audio.transcribe' }));
  env.site.say(call(2, { input: { messages: [] } }));
  env.site.say(call(3, { input: { messages: [{ role: 'robot', content: 'x' }] } }));
  env.site.say(call(4, { input: { messages: [{ role: 'user', content: 'x'.repeat(100_001) }] } }));
  env.site.say(call(5, { params: { choices: [1, 2] } }));
  await env.site.until(() => env.site.frames('result').length === 5);
  assert.ok(env.site.frames('result').every((r) => r.ok === false && r.output === undefined));
  assert.equal(env.calls.length, 0);
  env.connection.close();
});

test('a call without an integer id is ignored: there is no one to answer', async () => {
  const env = setup();
  await connect(env);
  env.site.say({ type: 'call', skill: 'text.generate', input: {} });
  env.site.say(call('7'));
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(env.site.frames('result').length, 0);
  env.connection.close();
});

test('a paid model is refused when the budget is nothing, and served within a budget', async () => {
  const none = setup({ models: [PAID] });
  await connect(none);
  none.site.say(call(1));
  await none.site.until(() => none.site.frames('result').length === 1);
  assert.equal(none.site.frames('result')[0].ok, false);
  assert.equal(none.calls.length, 0);
  none.connection.close();

  const some = setup({ models: [PAID], budgetUsd: 0.5, chat: async () => ({ text: 'ok', costUsd: 0.2 }) });
  await connect(some);
  some.site.say(call(1));
  await some.site.until(() => some.site.frames('result').length === 1);
  assert.equal(some.site.frames('result')[0].ok, true);
  assert.equal(some.connection.spentUsd, 0.2);
  assert.equal(some.site.frames('result')[0].receipt.cost, '0.200 USD');
  some.connection.close();
});

test('the site\'s own rate limit (10 a minute in the manifest) holds', async () => {
  let t = 1_000_000;
  const env = setup({ now: () => t });
  await connect(env);
  for (let i = 1; i <= 11; i++) { env.site.say(call(i)); await env.site.until(() => env.site.frames('result').length === i); }
  const results = env.site.frames('result');
  assert.equal(results.slice(0, 10).every((r) => r.ok), true);
  assert.equal(results[10].ok, false);
  t += 61_000;
  env.site.say(call(12));
  await env.site.until(() => env.site.frames('result').length === 12);
  assert.equal(env.site.frames('result')[11].ok, true);
  env.connection.close();
});

test('choices: the answer is one of them, tidied; an answer that is none of them is refused', async () => {
  const env = setup({ chat: async () => ({ text: ' "Yes." ', costUsd: 0 }) });
  await connect(env);
  env.site.say(call(1, { params: { choices: ['yes', 'no'] } }));
  await env.site.until(() => env.site.frames('result').length === 1);
  assert.equal(env.site.frames('result')[0].output.text, 'yes');
  env.connection.close();

  const wrong = setup({ chat: async () => ({ text: 'maybe', costUsd: 0 }) });
  await connect(wrong);
  wrong.site.say(call(1, { params: { choices: ['yes', 'no'] } }));
  await wrong.site.until(() => wrong.site.frames('result').length === 1);
  assert.equal(wrong.site.frames('result')[0].ok, false);
  wrong.connection.close();
  assert.equal(pickChoice(['A', 'B'], '**b**'), 'B');
  assert.equal(pickChoice(['A', 'B'], 'c'), null);
});

test('a streamed call sends chunks, then the whole text as the result', async () => {
  const chat = async ({ onDelta }) => { onDelta('Par'); onDelta('is'); return { text: 'Paris', costUsd: 0 }; };
  const env = setup({ chat });
  await connect(env);
  env.site.say(call(1, { params: { stream: true } }));
  await env.site.until(() => env.site.frames('result').length === 1);
  const chunks = env.site.frames('chunk');
  assert.equal(chunks.map((c) => c.text).join(''), 'Paris');
  assert.ok(chunks.every((c) => c.id === 1 && c.text.length <= 16_000));
  assert.equal(env.site.frames('result')[0].output.text, 'Paris');
  env.connection.close();
});

test('too many calls at once: the ones over the limit are refused', async () => {
  const release = [];
  const chat = () => new Promise((resolve) => release.push(() => resolve({ text: 'x', costUsd: 0 })));
  const manifest = { ...MANIFEST, purpose: { summary: 's', limits: { maxParallelCalls: 1 } } };
  const env = setup({ chat, manifest });
  await connect(env);
  env.site.say(call(1));
  await env.site.until(() => release.length === 1);
  env.site.say(call(2));
  await env.site.until(() => env.site.frames('result').length === 1);
  assert.equal(env.site.frames('result')[0].id, 2);
  assert.equal(env.site.frames('result')[0].ok, false);
  release[0]();
  await env.site.until(() => env.site.frames('result').length === 2);
  env.connection.close();
});

test('no key in the browser: the call is refused and nothing goes out', async () => {
  const env = setup({ getKey: () => null });
  await connect(env);
  env.site.say(call(1));
  await env.site.until(() => env.site.frames('result').length === 1);
  assert.equal(env.site.frames('result')[0].ok, false);
  assert.equal(env.calls.length, 0);
  env.connection.close();
});

test('bye from the site, a dropped socket and close() all end the connection once; close() says bye and aborts calls in flight', async () => {
  const env = setup();
  await connect(env);
  env.site.say({ type: 'bye' });
  assert.equal(env.connection.state, 'closed');
  assert.equal(env.events.filter((e) => e.state === 'closed').length, 1);
  env.connection.close();
  assert.equal(env.events.filter((e) => e.state === 'closed').length, 1);

  let aborted = false;
  const chat = ({ signal }) => new Promise((resolve, reject) => signal.addEventListener('abort', () => { aborted = true; reject(Object.assign(new Error('x'), { name: 'AbortError' })); }));
  const busy = setup({ chat });
  await connect(busy);
  busy.site.say(call(1));
  await new Promise((r) => setTimeout(r, 10));
  busy.connection.close('you disconnected');
  assert.equal(aborted, true);
  assert.equal(busy.site.frames('bye').length, 1);
  assert.equal(busy.site.frames('result').length, 0);
});

test('a binary or oversized or non-JSON frame: ignored, or the connection ends', async () => {
  const env = setup();
  await connect(env);
  env.site.say({ type: 'ping' });
  env.socketBinary = env.site.socket.onmessage({ data: new Uint8Array(3) });
  assert.equal(env.connection.state, 'connected');
  env.site.say('not json');
  assert.equal(env.connection.state, 'closed');
});

test('usd() rounds a price up, never down to nothing', () => {
  assert.equal(usd(0), '0.000000 USD');
  assert.equal(usd(0.0000001), '0.000001 USD');
  assert.equal(usd(1.5), '1.500000 USD');
  assert.equal(usd(-3), '0.000000 USD');
});

test('start() is for once', async () => {
  const env = setup();
  const first = env.connection.start();
  assert.deepEqual(await env.connection.start(), { ok: false, reason: 'already started' });
  env.connection.close();
  await first;
});
