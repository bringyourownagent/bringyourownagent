// The site's side of the BYOA "direct" transport (spec/CONTRACT-DIRECT.md): one-time codes for the
// bringyourownagent://connect link, the WebSocket from the visitor's agent wallet to the site's own server,
// pairing tokens, and calls from the server to the wallet.
//
// Everything is in memory. A restart forgets the pairings: a wallet that comes back with one hears "bad-session"
// and the visitor connects again. Nothing here talks to anyone but the wallets that connect to it.
//
// The server is the site's own: each site runs its own relay, there is no shared one. It needs the `ws` package
// (an optional peer dependency of this library): `npm install ws`.

import { EventEmitter } from 'node:events';
import { randomBytes } from 'node:crypto';
import { validateManifest } from '../manifest.js';

export const CODE_LIFE_MS = 120_000;
export const MAX_FRAME_BYTES = 4 * 1024 * 1024;
export const MAX_MANIFEST_BYTES = 4096;
const PAIRING_IDLE_MS = 24 * 60 * 60 * 1000;

const secret = () => randomBytes(24).toString('base64url'); // 192 random bits, URL-safe, no padding

/** Compact JSON, UTF-8, base64url without padding. Refuses what a link cannot carry. */
export function encodeManifest(manifest) {
  if (manifest === null || typeof manifest !== 'object' || Array.isArray(manifest)) {
    throw new TypeError('the BYOA manifest must be a JSON object');
  }
  const bytes = Buffer.from(JSON.stringify(manifest), 'utf8');
  if (bytes.length > MAX_MANIFEST_BYTES) {
    throw new RangeError(`the BYOA manifest is ${bytes.length} bytes, a link carries at most ${MAX_MANIFEST_BYTES}`);
  }
  return bytes.toString('base64url');
}

/** bringyourownagent://connect?v=0.1&endpoint=...&manifest=...&session=... */
export function connectLink(endpoint, manifestB64, code) {
  return `bringyourownagent://connect?v=0.1&endpoint=${encodeURIComponent(endpoint)}`
    + `&manifest=${encodeURIComponent(manifestB64)}&session=${encodeURIComponent(code)}`;
}

/**
 * One wallet connection. Calls go out with `call()`, which never rejects: the answer is
 * `{ ok: true, output, receipt }` or `{ ok: false }` (the wallet said no, did not answer in time, or went away).
 */
export class WalletConnection extends EventEmitter {
  constructor(socket, options) {
    super();
    this.socket = socket;
    this.options = options;
    this.pairing = null;
    this.grant = null;
    this.wallet = null;
    this.welcomed = false;
    this.closed = false;
    this._nextId = 0;
    this._pending = new Map();
    this._queue = [];
    this._inFlight = 0;
    this._warming = true; // the first call after a (re)connect may meet a cold model
  }

  /** True while the wallet can take calls. */
  get live() { return this.welcomed && !this.closed; }

  /**
   * One use of a skill. `options.timeoutMs` overrides the default wait. Never rejects.
   * At most 4 calls are in flight to one wallet; the rest wait their turn.
   */
  call(skill, input, params = {}, options = {}) {
    return new Promise((resolve) => {
      if (!this.live) { resolve({ ok: false }); return; }
      this._queue.push({ skill, input, params, options, resolve });
      this._pump();
    });
  }

  _pump() {
    while (this._inFlight < this.options.maxInFlight && this._queue.length > 0 && this.live) {
      const job = this._queue.shift();
      const id = ++this._nextId;
      const warm = this._warming;
      const timeoutMs = job.options.timeoutMs ?? (warm ? this.options.firstCallTimeoutMs : this.options.callTimeoutMs);
      const finish = (answer) => {
        const waiting = this._pending.get(id);
        if (!waiting) return;
        clearTimeout(waiting.timer);
        this._pending.delete(id);
        this._inFlight--;
        if (warm && answer.ok !== undefined) this._warming = false;
        job.resolve(answer);
        this._pump();
      };
      this._inFlight++;
      this._pending.set(id, { finish, timer: setTimeout(() => finish({ ok: false, timedOut: true }), timeoutMs) });
      this._send({ type: 'call', id, skill: job.skill, input: job.input, params: job.params });
    }
  }

