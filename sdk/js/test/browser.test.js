// The page's helpers: the extension path and the connect-without-extension flow (a fake `window` and a fake `fetch`).

import assert from 'node:assert/strict';
import { afterEach, describe, test } from 'node:test';
import {
  callWithExtension, connectWithoutExtension, hasExtension, openWalletLink, requestWithExtension, whenExtensionReady,
} from '../src/site/browser.js';

const realFetch = globalThis.fetch;
afterEach(() => { delete globalThis.window; globalThis.fetch = realFetch; });

function fakeFetch(routes) {
  const seen = [];
  globalThis.fetch = async (url, init = {}) => {
    seen.push({ url: String(url), method: init.method ?? 'GET', body: init.body });
    const route = routes(String(url), init);
    if (route === 'fail') throw new Error('network');
    return { ok: route.ok ?? true, status: route.status ?? 200, json: async () => route.json };
  };
  return seen;
}

describe('extension path', () => {
  test('hasExtension is false without a window or without window.byoa', () => {
    assert.equal(hasExtension(), false);
    globalThis.window = {};
    assert.equal(hasExtension(), false);
    globalThis.window = { byoa: { request() {} } };
    assert.equal(hasExtension(), true);
  });

  test('request returns the grant, or null for every kind of "no"', async () => {
    globalThis.window = { byoa: { request: async () => ({ grantId: 'g', skills: ['text.generate'], expiresAt: 'x' }) } };
    assert.equal((await requestWithExtension({})).grantId, 'g');
    globalThis.window = { byoa: { request: async () => null } };
    assert.equal(await requestWithExtension({}), null);
    globalThis.window = { byoa: { request: async () => { throw new Error('boom'); } } };
    assert.equal(await requestWithExtension({}), null);
    delete globalThis.window;
    assert.equal(await requestWithExtension({}), null);
  });

  test('call never throws: an answer or { ok: false }', async () => {
    globalThis.window = { byoa: { request() {}, call: async (id, skill) => ({ ok: true, output: { text: skill }, receipt: {} }) } };
    assert.deepEqual((await callWithExtension({ grantId: 'g' }, 'text.generate', {})).output, { text: 'text.generate' });
    assert.deepEqual(await callWithExtension(null, 'text.generate', {}), { ok: false });
    globalThis.window.byoa.call = async () => { throw new Error('boom'); };
    assert.deepEqual(await callWithExtension({ grantId: 'g' }, 'text.generate', {}), { ok: false });
    globalThis.window.byoa.call = async () => ({ ok: false });
    assert.deepEqual(await callWithExtension({ grantId: 'g' }, 'text.generate', {}), { ok: false });
    globalThis.window.byoa.call = async () => ({ ok: false, reason: 'limit', provider: 'secret' });
    assert.deepEqual(await callWithExtension({ grantId: 'g' }, 'text.generate', {}), { ok: false, reason: 'limit' });       // the closed list, nothing else
    globalThis.window.byoa.call = async () => ({ ok: false, reason: 'the model is not installed' });
    assert.deepEqual(await callWithExtension({ grantId: 'g' }, 'text.generate', {}), { ok: false });
  });

  test('openWalletLink opens only a BYOA link', () => {
    globalThis.window = { location: { href: '' } };
    openWalletLink('bringyourownagent://connect?v=0.1');
    assert.equal(window.location.href, 'bringyourownagent://connect?v=0.1');
    assert.throws(() => openWalletLink('https://example.org'), /not a BYOA link/);
    assert.throws(() => openWalletLink('byoa://connect'), /not a BYOA link/);
    assert.throws(() => openWalletLink('bringyourownagent://other'), /not a BYOA link/);
  });
});

