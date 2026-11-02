// The script of the hand-off page that the .NET relay serves (sdk/dotnet/src/BringYourOwnAgent.Relay/HandOffPage.js, spec/CONTRACT-PAIR.md
// section 2): what its buttons do on a phone and on a computer, that it asks the server for the link only from a click, and that it
// never opens anything it was not given. There is no browser here: the script runs in a vm with a very small fake page.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import vm from 'node:vm';

const SCRIPT_PATH = new URL('../../dotnet/src/BringYourOwnAgent.Relay/HandOffPage.js', import.meta.url);
const SCRIPT = readFileSync(SCRIPT_PATH, 'utf8');

const LINK = 'bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2Ficons.example.org%2Fbyoa%2Fws&manifest=eyJieW9hIjoiMC4xIn0&session=AbCdEfGhIjKlMnOpQrStUvWx';
const WEB = 'https://wallet.bringyourownagent.org/';
const TOKEN = 'Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MDEy';
const PHONE = { userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) Mobile' };
const COMPUTER = { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)' };

class Element {
  constructor(attrs = {}) {
    this.attrs = attrs;
    this.listeners = {};
    this.disabled = false;
    this.hidden = false;
    this.value = '';
    this.textContent = '';
  }

  getAttribute(name) { return this.attrs[name] ?? null; }
  addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
  fire(type, event = {}) { for (const fn of this.listeners[type] ?? []) fn(event); }
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** Runs the page's script against a fake page. `answer` is what the server says to the POST: `{ status, json }`, or `Error` for no network. */
function page({ nav = COMPUTER, mode = 'token', web = WEB, answer = { status: 200, json: { link: LINK } }, path = `/byoa/hand/${TOKEN}` } = {}) {
  const body = new Element({
    'data-mode': mode, 'data-post': '/byoa/hand', 'data-web': web,
    'data-used': 'USED', 'data-gone': 'GONE', 'data-wrong': 'WRONG', 'data-slow': 'SLOW', 'data-network': 'NETWORK', 'data-working': 'WORKING', 'data-opened': 'OPENED',
  });
  const ids = { open: new Element(), web: new Element(), short: new Element(), msg: new Element() };
  const calls = [];
  const window = { navigator: nav, location: { pathname: path, href: 'https://icons.example.org' + path } };
  const hrefs = [];
  let href = window.location.href;
  Object.defineProperty(window.location, 'href', { get: () => href, set: (value) => { href = value; hrefs.push(value); } });
  const sandbox = {
    window,
    document: { body, getElementById: (id) => ids[id] ?? null },
    URL,
    fetch: async (url, init) => {
      calls.push({ url, init, body: JSON.parse(init.body) });
      const reply = typeof answer === 'function' ? answer(calls.length) : answer;
      if (reply instanceof Error) throw reply;
      return { status: reply.status, json: async () => { if (reply.json === undefined) throw new Error('not json'); return reply.json; } };
    },
  };
  vm.runInNewContext(SCRIPT, sandbox, { filename: 'HandOffPage.js' });
  return { ...ids, body, calls, hrefs, window };
}

describe('on load', () => {
  test('nothing is asked and nothing is opened before a click: the page cannot tell a wallet from no wallet', () => {
    const p = page();
    assert.equal(p.calls.length, 0);
    assert.deepEqual(p.hrefs, []);
    assert.equal(p.msg.textContent, '');
  });

  test('the script reads no storage or cookie, builds no markup and runs no string as code', () => {
    for (const forbidden of ['localStorage', 'sessionStorage', 'document.cookie', 'innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function', 'XMLHttpRequest', 'WebSocket', 'importScripts']) {
      assert.ok(!SCRIPT.includes(forbidden), forbidden);
    }
  });
});

describe('a phone', () => {
  test('one click: the token goes in the body of a POST, and the web wallet opens with the link in its fragment', async () => {
    const p = page({ nav: PHONE });
    assert.equal(p.open.disabled, false);
    assert.equal(p.web.hidden, true);                                  // one button on a phone
    p.open.fire('click');
    assert.equal(p.msg.textContent, 'WORKING');
    await settle();

    assert.equal(p.calls.length, 1);
    assert.equal(p.calls[0].url, '/byoa/hand');
    assert.equal(p.calls[0].init.method, 'POST');
    assert.equal(p.calls[0].init.headers['Content-Type'], 'application/json');
    assert.deepEqual(p.calls[0].body, { token: TOKEN });
    assert.equal(p.calls[0].init.credentials, 'omit');
    assert.equal(p.calls[0].init.referrerPolicy, 'no-referrer');
    assert.equal(p.calls[0].init.cache, 'no-store');
    assert.deepEqual(p.hrefs, [WEB + '#link=' + LINK]);                // the Android wallet claims this address; the web wallet takes it otherwise
    assert.equal(p.msg.textContent, 'OPENED');
  });

  test('a site with no web wallet gets the link itself on a phone', async () => {
    const p = page({ nav: PHONE, web: '' });
    p.open.fire('click');
    await settle();
    assert.deepEqual(p.hrefs, [LINK]);
  });

  test('a phone is told by its client hints first, and an iPad that says it is a Mac is a phone too', async () => {
    const hinted = page({ nav: { userAgentData: { mobile: true }, userAgent: 'Windows' } });
    hinted.open.fire('click');
    await settle();
    assert.deepEqual(hinted.hrefs, [WEB + '#link=' + LINK]);

    const ipad = page({ nav: { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', maxTouchPoints: 5 } });
    ipad.open.fire('click');
    await settle();
    assert.deepEqual(ipad.hrefs, [WEB + '#link=' + LINK]);
  });
});

describe('a computer', () => {
  test('the main button opens the wallet\'s own link; a second, smaller one is there for the web wallet', async () => {
    const p = page();
    assert.equal(p.web.hidden, false);
    p.open.fire('click');
    await settle();
    assert.deepEqual(p.hrefs, [LINK]);
    assert.equal(p.calls.length, 1);
  });

  test('the web button uses the link the page already has: the server gives it out once', async () => {
    const p = page();
    p.open.fire('click');
    await settle();
    p.web.fire('click');
    await settle();
    assert.equal(p.calls.length, 1);                                   // not asked again
    assert.deepEqual(p.hrefs, [LINK, WEB + '#link=' + LINK]);
  });

  test('the web button first: one request, then the web wallet; the main button afterwards needs no second request either', async () => {
    const p = page();
    p.web.fire('click');
    await settle();
    p.open.fire('click');
    await settle();
    assert.equal(p.calls.length, 1);
    assert.deepEqual(p.hrefs, [WEB + '#link=' + LINK, LINK]);
  });

  test('no web wallet to offer: no second button', () => {
    assert.equal(page({ web: '' }).web.hidden, true);
    assert.equal(page({ web: 'http://wallet.example.org/' }).web.hidden, true);          // not https: not offered
    assert.equal(page({ web: 'javascript:alert(1)' }).web.hidden, true);
    assert.equal(page({ web: 'http://localhost:5500/' }).web.hidden, false);             // a test setup on this computer is
  });

  test('two clicks in a row are one request', async () => {
    const p = page();
    p.open.fire('click');
    p.open.fire('click');
    await settle();
    assert.equal(p.calls.length, 1);
  });
});

describe('when the server says no', () => {
  const cases = [
    ['used', { status: 410, json: { error: 'used' } }, 'USED'],
    ['gone', { status: 410, json: { error: 'gone' } }, 'GONE'],
    ['too many tries', { status: 429, json: { error: 'slow' } }, 'SLOW'],
    ['too many tries without a body', { status: 429 }, 'SLOW'],
    ['something unexpected', { status: 500, json: { error: 'nope' } }, 'NETWORK'],
    ['a page that is not the server', { status: 200, json: { hello: 'world' } }, 'NETWORK'],
    ['no network', new Error('offline'), 'NETWORK'],
  ];
  for (const [name, answer, shown] of cases) {
    test(`${name}: the visitor is told, and nothing is opened`, async () => {
      const p = page({ answer });
      p.open.fire('click');
      await settle();
      assert.equal(p.msg.textContent, shown);
      assert.deepEqual(p.hrefs, []);
    });
  }

  test('a "link" that is not a BYOA link is never opened (a script address, another scheme)', async () => {
    for (const link of ['javascript:alert(1)', 'https://evil.example/', 'bringyourownagent://other?x=1', 42, null]) {
      const p = page({ answer: { status: 200, json: { link } } });
      p.open.fire('click');
      await settle();
      assert.deepEqual(p.hrefs, [], String(link));
      assert.equal(p.msg.textContent, 'NETWORK');
    }
  });

  test('after an error a second click asks again', async () => {
    const p = page({ answer: (n) => (n === 1 ? { status: 429, json: { error: 'slow' } } : { status: 200, json: { link: LINK } }) });
    p.open.fire('click');
    await settle();
    p.open.fire('click');
    await settle();
    assert.equal(p.calls.length, 2);
    assert.deepEqual(p.hrefs, [LINK]);
  });
});

describe('the page where a short code is typed', () => {
  const typed = (p, text) => { p.short.value = text; p.short.fire('input'); };

  test('the button waits for something to be typed, and posts the code as typed', async () => {
    const p = page({ mode: 'short', path: '/byoa/hand' });
    assert.equal(p.open.disabled, true);
    typed(p, 'k7mq 3xpz');
    assert.equal(p.open.disabled, false);
    p.open.fire('click');
    await settle();
    assert.deepEqual(p.calls[0].body, { short: 'k7mq 3xpz' });
    assert.deepEqual(p.hrefs, [LINK]);
  });

  test('Enter does the same; typing nothing, or only spaces and hyphens, keeps the button off', async () => {
    const p = page({ mode: 'short', path: '/byoa/hand' });
    typed(p, ' - ');
    assert.equal(p.open.disabled, true);
    p.short.fire('keydown', { key: 'Enter' });
    await settle();
    assert.equal(p.calls.length, 0);

    typed(p, 'K7MQ-3XPZ');
    p.short.fire('keydown', { key: 'Enter' });
    await settle();
    assert.equal(p.calls.length, 1);
  });

  test('another code is another request: the link of the first one is not reused', async () => {
    const p = page({ mode: 'short', path: '/byoa/hand' });
    typed(p, 'K7MQ-3XPZ');
    p.open.fire('click');
    await settle();
    typed(p, 'ZZZZ-ZZZZ');
    p.open.fire('click');
    await settle();
    assert.equal(p.calls.length, 2);
    assert.deepEqual(p.calls.map((c) => c.body.short), ['K7MQ-3XPZ', 'ZZZZ-ZZZZ']);
  });

  test('a wrong code is told as wrong', async () => {
    const p = page({ mode: 'short', path: '/byoa/hand', answer: { status: 404, json: { error: 'wrong' } } });
    typed(p, 'ZZZZ-ZZZZ');
    p.open.fire('click');
    await settle();
    assert.equal(p.msg.textContent, 'WRONG');
    assert.deepEqual(p.hrefs, []);
  });
});