  _result(frame) {
    const waiting = Number.isInteger(frame.id) ? this._pending.get(frame.id) : undefined;
    if (!waiting) return; // late, repeated or made up
    if (frame.ok === true && frame.output !== null && typeof frame.output === 'object') {
      waiting.finish({ ok: true, output: frame.output, receipt: frame.receipt ?? null });
    } else {
      waiting.finish({ ok: false });
    }
  }

  _send(message) {
    if (this.closed) return;
    const timer = setTimeout(() => this.terminate(), this.options.sendTimeoutMs); // a wallet that stopped reading is dropped
    this.socket.send(JSON.stringify(message), (error) => { clearTimeout(timer); if (error) this.terminate(); });
  }

  /** The server lets this wallet go: "bye" and a close. */
  bye() {
    if (this.closed) return;
    try { this.socket.send(JSON.stringify({ type: 'bye' })); } catch { /* already gone */ }
    this.close(1000, 'bye');
  }

  close(code = 1000, reason = '') {
    if (this.closed) return;
    try { this.socket.close(code, reason); } catch { /* already gone */ }
  }

  terminate() {
    try { this.socket.terminate(); } catch { /* already gone */ }
  }

  _ended() {
    if (this.closed) return;
    this.closed = true;
    clearTimeout(this._helloTimer);
    clearTimeout(this._idleTimer);
    for (const waiting of [...this._pending.values()]) waiting.finish({ ok: false, cancelled: true });
    for (const job of this._queue.splice(0)) job.resolve({ ok: false, cancelled: true });
    this.emit('close');
  }
}

/**
 * The relay: codes, pairings and wallet connections.
 *
 * @param {object} options
 * @param {object} options.manifest       The manifest this site asks with (validated now; it travels in every link).
 * @param {string} options.endpoint       The wallet's WebSocket URL, `wss://your-host/byoa/ws`.
 * @param {number} [options.codesPerMinute=10]  Codes one address may ask for in a minute.
 * @param {number} [options.callTimeoutMs=12000]
 * @param {number} [options.firstCallTimeoutMs=30000]  The first call after a connect may meet a cold model.
 * @param {number} [options.helloMs=10000]   From the connection to its hello.
 * @param {number} [options.idleMs=75000]    Silence after welcome (the wallet pings every 30 s).
 * @param {number} [options.sendTimeoutMs=5000]
 * @param {number} [options.maxPairings=1000]
 * @param {number} [options.maxCodes=2000]
 * @param {() => number} [options.now]       Clock in ms, for tests.
 */
export function createRelay(options) {
  return new Relay(options);
}

export class Relay extends EventEmitter {
  constructor(options) {
    super();
    const checked = validateManifest(options.manifest);
    if (!checked.ok) {
      throw new Error('invalid BYOA manifest: ' + checked.errors.map((e) => `${e.code} ${e.path}`).join(', '));
    }
    if (typeof options.endpoint !== 'string' || !options.endpoint.startsWith('ws')) {
      throw new TypeError('options.endpoint must be a ws:// or wss:// URL');
    }
    this.manifest = options.manifest;
    this.manifestB64 = encodeManifest(options.manifest);
    this.endpoint = options.endpoint;
    this.skills = Object.keys(options.manifest.skills ?? {});
    this.limits = {
      codesPerMinute: options.codesPerMinute ?? 10,
      maxPairings: options.maxPairings ?? 1000,
      maxCodes: options.maxCodes ?? 2000,
    };
    this.connection = {
      callTimeoutMs: options.callTimeoutMs ?? 12_000,
      firstCallTimeoutMs: options.firstCallTimeoutMs ?? 30_000,
      helloMs: options.helloMs ?? 10_000,
      idleMs: options.idleMs ?? 75_000,
      sendTimeoutMs: options.sendTimeoutMs ?? 5_000,
      maxInFlight: 4,
    };
    this._now = options.now ?? Date.now;
    this._codes = new Map();
    this._pairings = new Map();
    this._asked = new Map();
    this._wss = null;
  }