describe('connect without the extension', () => {
  test('asks for a code, opens the link, and waits until the wallet has come', async () => {
    let polls = 0;
    const seen = fakeFetch((url) => {
      if (url === '/byoa/code') return { json: { code: 'c1', link: 'bringyourownagent://connect?session=c1' } };
      polls++;
      return { json: polls < 3 ? { state: 'waiting' } : { state: 'connected', pairing: 'P' } };
    });
    const opened = [];
    const result = await connectWithoutExtension({ pollMs: 5, open: (link) => opened.push(link) });
    assert.deepEqual(result, { state: 'connected', pairing: 'P' });
    assert.deepEqual(opened, ['bringyourownagent://connect?session=c1']);
    assert.equal(seen[0].method, 'POST');
    assert.equal(polls, 3);
  });

  test('onIssued hears the code, the link and the check code before the link opens', async () => {
    const order = [];
    fakeFetch((url) => (url === '/byoa/code'
      ? { json: { code: 'c', link: 'bringyourownagent://x', check: 'K7MQ' } } : { json: { state: 'connected', pairing: 'P' } }));
    const result = await connectWithoutExtension({
      pollMs: 1, onIssued: (issued) => order.push(['issued', issued]), open: (link) => order.push(['open', link]),
    });
    assert.deepEqual(result, { state: 'connected', pairing: 'P' });
    assert.deepEqual(order, [['issued', { code: 'c', link: 'bringyourownagent://x', check: 'K7MQ' }], ['open', 'bringyourownagent://x']]);
    fakeFetch((url) => (url === '/byoa/code' ? { json: { session: 's', link: 'bringyourownagent://y' } } : { json: { state: 'expired' } }));
    const seen = [];
    await connectWithoutExtension({ pollMs: 1, open() {}, onIssued: (issued) => { seen.push(issued); throw new Error('page bug'); } });
    assert.deepEqual(seen, [{ code: 's', link: 'bringyourownagent://y' }], 'no check code: none is passed, and a throwing callback changes nothing');
  });

  test('an expired code, a server error and a refused link are plain answers', async () => {
    fakeFetch((url) => (url === '/byoa/code' ? { json: { code: 'c', link: 'bringyourownagent://x' } } : { json: { state: 'expired' } }));
    assert.deepEqual(await connectWithoutExtension({ pollMs: 1, open() {} }), { state: 'expired' });
    fakeFetch(() => ({ ok: false, status: 429, json: {} }));
    assert.deepEqual(await connectWithoutExtension({ open() {} }), { state: 'error' });
    fakeFetch(() => 'fail');
    assert.deepEqual(await connectWithoutExtension({ open() {} }), { state: 'error' });
    fakeFetch(() => ({ json: { code: 'c', link: 'bringyourownagent://x' } }));
    assert.deepEqual(await connectWithoutExtension({ open() { throw new Error('blocked'); } }), { state: 'error' });
  });

  test('after the waiting time it says timeout and tells the server to forget the code', async () => {
    const seen = fakeFetch((url) => (url === '/byoa/code' ? { json: { code: 'c', link: 'bringyourownagent://x' } } : { json: { state: 'waiting' } }));
    const result = await connectWithoutExtension({ waitMs: 40, pollMs: 5, open() {} });
    assert.deepEqual(result, { state: 'timeout' });
    const cancel = seen.at(-1);
    assert.equal(cancel.url, '/byoa/cancel');
    assert.equal(JSON.parse(cancel.body).code, 'c');
  });

  test('aborting stops the wait', async () => {
    fakeFetch((url) => (url === '/byoa/code' ? { json: { code: 'c', link: 'bringyourownagent://x' } } : { json: { state: 'waiting' } }));
    const controller = new AbortController();
    setTimeout(() => controller.abort(), 30);
    const result = await connectWithoutExtension({ waitMs: 5000, pollMs: 10, signal: controller.signal, open() {} });
    assert.deepEqual(result, { state: 'cancelled' });
  });

  test('a second click while one is waiting opens no second link', async () => {
    let polls = 0;
    fakeFetch((url) => {
      if (url === '/byoa/code') return { json: { code: 'c', link: 'bringyourownagent://connect?session=c' } };
      return { json: ++polls < 3 ? { state: 'waiting' } : { state: 'connected', pairing: 'P' } };
    });
    const opened = [];
    const first = connectWithoutExtension({ pollMs: 5, open: (l) => opened.push(l) });
    const second = connectWithoutExtension({ pollMs: 5, open: (l) => opened.push(l) });
    assert.equal(first, second);
    assert.deepEqual(await first, { state: 'connected', pairing: 'P' });
    assert.equal(opened.length, 1);
    const again = connectWithoutExtension({ pollMs: 5, open: (l) => opened.push(l) }); // after it ended, a new one starts
    assert.notEqual(again, first);
    await again;
    assert.equal(opened.length, 2);
  });

  test('the first site\'s name for the code, "session", is accepted; a poll time of 0 is no busy loop', async () => {
    let polls = 0;
    fakeFetch((url) => {
      if (url === '/byoa/code') return { json: { session: 's1', link: 'bringyourownagent://connect?session=s1' } };
      polls++;
      assert.match(url, /code=s1/);
      return { json: { state: 'connected', pairing: 'P' } };
    });
    assert.deepEqual(await connectWithoutExtension({ pollMs: 0, open() {} }), { state: 'connected', pairing: 'P' });
    assert.equal(polls, 1);
  });

  test('no abort listeners are left behind by the polling', async () => {
    fakeFetch((url) => (url === '/byoa/code' ? { json: { code: 'c', link: 'bringyourownagent://connect?x' } } : { json: { state: 'waiting' } }));
    const controller = new AbortController();
    let added = 0;
    let removed = 0;
    const add = controller.signal.addEventListener.bind(controller.signal);
    const remove = controller.signal.removeEventListener.bind(controller.signal);
    controller.signal.addEventListener = (...a) => { added++; return add(...a); };
    controller.signal.removeEventListener = (...a) => { removed++; return remove(...a); };
    await connectWithoutExtension({ waitMs: 120, pollMs: 10, signal: controller.signal, open() {} });
    assert.ok(added >= 5);
    assert.equal(added, removed);
  });
});

describe('whenExtensionReady', () => {
  test('is true at once when the extension is there, true when it announces itself, false when it never does', async () => {
    globalThis.window = { byoa: { request() {} } };
    assert.equal(await whenExtensionReady(50), true);
    const listeners = {};
    globalThis.window = {
      addEventListener: (name, fn) => { listeners[name] = fn; },
      removeEventListener: (name) => { delete listeners[name]; },
    };
    const late = whenExtensionReady(500);
    window.byoa = { request() {} };
    listeners['byoa:ready']();
    assert.equal(await late, true);
    delete window.byoa;
    assert.equal(await whenExtensionReady(30), false);
    assert.deepEqual(Object.keys(listeners), []);
  });
});
