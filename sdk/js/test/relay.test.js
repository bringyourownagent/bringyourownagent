// The site's side of the direct link: codes, pairings, the wallet channel and calls (spec/CONTRACT-DIRECT.md).
// A fake wallet (a `ws` client) plays the visitor's side.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { afterEach, describe, test } from 'node:test';
import WebSocket from 'ws';
import { createRelay, encodeManifest } from '../src/site/relay.js';

const MANIFEST = JSON.parse(readFileSync(new URL('../../examples/manifest.json', import.meta.url), 'utf8'));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const running = [];

afterEach(async () => {
  for (const stop of running.splice(0).reverse()) await stop();
});

async function start(options = {}) {
  const clock = { t: 1_000_000 };
  const relay = createRelay({ manifest: MANIFEST, endpoint: 'wss://example.org/byoa/ws', now: () => clock.t, ...options });
  const server = createServer();
  await relay.attach(server, '/byoa/ws');
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const url = `ws://127.0.0.1:${server.address().port}/byoa/ws`;
  const sockets = [];
  running.push(async () => {
    for (const s of sockets) s.terminate();
    relay.closeAll();
    await new Promise((resolve) => server.close(resolve));
    server.closeAllConnections?.();
  });
  return { relay, server, url, clock, wallet: () => fakeWallet(url, sockets) };
}

// A fake wallet: records every frame the server sends, lets the test send its own.
function fakeWallet(url, sockets) {
  const ws = new WebSocket(url);
  sockets.push(ws);
  const frames = [];
  const waiters = [];
  const w = {
    ws, frames, closed: null,
    open: new Promise((resolve, reject) => { ws.on('open', resolve); ws.on('error', reject); }),
    send: (frame) => ws.send(typeof frame === 'string' ? frame : JSON.stringify(frame)),
    next: (ms = 1500) => new Promise((resolve, reject) => {
      if (frames.length > 0) { resolve(frames.shift()); return; }
      const timer = setTimeout(() => reject(new Error('no frame within ' + ms + ' ms')), ms);
      waiters.push((f) => { clearTimeout(timer); resolve(f); });
    }),
    closedWith: new Promise((resolve) => ws.on('close', (code) => { w.closed = code; resolve(code); })),
  };
  ws.on('message', (data) => {
    const f = JSON.parse(data.toString());
    if (waiters.length > 0) waiters.shift()(f); else frames.push(f);
  });
  return w;
}

function hello(session, extra = {}) {
  return { type: 'hello', v: '0.1', session, pairing: null, wallet: { name: 'Test wallet', version: '0' },
    grant: { skills: Object.keys(MANIFEST.skills), expiresAt: '2030-01-01T00:00:00Z' }, ...extra };
}

async function connect(env, pageKey = 'page-1') {
  const { code, link } = env.relay.newCode(pageKey, { ip: '1.1.1.1' });
  const w = env.wallet();
  await w.open;
  w.send(hello(code));
  const welcome = await w.next();
  return { w, code, link, welcome, pageKey };
}

