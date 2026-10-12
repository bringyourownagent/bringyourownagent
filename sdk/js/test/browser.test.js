// The page's helpers: the extension path and the connect-without-extension flow (a fake `window` and a fake `fetch`).

import assert from 'node:assert/strict';
import { afterEach, describe, test } from 'node:test';
import {
  callWithExtension, connectWithoutExtension, hasExtension, openWalletLink, requestWithExtension,
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
  });

  test('openWalletLink opens only a BYOA link', () => {
    globalThis.window = { location: { href: '' } };
    openWalletLink('bringyourownagent://connect?v=0.1');
    assert.equal(window.location.href, 'bringyourownagent://connect?v=0.1');
    assert.throws(() => openWalletLink('https://example.org'), /not a BYOA link/);
    assert.throws(() => openWalletLink('byoa://connect'), /not a BYOA link/);
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
});
