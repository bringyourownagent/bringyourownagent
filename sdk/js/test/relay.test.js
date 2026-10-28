// The site's side of the direct link: codes, pairings, the wallet channel and calls (spec/CONTRACT-DIRECT.md).
// A fake wallet (a `ws` client) plays the visitor's side.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { CHECK_ALPHABET, connectCheck, createRelay, encodeManifest } from '../src/site/relay.js';
import { MANIFEST, connect, hello, registerCleanup, sleep, start } from './relay-helpers.js';

registerCleanup();

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

  test('the check code is the one the spec computes (vectors from an independent Python script)', () => {
    const vectors = [
      ['', 'G7CP'], ['a', 'G3Y6'], ['test-session-0001', 'PW4A'], ['V8lRbzS0m6b-9A0vV8lRbzS0m6b-9A0v', '88UT'],
      ['kQ3Zr8Yt1pLd5Nw7Xc2Vb9Mj4Hf6Gs0A', 'QZTP'], ['0123456789abcdefghijklmnopqrstuvwxyz', 'X5N7'], ['SESSION-é中\u{1f600}', 'P546'],
    ];
    for (const [session, expected] of vectors) assert.equal(connectCheck(session), expected, session);
    assert.equal(CHECK_ALPHABET, 'ABCDEFGHJKMNPQRSTUVWXYZ23456789');
    assert.throws(() => connectCheck(null), /session code/);
  });

  test('with connectCheck the links say so and every code comes with its check code', async () => {
    const env = await start({ connectCheck: true });
    const issued = env.relay.newCode('p', { ip: 'a' });
    assert.equal(issued.check, connectCheck(issued.code));
    const manifest = JSON.parse(Buffer.from(new URL(issued.link).searchParams.get('manifest'), 'base64url').toString('utf8'));
    assert.deepEqual(manifest, { ...MANIFEST, connect: { check: true } });
    const w = env.wallet();
    await w.open;
    w.send(hello(issued.code));
    const welcome = await w.next();
    assert.equal(welcome.manifest, new URL(issued.link).searchParams.get('manifest'), 'the welcome echoes the manifest of the link');
    const plain = await start();
    assert.equal('check' in plain.relay.newCode('p', { ip: 'a' }), false);
    const declared = await start({ manifest: { ...MANIFEST, connect: { check: true } } });
    assert.match(declared.relay.newCode('p', { ip: 'a' }).check, /^[A-Z2-9]{4}$/);
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

  test('a full table pushes out the oldest pairing that has no wallet first', async () => {
    const env = await start({ maxPairings: 2 });
    const one = await connect(env, 'a');
    const two = await connect(env, 'b');
    one.w.ws.close();
    await one.w.closedWith;
    await sleep(50);
    env.clock.t += 1000;
    const evicted = [];
    env.relay.on('evicted', (p) => evicted.push(p));
    const three = await connect(env, 'c'); // pushes out the pairing of the closed wallet
    assert.equal(three.welcome.type, 'welcome');
    assert.equal(env.relay.pairingState(one.welcome.pairing), 'unknown');
    assert.equal(env.relay.pairingState(two.welcome.pairing), 'live');
    assert.deepEqual(evicted, [one.welcome.pairing]);
  });

  test('when every wallet is live, the one used least recently goes (a connection that only pings is never used)', async () => {
    const env = await start({ maxPairings: 2 });
    const idle = await connect(env, 'a'); // connected first, never used for a call
    env.clock.t += 1000;
    const busy = await connect(env, 'b');
    env.clock.t += 1000;
    const answered = env.relay.walletFor(busy.welcome.pairing).call('text.generate', {});
    busy.w.send({ type: 'result', id: (await busy.w.next()).id, ok: true, output: { text: 'x' } });
    assert.equal((await answered).ok, true);
    env.clock.t += 1000;
    const third = await connect(env, 'c');
    assert.equal(third.welcome.type, 'welcome');
    assert.deepEqual(await idle.w.next(), { type: 'bye' });
    assert.equal(env.relay.pairingState(idle.welcome.pairing), 'unknown');
    assert.equal(env.relay.pairingState(busy.welcome.pairing), 'live');
  });
});
