// Helpers for the relay tests: a relay on a real HTTP server, and a fake wallet (a `ws` client) that plays the visitor's side.

import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { afterEach } from 'node:test';
import WebSocket from 'ws';
import { createRelay } from '../src/site/relay.js';

export const MANIFEST = JSON.parse(readFileSync(new URL('../../examples/manifest.json', import.meta.url), 'utf8'));
export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const running = [];

export const registerCleanup = () => afterEach(async () => {
  for (const stop of running.splice(0).reverse()) await stop();
});

export async function start(options = {}) {
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
export function fakeWallet(url, sockets) {
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

export function hello(session, extra = {}) {
  return { type: 'hello', v: '0.1', session, pairing: null, wallet: { name: 'Test wallet', version: '0' },
    grant: { skills: Object.keys(MANIFEST.skills), expiresAt: '2030-01-01T00:00:00Z' }, ...extra };
}

export async function connect(env, pageKey = 'page-1') {
  const { code, link } = env.relay.newCode(pageKey, { ip: '1.1.1.1' });
  const w = env.wallet();
  await w.open;
  w.send(hello(code));
  const welcome = await w.next();
  return { w, code, link, welcome, pageKey };
}