describe('codes and links', () => {
  test('a code is 192 random bits and the link carries the manifest itself', async () => {
    const env = await start();
    const { code, link } = env.relay.newCode('p', { ip: 'a' });
    assert.match(code, /^[A-Za-z0-9_-]{32}$/);
    const url = new URL(link);
    assert.equal(url.protocol, 'bringyourownagent:');
    assert.equal(url.hostname, 'connect');
    assert.equal(url.searchParams.get('v'), '0.1');
    assert.equal(url.searchParams.get('endpoint'), 'wss://example.org/byoa/ws');
    assert.equal(url.searchParams.get('session'), code);
    const manifest = url.searchParams.get('manifest');
    assert.doesNotMatch(manifest, /=/);
    assert.deepEqual(JSON.parse(Buffer.from(manifest, 'base64url').toString('utf8')), MANIFEST);
  });

  test('codes are rate limited per address and the limit lifts after a minute', async () => {
    const env = await start({ codesPerMinute: 3 });
    for (let i = 0; i < 3; i++) assert.ok(env.relay.newCode('p', { ip: 'a' }));
    assert.equal(env.relay.newCode('p', { ip: 'a' }), null);
    assert.ok(env.relay.newCode('p', { ip: 'b' }));
    env.clock.t += 61_000;
    assert.ok(env.relay.newCode('p', { ip: 'a' }));
  });

  test('only the page that asked can ask about its code; cancelling kills it', async () => {
    const env = await start();
    const { code } = env.relay.newCode('mine', { ip: 'a' });
    assert.deepEqual(env.relay.codeState('mine', code), { state: 'waiting' });
    assert.deepEqual(env.relay.codeState('other', code), { state: 'expired' });
    assert.equal(env.relay.cancel('other', code), false);
    assert.equal(env.relay.cancel('mine', code), true);
    assert.deepEqual(env.relay.codeState('mine', code), { state: 'expired' });
    const w = env.wallet();
    await w.open;
    w.send(hello(code));
    assert.deepEqual(await w.next(), { type: 'error', code: 'bad-session' });
    await w.closedWith;
  });

  test('an invalid manifest or an endpoint that is not ws(s) is refused at creation', () => {
    assert.throws(() => createRelay({ manifest: { byoa: 'nope' }, endpoint: 'wss://x/y' }), /invalid BYOA manifest/);
    assert.throws(() => createRelay({ manifest: MANIFEST, endpoint: 'https://x/y' }), /ws:\/\/ or wss:\/\//);
    assert.throws(() => encodeManifest({ big: 'x'.repeat(5000) }), /at most 4096/);
  });
});

describe('the hello', () => {
  test('a good code gets welcome with a pairing and the very manifest string of the link', async () => {
    const env = await start();
    const { link, welcome, code, pageKey } = await connect(env);
    assert.equal(welcome.type, 'welcome');
    assert.match(welcome.pairing, /^[A-Za-z0-9_-]{32}$/);
    assert.equal(welcome.manifest, new URL(link).searchParams.get('manifest'));
    assert.deepEqual(env.relay.codeState(pageKey, code), { state: 'connected', pairing: welcome.pairing });
    assert.equal(env.relay.pairingState(welcome.pairing), 'live');
  });

  test('a code works once', async () => {
    const env = await start();
    const { code } = await connect(env);
    const second = env.wallet();
    await second.open;
    second.send(hello(code));
    assert.deepEqual(await second.next(), { type: 'error', code: 'bad-session' });
    assert.equal(await second.closedWith, 1008);
  });

  test('an old code and a wrong version are refused', async () => {
    const env = await start();
    const { code } = env.relay.newCode('p', { ip: 'a' });
    env.clock.t += 121_000;
    const late = env.wallet();
    await late.open;
    late.send(hello(code));
    assert.equal((await late.next()).code, 'bad-session');

    const fresh = env.relay.newCode('p', { ip: 'b' });
    const wrong = env.wallet();
    await wrong.open;
    wrong.send(hello(fresh.code, { v: '9.9' }));
    assert.equal((await wrong.next()).code, 'bad-session');
  });

  test('a hello with both a session and a pairing, or neither, is refused', async () => {
    const env = await start();
    const { code } = env.relay.newCode('p', { ip: 'a' });
    const both = env.wallet();
    await both.open;
    both.send(hello(code, { pairing: 'x'.repeat(32) }));
    assert.equal((await both.next()).code, 'bad-session');
    const neither = env.wallet();
    await neither.open;
    neither.send(hello(null));
    assert.equal((await neither.next()).code, 'bad-session');
  });

  test('the server says nothing before a valid hello, ignores other frames, and drops a silent connection', async () => {
    const env = await start({ helloMs: 300 });
    const w = env.wallet();
    await w.open;
    w.send({ type: 'ping' });
    w.send({ type: 'call', id: 1 });
    w.send(Buffer.from([1, 2, 3]));
    await sleep(100);
    assert.equal(w.frames.length, 0);
    assert.equal(await w.closedWith, 1008);
  });

  test('malformed JSON closes the connection', async () => {
    const env = await start();
    const w = env.wallet();
    await w.open;
    w.send('{not json');
    assert.equal(await w.closedWith, 1007);
  });

  test('a frame over 4 MB closes the connection', async () => {
    const env = await start();
    const w = env.wallet();
    await w.open;
    w.send('x'.repeat(4 * 1024 * 1024 + 10));
    assert.ok([1009, 1006].includes(await w.closedWith)); // the server closes with 1009; the client may see the socket drop first
  });
});

describe('calls', () => {
  test('a call reaches the wallet and its result comes back', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const asked = wallet.call('text.generate', { messages: [{ role: 'user', content: 'hi' }] }, { choices: ['yes', 'no'] });
    const frame = await w.next();
    assert.equal(frame.type, 'call');
    assert.equal(frame.skill, 'text.generate');
    assert.deepEqual(frame.params, { choices: ['yes', 'no'] });
    w.send({ type: 'result', id: frame.id, ok: true, output: { text: 'yes' }, receipt: { cost: '0.000 USD', provider: 'local' } });
    assert.deepEqual(await asked, { ok: true, output: { text: 'yes' }, receipt: { cost: '0.000 USD', provider: 'local' } });
  });

  test('ok:false, a result without output, a late or made-up id: only the first two are answers', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const a = wallet.call('text.generate', {});
    const b = wallet.call('text.generate', {});
    const fa = await w.next();
    const fb = await w.next();
    w.send({ type: 'result', id: 9999, ok: true, output: { text: 'made up' } });
    w.send({ type: 'result', id: fa.id, ok: false });
    w.send({ type: 'result', id: fb.id, ok: true });
    assert.deepEqual(await a, { ok: false });
    assert.deepEqual(await b, { ok: false });
    w.send({ type: 'result', id: fa.id, ok: true, output: { text: 'twice' } }); // repeated: ignored
    await sleep(50);
  });

  test('the first call waits longer (a cold model); later ones use the short wait', async () => {
    const env = await start({ callTimeoutMs: 120, firstCallTimeoutMs: 600 });
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const first = wallet.call('text.generate', {});
    const f1 = await w.next();
    await sleep(300); // slower than the short wait, faster than the long one
    w.send({ type: 'result', id: f1.id, ok: true, output: { text: 'warm now' } });
    assert.equal((await first).ok, true);
    const second = wallet.call('text.generate', {});
    await w.next();
    const answer = await second; // never answered
    assert.equal(answer.ok, false);
    assert.equal(answer.timedOut, true);
  });

  test('at most 4 calls are in flight; the rest wait for a free place', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const answers = Array.from({ length: 6 }, (_, i) => wallet.call('text.generate', { i }));
    await sleep(150);
    assert.equal(w.frames.length, 4);
    const first = w.frames.shift();
    w.send({ type: 'result', id: first.id, ok: true, output: { text: 'done' } });
    await sleep(100);
    assert.equal(w.frames.length, 4); // 3 left in flight, 1 more has gone out
    for (const f of w.frames.splice(0)) w.send({ type: 'result', id: f.id, ok: true, output: { text: 'done' } });
    await sleep(100);
    for (const f of w.frames.splice(0)) w.send({ type: 'result', id: f.id, ok: true, output: { text: 'done' } });
    assert.ok((await Promise.all(answers)).every((a) => a.ok));
  });

  test('when the wallet goes away, waiting calls end at once', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const asked = wallet.call('text.generate', {});
    await w.next();
    w.ws.close();
    const answer = await asked;
    assert.equal(answer.ok, false);
    assert.equal(answer.cancelled, true);
    assert.equal(env.relay.pairingState(welcome.pairing), 'offline');
    assert.deepEqual(await wallet.call('text.generate', {}), { ok: false }); // no live wallet: no call
  });

  test('bye from the wallet ends the connection', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    w.send({ type: 'bye' });
    await w.closedWith;
    assert.equal(env.relay.walletFor(welcome.pairing), undefined);
  });
});

