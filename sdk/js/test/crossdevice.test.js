// The "Open on another device" block: what it offers, what it hides on a phone, what its buttons do, and that nothing is read as
// markup. There is no browser here, so a very small fake DOM stands in for the page.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import {
  checkCodeOf, createCrossDeviceBlock, DEFAULT_LABELS, DEFAULT_WEB_WALLET, isMobileBrowser, planCrossDevice, sessionOfLink, webWalletUrl,
} from '../src/site/crossdevice.js';
import { encodeQr, qrPath } from '../src/site/qr.js';

class FakeNode {
  constructor(tag) {
    this.tag = tag;
    this.attrs = {};
    this.children = [];
    this.listeners = {};
    this.value = '';
    this._text = '';
    this.selected = false;
  }

  setAttribute(name, value) { this.attrs[name] = String(value); }
  getAttribute(name) { return this.attrs[name]; }
  append(...nodes) { this.children.push(...nodes); }
  addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
  async click() { for (const fn of this.listeners.click ?? []) await fn({ type: 'click' }); }
  select() { this.selected = true; }
  setSelectionRange() {}
  get textContent() { return this._text + this.children.map((c) => (typeof c === 'string' ? c : c.textContent)).join(''); }
  set textContent(text) { this._text = text; this.children = []; }
  // the block must never use it: setting it is a test failure
  set innerHTML(html) { throw new Error(`innerHTML was set: ${html}`); }
}

const fakeDocument = () => ({
  executed: [],
  execResult: true,
  createElement: (tag) => new FakeNode(tag),
  createElementNS: (ns, tag) => Object.assign(new FakeNode(tag), { ns }),
  execCommand(command) { this.executed.push(command); return this.execResult; },
});

const walk = (node, visit) => { visit(node); for (const c of node.children) if (typeof c !== 'string') walk(c, visit); };
const all = (root, pred) => { const found = []; walk(root, (n) => { if (pred(n)) found.push(n); }); return found; };
const role = (root, name) => all(root, (n) => n.attrs['data-byoa-role'] === name)[0] ?? null;
const text = (root) => root.textContent;

const SESSION = 'AbCdEfGhIjKlMnOpQrStUvWx';
const LINK = 'bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2Fgeo.example.org%2Fbyoa%2Fws&manifest=eyJieW9hIjoiMC4xIn0&session=' + SESSION;
const HANDOFF = 'https://geo.example.org/byoa/hand/Zm9vYmFyYmF6cXV4MTIzNDU2';
const WALLET = 'https://wallet.example.org/';

