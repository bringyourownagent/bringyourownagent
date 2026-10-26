// Test helpers: links as a site makes them, and a pretend site (an in-memory socket) that the wallet connects to.

import { createHash } from 'node:crypto';

export const SESSION = 'AbCdEfGhIjKlMnOpQrStUvWx';

export const MANIFEST = {
  byoa: '0.1-draft',
  purpose: { summary: 'Family quiz: the wallet answers questions', limits: { maxCallsPerMinute: 10 } },
  skills: { 'text.generate': { required: true, models: { count: { min: 1, max: 3 }, names: true } } },
  privacy: 'public',
  connect: { check: true },
};

export const b64url = (text) => Buffer.from(text, 'utf8').toString('base64url');

/** A link as a site makes it; `overrides` replaces a parameter (a string) or drops it (null). */
export function makeLink({ manifest = MANIFEST, session = SESSION, endpoint = 'wss://quiz.example.org/byoa/ws', overrides = {} } = {}) {
  const params = { v: '0.1', session, endpoint, manifest: b64url(JSON.stringify(manifest)), ...overrides };
  const query = Object.entries(params).filter(([, v]) => v !== null).map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join('&');
  return `bringyourownagent://connect?${query}`;
}

/** The check code written out here a second time, from the spec's words, so the wallet's code is compared with something independent. */
export function expectedCheck(session) {
  const alphabet = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
  const d = createHash('sha256').update(`BYOA-CHECK-1:${session}`).digest();
  return [0, 1, 2, 3].map((i) => alphabet[d[i] % 31]).join('');
}

/** A pretend site: what the wallet sees as a WebSocket. `sent` is what the wallet wrote; `say` is the site speaking. */
export class FakeSite {
  constructor() {
    this.sent = [];
    this.urls = [];
    this.closed = false;
    this.sockets = [];
    this.socket = null;
    this.createSocket = (url) => {
      this.urls.push(url);
      const site = this;
      const socket = {
        onopen: null, onmessage: null, onclose: null, onerror: null,
        send(text) { site.sent.push(JSON.parse(text)); },
        close() { site.closed = true; },
      };
      this.socket = socket;
      this.sockets.push(socket);
      return socket;
    };
  }

  open() { this.socket.onopen?.(); }
  say(frame) { this.socket.onmessage?.({ data: typeof frame === 'string' ? frame : JSON.stringify(frame) }); }
  drop() { this.socket.onclose?.(); }
  frames(type) { return this.sent.filter((f) => f.type === type); }
  async until(predicate, ms = 2000) {
    const end = Date.now() + ms;
    while (!predicate()) {
      if (Date.now() > end) throw new Error('the pretend site waited too long');
      await new Promise((r) => setTimeout(r, 2));
    }
  }
}