  /**
   * A new one-time code for one page, and the link that carries it. `pageKey` is any string that names the page
   * or game session on your side: only the same key can ask about the code later.
   * Returns null when this address asked for too many codes in the last minute (or the table is full).
   */
  newCode(pageKey, { ip = '' } = {}) {
    const now = this._now();
    this._sweep(now);
    const times = this._asked.get(ip) ?? [];
    while (times.length > 0 && now - times[0] >= 60_000) times.shift();
    if (times.length >= this.limits.codesPerMinute || this._codes.size >= this.limits.maxCodes) return null;
    times.push(now);
    this._asked.set(ip, times);
    const code = { value: secret(), pageKey, expires: now + CODE_LIFE_MS, used: false, cancelled: false, pairing: null };
    this._codes.set(code.value, code);
    return { code: code.value, link: connectLink(this.endpoint, this.manifestB64, code.value), expiresAt: new Date(code.expires).toISOString() };
  }

  /** Has the wallet come with this code? `waiting`, `connected` (with the pairing token for the page to keep) or `expired`. */
  codeState(pageKey, code) {
    const c = typeof code === 'string' ? this._codes.get(code) : undefined;
    if (!c || c.pageKey !== pageKey || c.cancelled) return { state: 'expired' };
    if (c.used) return c.pairing ? { state: 'connected', pairing: c.pairing } : { state: 'expired' };
    return this._now() < c.expires ? { state: 'waiting' } : { state: 'expired' };
  }

  /** The page stopped waiting: a wallet arriving with this code later hears "bad-session". */
  cancel(pageKey, code) {
    const c = typeof code === 'string' ? this._codes.get(code) : undefined;
    if (!c || c.pageKey !== pageKey || c.used) return false;
    c.cancelled = true;
    return true;
  }

  /** The live wallet connection of a pairing token (what a page keeps after connecting), or undefined. */
  walletFor(pairing) {
    const p = typeof pairing === 'string' ? this._pairings.get(pairing) : undefined;
    return p?.live?.live ? p.live : undefined;
  }

  /** What the server knows about a pairing token: `live`, `offline` (known, no wallet now) or `unknown`. */
  pairingState(pairing) {
    const p = typeof pairing === 'string' ? this._pairings.get(pairing) : undefined;
    return !p ? 'unknown' : p.live?.live ? 'live' : 'offline';
  }

  /** Forgets a pairing and says bye to its wallet (the visitor disconnected on the site). */
  forget(pairing) {
    const p = this._pairings.get(pairing);
    if (!p) return false;
    this._pairings.delete(pairing);
    p.live?.bye();
    return true;
  }

  get pairingCount() { return this._pairings.size; }

