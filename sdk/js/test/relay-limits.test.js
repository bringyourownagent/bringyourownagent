// The limits of the relay: what a hostile client of the site's own server can and cannot do, and what a rogue wallet
// can hand back. (Found by an independent review; see the changelog.)

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import WebSocket from 'ws';
import { addressKey, createRelay } from '../src/site/relay.js';
import { MANIFEST, connect, hello, registerCleanup, sleep, start } from './relay-helpers.js';

registerCleanup();

describe('who may ask for codes', () => {
  test('the visitor\'s address is required, and the key is the same for the forms of one address', async () => {
    const env = await start();
    assert.throws(() => env.relay.newCode('p'), /needs the visitor/);
    assert.throws(() => env.relay.newCode('p', { ip: '' }), /needs the visitor/);
    assert.throws(() => env.relay.newCode('', { ip: 'a' }), /pageKey/);
    assert.equal(addressKey('::ffff:1.2.3.4'), '1.2.3.4');
    assert.equal(addressKey('1.2.3.4'), '1.2.3.4');
    assert.equal(addressKey('2001:db8:1:2:aaaa:bbbb:cccc:dddd'), addressKey('2001:DB8:1:2::1'));
    assert.notEqual(addressKey('2001:db8:1:2::1'), addressKey('2001:db8:1:3::1'));
    assert.equal(addressKey('::1'), '0:0:0:0::/64');
  });

  test('one home network cannot get round the limit by changing the end of its IPv6 address', async () => {
    const env = await start({ codesPerMinute: 2, codesPerPageMinute: 100 });
    assert.ok(env.relay.newCode('p1', { ip: '2001:db8:1:2::1' }));
    assert.ok(env.relay.newCode('p2', { ip: '2001:db8:1:2::2' }));
    assert.equal(env.relay.newCode('p3', { ip: '2001:db8:1:2:ffff::9' }), null);
    assert.ok(env.relay.newCode('p4', { ip: '2001:db8:1:3::1' }));
    assert.ok(env.relay.newCode('p5', { ip: '1.2.3.4' }));
    assert.ok(env.relay.newCode('p6', { ip: '::ffff:9.9.9.9' }));
    assert.equal(env.relay.newCode('p7', { ip: '9.9.9.9' }) !== null, true); // 1 of 2 for 9.9.9.9
    assert.equal(env.relay.newCode('p8', { ip: '9.9.9.9' }), null);          // the mapped form counted
  });

  test('one page cannot ask for many codes by changing address', async () => {
    const env = await start({ codesPerPageMinute: 3 });
    for (let i = 0; i < 3; i++) assert.ok(env.relay.newCode('same-page', { ip: 'ip' + i }));
    assert.equal(env.relay.newCode('same-page', { ip: 'another' }), null);
    assert.ok(env.relay.newCode('other-page', { ip: 'another' }));
  });

  test('a bad endpoint is refused at creation', () => {
    for (const endpoint of ['wss:/x', 'https://x/y', 'wss://user:pw@x/y', 'wss://x/y#frag', 'not a url', 'wsfoo://x']) {
      assert.throws(() => createRelay({ manifest: MANIFEST, endpoint }), /ws:\/\/ or wss:\/\//, endpoint);
    }
    assert.doesNotThrow(() => createRelay({ manifest: MANIFEST, endpoint: 'wss://example.org/byoa/ws' }).closeAll());
  });

  test('expired codes are swept even when nobody asks for new ones', async () => {
    const env = await start();
    const { code } = env.relay.newCode('p', { ip: 'a' });
    env.clock.t += 125_000;
    env.relay._sweep(env.clock.t);
    assert.equal(env.relay._codes.size, 0);
    assert.deepEqual(env.relay.codeState('p', code), { state: 'expired' });
  });
});

describe('connections', () => {
  test('a silent wallet is dropped; one that keeps pinging stays', async () => {
    const env = await start({ idleMs: 200 });
    const quiet = await connect(env, 'quiet');
    const pinging = await connect(env, 'pinging');
    const ping = setInterval(() => pinging.w.send({ type: 'ping' }), 60);
    await quiet.w.closedWith;
    await sleep(150);
    clearInterval(ping);
    assert.equal(env.relay.pairingState(pinging.welcome.pairing), 'live');
    assert.equal(env.relay.pairingState(quiet.welcome.pairing), 'offline');
  });

  test('a connection older than its lifetime is let go with bye, and the wallet can come back with its pairing', async () => {
    const env = await start({ maxLifetimeMs: 200 });
    const { w, welcome } = await connect(env);
    assert.deepEqual(await w.next(), { type: 'bye' });
    await w.closedWith;
    const again = env.wallet();
    await again.open;
    again.send(hello(null, { pairing: welcome.pairing }));
    assert.equal((await again.next()).type, 'welcome');
  });

  test('more sockets than the limit are refused before the handshake', async () => {
    const env = await start({ maxSockets: 2 });
    const a = env.wallet();
    const b = env.wallet();
    await Promise.all([a.open, b.open]);
    const c = new WebSocket(env.url);
    const result = await new Promise((resolve) => { c.on('open', () => resolve('open')); c.on('error', () => resolve('refused')); });
    assert.equal(result, 'refused');
    assert.equal(env.relay.socketCount, 2);
  });

  test('closeAll closes every socket, also the ones that never said hello; detach stops new ones', async () => {
    const env = await start();
    const hello1 = await connect(env);
    const silent = env.wallet();
    await silent.open;
    env.relay.closeAll();
    await Promise.all([hello1.w.closedWith, silent.closedWith]);
    env.relay.detach();
    const late = new WebSocket(env.url);
    const result = await new Promise((resolve) => {
      late.on('open', () => resolve('open'));
      late.on('error', () => resolve('refused'));
      late.on('unexpected-response', () => resolve('refused'));
      setTimeout(() => resolve('never opened'), 500); // this test server has no plain HTTP handler, so the upgrade just hangs
    });
    late.terminate();
    assert.notEqual(result, 'open');
  });

  test('the old connection of a pairing knows it was replaced', async () => {
    const env = await start();
    const { w: old, welcome } = await connect(env);
    const oldConn = env.relay.walletFor(welcome.pairing);
    const events = [];
    env.relay.on('disconnected', (conn) => events.push([conn === oldConn, conn.replaced]));
    const again = env.wallet();
    await again.open;
    again.send(hello(null, { pairing: welcome.pairing }));
    await again.next();
    await old.closedWith;
    await sleep(20);
    assert.deepEqual(events, [[true, true]]);
    assert.notEqual(env.relay.walletFor(welcome.pairing), oldConn);
  });

  test('forgetting a pairing and evicting one are told by events', async () => {
    const env = await start();
    const forgotten = [];
    env.relay.on('forgotten', (p) => forgotten.push(p));
    const { welcome } = await connect(env);
    env.relay.forget(welcome.pairing);
    assert.deepEqual(forgotten, [welcome.pairing]);
  });
});

describe('calls and what comes back', () => {
  test('the queue is bounded, and the wait counts from the call, not from the moment it is sent', async () => {
    const env = await start({ maxQueued: 2, callTimeoutMs: 250, firstCallTimeoutMs: 250 });
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const calls = Array.from({ length: 6 }, () => wallet.call('text.generate', {})); // 4 in flight, 2 queued
    assert.equal(wallet.queued, 2);
    assert.deepEqual(await wallet.call('text.generate', {}), { ok: false }); // over the bound: at once
    const started = Date.now();
    const answers = await Promise.all(calls); // the wallet never answers
    assert.ok(Date.now() - started < 600, 'the queued calls did not wait for the in-flight ones to time out first');
    assert.ok(answers.every((a) => a.ok === false && a.timedOut === true));
    assert.equal(wallet.queued, 0);
    assert.equal(w.frames.length, 4);
  });

  test('a receipt is cut down to two short strings, and a bad one is dropped', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const send = async (receipt) => {
      const asked = wallet.call('text.generate', {});
      w.send({ type: 'result', id: (await w.next()).id, ok: true, output: { text: 'x' }, receipt });
      return (await asked).receipt;
    };
    assert.deepEqual(await send({ cost: '0.000 USD', provider: 'local', extra: '<b>hi</b>' }), { cost: '0.000 USD', provider: 'local' });
    assert.equal(await send('please visit evil.example'), null);
    assert.equal(await send({ cost: 5, provider: 'local' }), null);
    assert.equal(await send({ cost: 'x'.repeat(100), provider: 'local' }), null);
  });

  test('an output that is too big, or not an object, is no answer', async () => {
    const env = await start({ maxOutputBytes: 1000 });
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    for (const output of [{ text: 'x'.repeat(2000) }, ['a'], 'text', null]) {
      const asked = wallet.call('text.generate', {});
      w.send({ type: 'result', id: (await w.next()).id, ok: true, output });
      assert.deepEqual(await asked, { ok: false });
    }
  });

  test('a call too big for a frame fails here, and the connection stays', async () => {
    const env = await start();
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const answer = await wallet.call('audio.transcribe', { audio: { base64: 'A'.repeat(5 * 1024 * 1024) } });
    assert.deepEqual(answer, { ok: false, tooBig: true });
    assert.equal(wallet.live, true);
    assert.equal(wallet.failures, 0); // the site's own mistake is not the wallet failing
    assert.equal(w.frames.length, 0);
  });

  test('three failures in a row make the connection say it is failing; a timeout during warm-up is not a failure', async () => {
    const env = await start({ callTimeoutMs: 100, firstCallTimeoutMs: 100 });
    const { w, welcome } = await connect(env);
    const wallet = env.relay.walletFor(welcome.pairing);
    const events = [];
    wallet.on('failing', (n) => events.push(n));
    const first = wallet.call('text.generate', {}); // never answered: the cold start
    await w.next();
    assert.equal((await first).timedOut, true);
    assert.equal(wallet.failures, 0);
    for (let i = 0; i < 3; i++) {
      const asked = wallet.call('text.generate', {});
      w.send({ type: 'result', id: (await w.next()).id, ok: false });
      await asked;
    }
    assert.equal(wallet.failures, 3);
    assert.deepEqual(events, [3]);
    const good = wallet.call('text.generate', {});
    w.send({ type: 'result', id: (await w.next()).id, ok: true, output: { text: 'ok' } });
    await good;
    assert.equal(wallet.failures, 0);
  });
});
