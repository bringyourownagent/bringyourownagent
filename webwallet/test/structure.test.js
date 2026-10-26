// The page's shape: what keeps the wallet page safe is checked as text, because no browser runs here.

import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { test } from 'node:test';

const here = new URL('../', import.meta.url);
const read = (path) => readFileSync(new URL(path, here), 'utf8');
const code = (text) => text.replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/ .*$/gm, '');         // the comments may name what the code must not do
const sources = readdirSync(new URL('src/', here)).filter((f) => f.endsWith('.js')).map((f) => [`src/${f}`, code(read(`src/${f}`))]);

test('the vendored gate and manifest code is byte for byte the SDK\'s', () => {
  for (const name of readdirSync(new URL('vendor/byoa/', here)).filter((f) => f.endsWith('.js'))) {
    assert.equal(read(`vendor/byoa/${name}`), readFileSync(new URL(`../sdk/js/src/${name}`, here), 'utf8'), name);
  }
});

test('the CSP in the page and in _headers: nothing allowed by default, scripts only from this origin, no framing', () => {
  const html = read('index.html');
  const meta = /http-equiv="Content-Security-Policy" content="([^"]+)"/.exec(html)?.[1];
  const header = /Content-Security-Policy: (.+)/.exec(read('_headers'))?.[1];
  assert.ok(meta && header);
  for (const policy of [meta, header]) {
    const d = Object.fromEntries(policy.split(';').map((x) => x.trim().split(/\s+/)).map(([k, ...v]) => [k, v.join(' ')]));
    assert.equal(d['default-src'], "'none'");
    assert.equal(d['script-src'], "'self'");
    assert.equal(d['style-src'], "'self'");
    assert.equal(d['connect-src'], 'https://openrouter.ai wss:');
    assert.equal(d['base-uri'], "'none'");
    assert.equal(d['form-action'], "'none'");
    assert.equal(policy.includes('unsafe'), false);
    assert.equal(policy.includes('data:'), false);
    assert.equal(policy.includes('http:'), false);
  }
  assert.match(header, /frame-ancestors 'none'/);
  assert.equal(meta.replace(/; frame-ancestors 'none'/, ''), header.replace(/; frame-ancestors 'none'/, ''));
});

test('the page has no inline script, no inline style, no handler attribute and no third-party file', () => {
  const html = read('index.html');
  assert.deepEqual([...html.matchAll(/<script\b[^>]*>/g)].map((m) => m[0]), ['<script type="module" src="src/app.js">']);
  assert.equal(/<style\b/i.test(html), false);
  assert.equal(/\son[a-z]+\s*=/i.test(html), false);
  assert.equal(/\sstyle\s*=/i.test(html), false);
  assert.equal(/(src|href)="(https?:)?\/\//.test(html), false);
});

test('the scripts build the page as text: no innerHTML, no eval, no document.write, no dynamic import, no other host', () => {
  for (const [name, text] of sources) {
    assert.equal(/innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function|(await|return|=)\s*import\(/.test(text), false, name);
    const hosts = [...text.matchAll(/https?:\/\/[a-z0-9.-]+/gi)].map((m) => m[0]).filter((h) => !/^https?:\/\/(example\.org|bringyourownagent\.org)/.test(h));
    assert.ok(hosts.every((h) => h === 'https://openrouter.ai'), `${name}: ${hosts}`);
  }
});

test('only openrouter.js reaches the network, and only wallet.js opens a socket', () => {
  for (const [name, text] of sources) {
    if (name !== 'src/openrouter.js') assert.equal(/\bfetch\(/.test(text), false, name);
    if (name !== 'src/wallet.js') assert.equal(/new WebSocket|EventSource|sendBeacon|XMLHttpRequest/.test(text), false, name);
  }
});

test('the key is never written to the page, the log or the address bar', () => {
  const app = sources.find(([n]) => n === 'src/app.js')[1];
  assert.equal(/console\./.test(app), false);
  assert.equal(/getKey\(\)/.test(app.replace(/store\.getKey\(\)/g, '')), false);
  for (const [name, text] of sources) assert.equal(/console\.(log|info|debug)/.test(text), false, name);
});

test('the Allow button answers only to a real click, never to a script\'s', () => {
  const app = read('src/app.js');
  assert.match(app, /isTrusted/);
  assert.match(app, /allow = h\('button'[^)]*trusted\(/);
});

test('no Cyrillic anywhere in the files that go to git', () => {
  for (const path of ['index.html', 'style.css', '_headers', 'package.json', ...sources.map(([n]) => n)]) {
    assert.equal(new RegExp(`[${String.fromCharCode(0x400)}-${String.fromCharCode(0x4ff)}]`).test(read(path)), false, path);
  }
});