describe('pairing', () => {
  test('the wallet comes back with its pairing alone; the newest connection wins', async () => {
    const env = await start();
    const { w: old, welcome } = await connect(env);
    const again = env.wallet();
    await again.open;
    again.send(hello(null, { pairing: welcome.pairing }));
    const second = await again.next();
    assert.equal(second.type, 'welcome');
    assert.equal(second.pairing, welcome.pairing);
    assert.deepEqual(await old.next(), { type: 'bye' });
    await old.closedWith;
    assert.equal(env.relay.walletFor(welcome.pairing).socket !== undefined, true);
    const asked = env.relay.walletFor(welcome.pairing).call('text.generate', {});
    const frame = await again.next();
    again.send({ type: 'result', id: frame.id, ok: true, output: { text: 'ok' } });
    assert.equal((await asked).ok, true);
  });

  test('an unknown pairing is refused; forgetting a pairing says bye', async () => {
    const env = await start();
    const stranger = env.wallet();
    await stranger.open;
    stranger.send(hello(null, { pairing: 'nope' }));
    assert.equal((await stranger.next()).code, 'bad-session');

    const { w, welcome } = await connect(env);
    assert.equal(env.relay.forget(welcome.pairing), true);
    assert.deepEqual(await w.next(), { type: 'bye' });
    assert.equal(env.relay.pairingState(welcome.pairing), 'unknown');
  });

  test('a full table pushes out the oldest pairing that has no wallet; a table of live wallets refuses', async () => {
    const env = await start({ maxPairings: 2 });
    const one = await connect(env, 'a');
    const two = await connect(env, 'b');
    one.w.ws.close();
    await one.w.closedWith;
    await sleep(50);
    env.clock.t += 1000;
    const three = await connect(env, 'c'); // pushes out the pairing of the closed wallet
    assert.equal(three.welcome.type, 'welcome');
    assert.equal(env.relay.pairingState(one.welcome.pairing), 'unknown');
    assert.equal(env.relay.pairingState(two.welcome.pairing), 'live');
    const { code } = env.relay.newCode('d', { ip: '9' });
    const w = env.wallet();
    await w.open;
    w.send(hello(code));
    assert.equal((await w.next()).code, 'bad-session');
  });
});