const desktop = (extra = {}) => ({ userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)', ...extra });
const phone = (extra = {}) => ({ userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) Mobile', ...extra });

function block(options, nav = desktop()) {
  const document = fakeDocument();
  const root = createCrossDeviceBlock({ link: LINK, check: 'K7MQ', webWallet: null, ...options, document, navigator: nav });
  return { root, document };
}

describe('the plan', () => {
  test('a link alone: the link is copied and shared, and is the QR code', () => {
    assert.deepEqual(planCrossDevice({ link: LINK }), { text: LINK, textKind: 'link', walletUrl: null, qrText: LINK, qrKind: 'link' });
  });

  test('a hand-off address replaces the link in the text and in the QR code', () => {
    assert.deepEqual(planCrossDevice({ link: LINK, handoff: HANDOFF }), { text: HANDOFF, textKind: 'handoff', walletUrl: null, qrText: HANDOFF, qrKind: 'handoff' });
  });

  test('a web wallet makes the QR code open it, and keeps the text as it was', () => {
    const plan = planCrossDevice({ link: LINK, handoff: HANDOFF, webWallet: WALLET });
    assert.equal(plan.text, HANDOFF);
    assert.equal(plan.qrKind, 'webwallet');
    assert.equal(plan.qrText, plan.walletUrl);
    assert.equal(plan.walletUrl, WALLET + '#link=' + LINK);
  });

  test('anything that is not a BYOA link, or not https, is refused', () => {
    assert.throws(() => planCrossDevice({ link: 'https://example.org/' }), /not a BYOA link/);
    assert.throws(() => planCrossDevice({ link: LINK + ' x' }), /characters a link cannot have/);
    assert.throws(() => planCrossDevice({ link: LINK + '"<script>' }), /characters a link cannot have/);
    assert.throws(() => planCrossDevice({ link: LINK, handoff: 'http://geo.example.org/hand/x' }), /must be https/);
    assert.throws(() => planCrossDevice({ link: LINK, webWallet: 'http://wallet.example.org/' }), /must be https/);
    assert.doesNotThrow(() => planCrossDevice({ link: LINK, webWallet: 'http://localhost:5500/' }));
  });
});

describe('web wallet address', () => {
  test('the link goes in the fragment as it is, not encoded again', () => {
    assert.equal(webWalletUrl('https://wallet.example.org/app/?x=1#old', LINK), 'https://wallet.example.org/app/#link=' + LINK);
  });
});

describe('helpers', () => {
  test('a phone is told from a computer', () => {
    assert.equal(isMobileBrowser(desktop()), false);
    assert.equal(isMobileBrowser(phone()), true);
    assert.equal(isMobileBrowser({ userAgentData: { mobile: true }, userAgent: 'x' }), true);
    assert.equal(isMobileBrowser({ userAgentData: { mobile: false }, userAgent: 'Android' }), false);
    assert.equal(isMobileBrowser({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)' }), true);
    assert.equal(isMobileBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', maxTouchPoints: 5 }), true);   // an iPad
    assert.equal(isMobileBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', maxTouchPoints: 0 }), false);
    assert.equal(isMobileBrowser(undefined), false);
  });

  test('the session of a link', () => {
    assert.equal(sessionOfLink(LINK), SESSION);
    assert.equal(sessionOfLink('bringyourownagent://connect'), null);
    assert.equal(sessionOfLink(null), null);
  });

  test('the check code is the one the spec defines', async () => {
    // the same vectors as the wallet's tests, made by an independent script
    for (const [session, code] of [['a', 'G3Y6'], ['session-1', '6FR5'], ['AAAAAAAAAAAAAAAAAAAAAAAA', 'SD95'], ['Zm9v', '5ZT8'], ['k7MQ-_9', '4H5J']]) {
      assert.equal(await checkCodeOf(session), code, session);
    }
  });
});

describe('the block', () => {
  test('shows the check code, the link as text, a copy button and a QR code on a computer', () => {
    const { root } = block();
    assert.equal(text(role(root, 'check')), 'K7MQ');
    assert.ok(text(root).includes(DEFAULT_LABELS.checkHow));
    assert.equal(role(root, 'text').value, LINK);
    assert.equal(role(root, 'text').attrs.readonly, '');
    assert.ok(role(root, 'copy'));
    assert.equal(role(root, 'share'), null);                             // this browser has no Web Share API
    assert.equal(role(root, 'webwallet'), null);
    const qr = role(root, 'qr');
    assert.ok(qr);
    const svg = all(qr, (n) => n.tag === 'svg')[0];
    assert.equal(svg.attrs.role, 'img');
    assert.equal(svg.attrs['aria-label'], DEFAULT_LABELS.qrAlt);
    const path = all(svg, (n) => n.tag === 'path')[0];
    assert.equal(path.attrs.d, qrPath(encodeQr(LINK, 'L'), 4));         // the QR code carries the link
    assert.ok(text(qr).includes(DEFAULT_LABELS.qrLink));
  });

  test('without a check code there is no code and no line about it', () => {
    const { root } = block({ check: undefined });
    assert.equal(role(root, 'check'), null);
    assert.ok(!text(root).includes(DEFAULT_LABELS.checkHow));
  });

  test('a check code that is not four characters of the alphabet is a mistake of the site, not shown', () => {
    for (const bad of ['k7mq', 'K7M', 'K7MQX', 'K7M0', 'K7MI', '<b>x', '']) {
      assert.throws(() => block({ check: bad }), /check must be four characters/, bad);
    }
  });

  test('on a phone the QR code is left out (a phone cannot scan itself), unless the site insists', () => {
    assert.equal(role(block({}, phone()).root, 'qr'), null);
    assert.ok(role(block({ showQr: true }, phone()).root, 'qr'));
    assert.equal(role(block({ showQr: false }).root, 'qr'), null);
  });

  test('a hand-off address is what is shown, and its words are the hand-off words', () => {
    const { root } = block({ handoff: HANDOFF });
    assert.equal(role(root, 'text').value, HANDOFF);
    assert.ok(text(root).includes(DEFAULT_LABELS.handoffHow));
    assert.ok(!text(root).includes(DEFAULT_LABELS.linkHow));
    assert.ok(text(role(root, 'qr')).includes(DEFAULT_LABELS.qrHandoff));
  });

  test('a web wallet is an anchor that opens in a new tab without a referrer, and the QR code opens it', () => {
    const { root } = block({ webWallet: WALLET });
    const a = role(root, 'webwallet');
    assert.equal(a.tag, 'a');
    assert.equal(a.attrs.href, WALLET + '#link=' + LINK);
    assert.equal(a.attrs.target, '_blank');
    assert.match(a.attrs.rel, /noopener/);
    assert.match(a.attrs.rel, /noreferrer/);
    const path = all(role(root, 'qr'), (n) => n.tag === 'path')[0];
    assert.equal(path.attrs.d, qrPath(encodeQr(WALLET + '#link=' + LINK, 'L'), 4));
    assert.ok(text(role(root, 'qr')).includes(DEFAULT_LABELS.qrWebWallet));
  });

  test('with no choice made the project\'s web wallet is offered and opens from the QR code (a phone camera does not open a bringyourownagent link)', () => {
    const { root } = block({ webWallet: undefined });
    const a = role(root, 'webwallet');
    assert.equal(DEFAULT_WEB_WALLET, 'https://wallet.bringyourownagent.org/');
    assert.equal(a.attrs.href, DEFAULT_WEB_WALLET + '#link=' + LINK);
    const path = all(role(root, 'qr'), (n) => n.tag === 'path')[0];
    assert.equal(path.attrs.d, qrPath(encodeQr(DEFAULT_WEB_WALLET + '#link=' + LINK, 'L'), 4));
  });

  test('a site that wants no web wallet says null, and its own address replaces the default', () => {
    assert.equal(role(block({ webWallet: null }).root, 'webwallet'), null);
    assert.equal(role(block({ webWallet: WALLET }).root, 'webwallet').attrs.href, WALLET + '#link=' + LINK);
  });

  test('a text too long for any QR code gets no QR code and no error', () => {
    const long = LINK + '&pad=' + 'a'.repeat(3000);
    const { root } = block({ link: long });
    assert.equal(role(root, 'qr'), null);
    assert.equal(role(root, 'text').value, long);                        // the text is still there to copy
  });

  test('the words can be replaced, and are set as text', () => {
    const { root } = block({ labels: { title: 'Auf einem anderen Gerät öffnen', copy: '<b>Kopieren</b>' } });
    assert.ok(text(root).includes('<b>Kopieren</b>'));
    assert.equal(role(root, 'copy').textContent, '<b>Kopieren</b>');     // a tag in a label is a few characters, not markup
  });

  test('focusing the text selects it', () => {
    const { root } = block();
    const box = role(root, 'text');
    for (const fn of box.listeners.focus) fn();
    assert.equal(box.selected, true);
  });
});

describe('the buttons', () => {
  test('Copy uses the clipboard and says so', async () => {
    const written = [];
    const { root } = block({}, desktop({ clipboard: { writeText: async (t) => { written.push(t); } } }));
    await role(root, 'copy').click();
    assert.deepEqual(written, [LINK]);
    assert.equal(text(role(root, 'status')), DEFAULT_LABELS.copied);
    assert.equal(role(root, 'status').attrs.role, 'status');
  });

  test('Copy falls back to selecting the text and asking the browser, and says when that fails too', async () => {
    const refusing = { clipboard: { writeText: async () => { throw new Error('denied'); } } };
    const a = block({}, desktop(refusing));
    await role(a.root, 'copy').click();
    assert.deepEqual(a.document.executed, ['copy']);
    assert.equal(role(a.root, 'text').selected, true);
    assert.equal(text(role(a.root, 'status')), DEFAULT_LABELS.copied);
    const b = block({}, desktop(refusing));
    b.document.execResult = false;
    await role(b.root, 'copy').click();
    assert.equal(text(role(b.root, 'status')), DEFAULT_LABELS.copyFailed);
    const c = block({}, desktop());                                      // no clipboard object at all
    c.document.execResult = false;
    await role(c.root, 'copy').click();
    assert.equal(text(role(c.root, 'status')), DEFAULT_LABELS.copyFailed);
  });

  test('Share appears where the browser can share; the raw link goes as text, a hand-off address as a URL', async () => {
    const shared = [];
    const nav = phone({ share: async (data) => { shared.push(data); } });
    const a = block({}, nav);
    await role(a.root, 'share').click();
    assert.deepEqual(shared[0], { title: DEFAULT_LABELS.shareTitle, text: LINK });
    const b = block({ handoff: HANDOFF }, nav);
    await role(b.root, 'share').click();
    assert.deepEqual(shared[1], { title: DEFAULT_LABELS.shareTitle, url: HANDOFF });
  });

  test('closing the share sheet is not an error; a real failure is said', async () => {
    const abort = block({}, phone({ share: async () => { throw Object.assign(new Error('closed'), { name: 'AbortError' }); } }));
    await role(abort.root, 'share').click();
    assert.equal(text(role(abort.root, 'status')), '');
    const fail = block({}, phone({ share: async () => { throw Object.assign(new Error('no'), { name: 'NotAllowedError' }); } }));
    await role(fail.root, 'share').click();
    assert.equal(text(role(fail.root, 'status')), DEFAULT_LABELS.shareFailed);
  });
});