  /**
   * Starts serving wallet connections on a Node `http.Server` (or `https.Server`) at `path`.
   * Behind a reverse proxy, forward the WebSocket upgrade for that path (Apache: `ProxyPass /byoa/ws ws://127.0.0.1:PORT/byoa/ws`).
   */
  async attach(server, path = '/byoa/ws') {
    const { WebSocketServer } = await importWs();
    this._wss = new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME_BYTES });
    server.on('upgrade', (request, socket, head) => {
      const pathname = (request.url ?? '').split('?')[0];
      if (pathname !== path) return; // not ours; let other upgrade handlers see it
      this._wss.handleUpgrade(request, socket, head, (ws) => this._serve(ws));
    });
    return this;
  }

  /** Serves one already-accepted `ws` WebSocket (for frameworks that do their own upgrade). */
  serve(ws) { return this._serve(ws); }

  /** Closes every wallet connection (shutdown). */
  closeAll() {
    for (const p of this._pairings.values()) p.live?.close(1001, 'server stopping');
  }

  _serve(ws) {
    const conn = new WalletConnection(ws, this.connection);
    conn._helloTimer = setTimeout(() => conn.close(1008, 'no hello'), this.connection.helloMs);
    const armIdle = () => {
      clearTimeout(conn._idleTimer);
      conn._idleTimer = setTimeout(() => conn.terminate(), this.connection.idleMs);
    };
    ws.on('message', (data, isBinary) => {
      if (isBinary) return; // unknown frames are ignored
      let frame;
      try { frame = JSON.parse(data.toString('utf8')); } catch { conn.close(1007, 'malformed'); return; }
      if (frame === null || typeof frame !== 'object' || Array.isArray(frame)) { conn.close(1007, 'malformed'); return; }
      if (!conn.welcomed) {
        if (frame.type !== 'hello') return; // the server says nothing before a valid hello
        this._hello(conn, frame, armIdle);
        return;
      }
      armIdle();
      if (frame.type === 'result') conn._result(frame);
      else if (frame.type === 'bye') conn.close(1000, 'bye');
      // ping and anything else: nothing to do
    });
    ws.on('close', () => { this._gone(conn); conn._ended(); });
    ws.on('error', () => conn.terminate());
  }

  _hello(conn, frame, armIdle) {
    const welcome = this._accept(conn, frame);
    if (!welcome) {
      conn._send({ type: 'error', code: 'bad-session' });
      conn.close(1008, 'bad-session');
      return;
    }
    clearTimeout(conn._helloTimer);
    conn.welcomed = true;
    conn.pairing = welcome.pairing;
    conn.wallet = typeof frame.wallet === 'object' && frame.wallet !== null ? frame.wallet : null;
    const skills = Array.isArray(frame.grant?.skills) ? frame.grant.skills.filter((s) => typeof s === 'string') : [];
    conn.grant = { skills, expiresAt: typeof frame.grant?.expiresAt === 'string' ? frame.grant.expiresAt : null };
    conn._send({ type: 'welcome', pairing: welcome.pairing, manifest: welcome.manifest });
    armIdle();
    this.emit('connected', conn);
  }

  /** A wallet's hello: a fresh code (single use) or a known pairing token. Returns what "welcome" carries, or null. */
  _accept(conn, frame) {
    const now = this._now();
    this._sweep(now);
    const { session, pairing, v } = frame;
    if (v !== '0.1') return null;
    if (typeof session === 'string' && (pairing === null || pairing === undefined)) {
      const code = this._codes.get(session);
      if (!code || code.used || code.cancelled || now >= code.expires || !this._makeRoom()) return null;
      code.used = true;
      const p = { token: secret(), pageKey: code.pageKey, live: conn, lastUsed: now };
      this._pairings.set(p.token, p);
      code.pairing = p.token;
      return { pairing: p.token, manifest: this.manifestB64 };
    }
    if (typeof pairing === 'string' && (session === null || session === undefined)) {
      const p = this._pairings.get(pairing);
      if (!p) return null;
      if (p.live && p.live !== conn) p.live.bye(); // at most one connection per pairing: the newest wins
      p.live = conn;
      p.lastUsed = now;
      return { pairing: p.token, manifest: this.manifestB64 };
    }
    return null;
  }

  _gone(conn) {
    for (const p of this._pairings.values()) {
      if (p.live === conn) { p.live = null; p.lastUsed = this._now(); }
    }
    if (conn.welcomed) this.emit('disconnected', conn);
  }

  // a full table makes room instead of refusing: the oldest pairing without a wallet goes first
  _makeRoom() {
    if (this._pairings.size < this.limits.maxPairings) return true;
    let victim = null;
    for (const p of this._pairings.values()) {
      if (!p.live && (!victim || p.lastUsed < victim.lastUsed)) victim = p;
    }
    if (!victim) return false; // every pairing has a live wallet
    this._pairings.delete(victim.token);
    return true;
  }

  _sweep(now) {
    for (const [value, c] of this._codes) {
      if (now - c.expires > 10 * 60_000) this._codes.delete(value); // a used code is kept a while: the page may still ask
    }
    for (const [token, p] of this._pairings) {
      if (!p.live && now - p.lastUsed > PAIRING_IDLE_MS) this._pairings.delete(token);
    }
    for (const [ip, times] of this._asked) {
      while (times.length > 0 && now - times[0] >= 60_000) times.shift();
      if (times.length === 0) this._asked.delete(ip);
    }
  }
}

async function importWs() {
  try {
    return await import('ws');
  } catch (error) {
    if (error?.code === 'ERR_MODULE_NOT_FOUND') {
      throw new Error('The BYOA relay needs the "ws" package: run `npm install ws`.');
    }
    throw error;
  }
}
