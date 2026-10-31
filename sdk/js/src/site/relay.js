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
import { createHash, randomBytes } from 'node:crypto';
import { isIPv6 } from 'node:net';
import { validateManifest } from '../manifest.js';

export const CODE_LIFE_MS = 120_000;
export const MAX_FRAME_BYTES = 4 * 1024 * 1024;
export const MAX_MANIFEST_BYTES = 4096;
const PAIRING_IDLE_MS = 24 * 60 * 60 * 1000;
const SWEEP_EVERY_MS = 60_000;

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

/** The alphabet of the connect check code: no I, L, O, 0 or 1, which are told apart badly by eye. */
export const CHECK_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

/**
 * The connect check code of one session (spec/CONTRACT-DIRECT.md section 3a): four characters that the page shows
 * while it waits and the wallet shows in its consent window, so the visitor sees that the connection he started is
 * the one he is asked to allow. `d = SHA-256(UTF-8("BYOA-CHECK-1:" + session))`, character i is
 * `CHECK_ALPHABET[d[i] mod 31]`. It is not a secret: the session it comes from is in the link.
 */
export function connectCheck(session) {
  if (typeof session !== 'string') throw new TypeError('connectCheck needs the session code as a string');
  const d = createHash('sha256').update('BYOA-CHECK-1:' + session, 'utf8').digest();
  let code = '';
  for (let i = 0; i < 4; i++) code += CHECK_ALPHABET[d[i] % CHECK_ALPHABET.length];
  return code;
}

/**
 * The key a visitor's address is limited under: an IPv4-mapped IPv6 address becomes its IPv4 form, and any other
 * IPv6 address becomes its /64 (one home network owns the whole /64 and can rotate inside it at will).
 */
export function addressKey(ip) {
  let a = String(ip).trim().toLowerCase();
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(a);
  if (mapped) return mapped[1];
  if (!isIPv6(a)) return a;
  const [head, tail = ''] = a.split('::');
  const groups = head.split(':').filter(Boolean);
  const rest = tail.split(':').filter(Boolean);
  const all = [...groups, ...Array(Math.max(0, 8 - groups.length - rest.length)).fill('0'), ...rest];
  return all.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, '')).join(':') + '::/64';
}

/** What a wallet may hand back as a receipt: two short strings, nothing else. Anything else is dropped. */
function cleanReceipt(receipt) {
  if (receipt === null || typeof receipt !== 'object') return null;
  const { cost, provider } = receipt;
  if (typeof cost !== 'string' || typeof provider !== 'string' || cost.length > 40 || provider.length > 40) return null;
  const model = typeof receipt.model === 'string' && receipt.model.length <= 16 ? receipt.model : undefined;
  return model === undefined ? { cost, provider } : { cost, provider, model };
}

/** Why a wallet says a call failed (spec/CONTRACT-DIRECT.md section 4): a closed list; any other word is "failed". */
const REASONS = new Set(['timeout', 'cancelled', 'limit', 'failed']);

/** The abilities a visitor's agent lacked for an answer: ability names only, once each, at most 8; undefined when none. */
function cleanLacks(lacks) {
  if (!Array.isArray(lacks)) return undefined;
  const names = [...new Set(lacks.filter((a) => typeof a === 'string' && /^[a-z][a-z0-9.-]{0,31}$/.test(a)))].slice(0, 8);
  return names.length > 0 ? names : undefined;
}

const LIMIT_NAMES = ['maxTextChars', 'maxAnswerTokens', 'maxCallSeconds', 'maxParallelCalls', 'maxCallsPerMinute'];

/** What a wallet says it holds the site to (hello.grant.limits, or a "grant" frame): known names, positive integers only; null when it says nothing. */
function cleanLimits(limits) {
  if (limits === null || typeof limits !== 'object' || Array.isArray(limits)) return null;
  const out = {};
  for (const name of LIMIT_NAMES) {
    const v = limits[name];
    if (Number.isInteger(v) && v > 0 && v <= 1_000_000) out[name] = v;
  }
  return out;
}

/** The models the visitor gave this site ({ ref, name, id, runs, kind }; kind is 'agent' or 'model', 'model' when a wallet does not say), at most 64; a wallet that does not know the models block sends none. */
function cleanModels(models) {
  return cleanModelsBySkill(models)['text.generate'] ?? [];
}

/** The closed list of grant capabilities (CONTRACT.md section 3.3, wallet 0.36). */
const CAPABILITY_NAMES = ['needs', 'can', 'kind', 'cancel', 'image.generate', 'images-in', 'parts', 'frame-limit'];

/** The properties a site may ask about in `needs`; a verdict for any other name is dropped. */
const CAN_NAMES = ['input', 'output', 'context', 'runs'];

/** `can` or `abilities`: booleans only, at most four keys, safe names, and only what a site can ask about; undefined when nothing survives. */
function cleanVerdicts(value, allowed) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const out = {};
  let n = 0;
  for (const key of Object.keys(value)) {
    if (n >= 4) break;
    if (allowed && !allowed.includes(key)) continue;
    if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$/.test(key)) continue;
    if (typeof value[key] !== 'boolean') continue;
    out[key] = value[key];
    n++;
  }
  return n ? out : undefined;
}

/** grant.capabilities: the closed list, unknown and repeated names dropped; an empty array is kept (a wallet that understands none). */
function cleanCapabilities(value) {
  if (!Array.isArray(value)) return undefined;
  const out = [];
  for (const name of value) {
    if (CAPABILITY_NAMES.includes(name) && !out.includes(name)) out.push(name);
    if (out.length === CAPABILITY_NAMES.length) break;
  }
  return out;
}

/** Every skill's models, as the grant carries them (CONTRACT.md section 3.3): { "<skill>": [{ ref, name, id, runs, kind, can?, abilities? }] }, at most 8 skills and 64 entries a skill. */
function cleanModelsBySkill(models) {
  if (models === null || typeof models !== 'object' || Array.isArray(models)) return {};
  const str = (v, max) => (typeof v === 'string' && v.length <= max ? v : null);
  const out = {};
  for (const skill of Object.keys(models).slice(0, 8)) {
    if (!/^[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$/.test(skill) || !Array.isArray(models[skill])) continue;
    out[skill] = models[skill].slice(0, 64).filter((m) => m !== null && typeof m === 'object' && str(m.ref, 16)).map((m) => {
      const kind = m.kind === 'agent' ? 'agent' : 'model';
      const entry = { ref: m.ref, name: str(m.name, 80) ?? m.ref, id: str(m.id, 120), runs: str(m.runs, 16), kind };
      const can = cleanVerdicts(m.can, CAN_NAMES);
      if (can) entry.can = can;
      const abilities = kind === 'agent' ? cleanVerdicts(m.abilities, null) : undefined;
      if (abilities) entry.abilities = abilities;
      return entry;
    });
  }
  return out;
}

/**
 * One wallet connection. Calls go out with `call()`, which never rejects: the answer is
 * `{ ok: true, output, receipt }` or `{ ok: false }` (the wallet said no, did not answer in time, or went away).
 * The wallet's `output` is the visitor's software speaking: it is user input, never trusted.
 *
 * Events: `close`; `failing` (this many calls in a row failed: the site should give up on this wallet).
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
    /** True when a newer connection with the same pairing took this one's place. */
    this.replaced = false;
    /** How many calls in a row have failed (a timeout during the warm-up does not count). */
    this.failures = 0;
    this.openedAt = Date.now();
    this._nextId = 0;
    this._pending = new Map();
    this._queue = [];
    this._inFlight = 0;
    this._warming = true; // the first call after a (re)connect may meet a cold model
    this._lastCall = 0;
  }

  /** True while the wallet can take calls. */
  get live() { return this.welcomed && !this.closed; }

  /** How many calls wait for a free place. */
  get queued() { return this._queue.length; }

  /**
   * One use of a skill. Never rejects. `options.timeoutMs` overrides the wait, which counts from now, including
   * the time spent waiting for a place: as many calls are in flight to one wallet as its `grant.limits.maxParallelCalls`
   * says (4 when it says nothing; the option `maxInFlight` caps it), up to `maxQueued` more wait their turn, and any call
   * beyond that fails at once. `options.onChunk(text)` gets the pieces of a streamed answer (`params.stream: true`) as the
   * wallet sends them: a preview, the answer replaces them.
   */
  call(skill, input, params = {}, options = {}) {
    return new Promise((resolve) => {
      if (!this.live || this._queue.length >= this.options.maxQueued) { resolve({ ok: false }); return; }
      const job = { skill, input, params, resolve, done: false, warm: this._warming, onChunk: options.onChunk };
      job.timer = setTimeout(() => this._expire(job), options.timeoutMs ?? (job.warm ? this.options.firstCallTimeoutMs : this.options.callTimeoutMs));
      this._queue.push(job);
      this._pump();
    });
  }

  _expire(job) {
    if (job.done) return;
    const at = this._queue.indexOf(job);
    if (at >= 0) this._queue.splice(at, 1);
    // a call the wallet is working on is stopped there too (an old wallet ignores the frame), so its place is free again
    if (job.id !== undefined && this.capable('cancel')) this._send({ type: 'cancel', id: job.id });
    this._finish(job, { ok: false, timedOut: true, reason: 'timeout' });
  }

  /** Whether the wallet says it supports this feature (grant.capabilities; an older wallet says nothing, so nothing is assumed). */
  capable(feature) { return Array.isArray(this.grant?.capabilities) && this.grant.capabilities.includes(feature); }

  _finish(job, answer) {
    if (job.done) return;
    job.done = true;
    clearTimeout(job.timer);
    if (job.id !== undefined) { this._pending.delete(job.id); this._inFlight--; }
    if (job.warm && !answer.cancelled) this._warming = false;
    if (answer.ok) this.failures = 0;
    else if (!(job.warm && answer.timedOut) && !answer.cancelled && !answer.tooBig) { // a cold start is not the wallet failing
      if (++this.failures === this.options.failureLimit) this.emit('failing', this.failures);
    }
    job.resolve(answer);
    this._pump();
  }

  /** How many calls may be in flight to this wallet now: what it allows, or what the site's option allows, whichever is less. */
  get maxInFlight() {
    const theirs = this.grant?.limits?.maxParallelCalls ?? 4;
    return Math.min(theirs, this.options.maxInFlight ?? theirs);
  }

  _pump() {
    while (this._inFlight < this.maxInFlight && this._queue.length > 0 && this.live) {
      const job = this._queue.shift();
      const id = ++this._nextId;
      const frame = JSON.stringify({ type: 'call', id, skill: job.skill, input: job.input, params: job.params });
      if (Buffer.byteLength(frame) > MAX_FRAME_BYTES) { this._finish(job, { ok: false, tooBig: true }); continue; } // the wallet would close the connection
      job.id = id;
      this._inFlight++;
      this._pending.set(id, job);
      this._lastCall = Date.now();
      this._sendRaw(frame);
    }
  }

  _result(frame) {
    const job = Number.isInteger(frame.id) ? this._pending.get(frame.id) : undefined;
    if (!job) return; // late, repeated or made up
    const output = frame.output;
    const sane = frame.ok === true && output !== null && typeof output === 'object' && !Array.isArray(output)
      && JSON.stringify(output).length <= this.options.maxOutputBytes;
    if (sane) {
      const answer = { ok: true, output, receipt: cleanReceipt(frame.receipt) };
      const lacks = cleanLacks(frame.lacks);
      if (lacks) answer.lacks = lacks;
      this._finish(job, answer);
      return;
    }

    const failed = { ok: false };
    if (frame.reason !== undefined) failed.reason = REASONS.has(frame.reason) ? frame.reason : 'failed';
    this._finish(job, failed);
  }

  _chunk(frame) {
    const job = Number.isInteger(frame.id) ? this._pending.get(frame.id) : undefined;
    if (!job || job.done || typeof frame.text !== 'string' || typeof job.onChunk !== 'function') return;
    try { job.onChunk(frame.text); } catch { /* the site's callback must not break the connection */ }
  }

  _send(message) { this._sendRaw(JSON.stringify(message)); }

  _sendRaw(text) {
    if (this.closed) return;
    const timer = setTimeout(() => this.terminate(), this.options.sendTimeoutMs); // a wallet that stopped reading is dropped
    this.socket.send(text, (error) => { clearTimeout(timer); if (error) this.terminate(); });
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
    clearTimeout(this._lifeTimer);
    for (const job of [...this._pending.values()]) this._finish(job, { ok: false, cancelled: true });
    for (const job of this._queue.splice(0)) this._finish(job, { ok: false, cancelled: true });
    this.emit('close');
  }
}

/**
 * The relay: codes, pairings and wallet connections.
 *
 * @param {object} options
 * @param {object} options.manifest       The manifest this site asks with (validated now; it travels in every link).
 * @param {string} options.endpoint       The wallet's WebSocket URL, `wss://your-host/byoa/ws`.
 * @param {boolean} [options.connectCheck=false]  Your page shows the connect check code while it waits: the links'
 *   manifest gets `"connect": { "check": true }` and `newCode` answers with `check` (also on when the manifest already says so).
 * @param {number} [options.codesPerMinute=10]     Codes one address may ask for in a minute.
 * @param {number} [options.codesPerPageMinute=6]  Codes one page (`pageKey`) may ask for in a minute.
 * @param {number} [options.callTimeoutMs=12000]
 * @param {number} [options.firstCallTimeoutMs=30000]  The first call after a connect may meet a cold model.
 * @param {number} [options.helloMs=10000]   From the connection to its hello.
 * @param {number} [options.idleMs=75000]    Silence after welcome (the wallet pings every 30 s).
 * @param {number} [options.sendTimeoutMs=5000]
 * @param {number} [options.maxLifetimeMs=43200000]  A connection older than this is let go with "bye" (the wallet
 *   comes back on its own with its pairing).
 * @param {number} [options.maxSockets=500]  Open WebSockets, welcomed or not; more are refused before the handshake.
 * @param {number} [options.maxPairings=1000]
 * @param {number} [options.maxCodes=2000]
 * @param {number} [options.maxQueued=32]    Calls waiting for a free place, per wallet.
 * @param {number} [options.maxInFlight]  Fewest of this and the wallet's own `maxParallelCalls` calls in flight (default: the wallet's).
 * @param {number} [options.maxOutputBytes=65536]  The biggest `output` taken from a wallet (as JSON); a text answer may be 128 000 characters, raise it for text.generate.
 * @param {number} [options.failureLimit=3]  Calls in a row that fail before the connection emits `failing`.
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
    let endpoint;
    try { endpoint = new URL(options.endpoint); } catch { endpoint = null; }
    // the URL parser forgives "wss:/host"; a wallet does not
    if (!endpoint || !/^wss?:\/\/[^/@?#\s]+/i.test(options.endpoint) || !['ws:', 'wss:'].includes(endpoint.protocol)
      || endpoint.username || endpoint.password || endpoint.hash) {
      throw new TypeError('options.endpoint must be a ws:// or wss:// URL without user name or fragment');
    }
    this.checks = options.connectCheck === true || options.manifest.connect?.check === true;
    this.manifest = this.checks && options.manifest.connect?.check !== true
      ? { ...options.manifest, connect: { ...options.manifest.connect, check: true } }
      : options.manifest;
    this.manifestB64 = encodeManifest(this.manifest);
    this.endpoint = options.endpoint;
    this.limits = {
      codesPerMinute: options.codesPerMinute ?? 10,
      codesPerPageMinute: options.codesPerPageMinute ?? 6,
      maxPairings: options.maxPairings ?? 1000,
      maxCodes: options.maxCodes ?? 2000,
      maxSockets: options.maxSockets ?? 500,
      maxLifetimeMs: options.maxLifetimeMs ?? 12 * 60 * 60 * 1000,
    };
    this.connection = {
      callTimeoutMs: options.callTimeoutMs ?? 12_000,
      firstCallTimeoutMs: options.firstCallTimeoutMs ?? 30_000,
      helloMs: options.helloMs ?? 10_000,
      idleMs: options.idleMs ?? 75_000,
      sendTimeoutMs: options.sendTimeoutMs ?? 5_000,
      maxQueued: options.maxQueued ?? 32,
      maxOutputBytes: options.maxOutputBytes ?? 65_536,
      failureLimit: options.failureLimit ?? 3,
      maxInFlight: options.maxInFlight, // undefined: what the wallet's own limit says (4 when it says none)
    };
    this._now = options.now ?? Date.now;
    this._codes = new Map();
    this._pairings = new Map();
    this._asked = new Map();
    this._askedPage = new Map();
    this._sockets = new Set();
    this._servers = [];
    this._wss = null;
    this._sweeper = setInterval(() => this._sweep(this._now()), SWEEP_EVERY_MS);
    this._sweeper.unref?.();
  }

  /**
   * A new one-time code for one page, and the link that carries it. `pageKey` is any string that names the page
   * or game session on your side and does not change while the page waits: only the same key can ask about the code.
   *
   * `ip` is the visitor's address and is **required**: the number of codes is limited per address and per page.
   * Give the address of the connection, or a header that your own reverse proxy sets (never one the visitor can
   * send); behind a proxy the socket's address is the proxy's, which would put every visitor in one bucket.
   *
   * Returns `{ code, link, expiresAt }`, with `check` (the code your page shows) when the relay checks connections,
   * or null when this address or page asked for too many codes in the last minute, or the table is full.
   */
  newCode(pageKey, { ip } = {}) {
    if (typeof pageKey !== 'string' || pageKey === '') throw new TypeError('newCode needs a pageKey');
    if (typeof ip !== 'string' || ip === '') throw new TypeError('newCode needs the visitor\'s address in options.ip (see the docs)');
    const now = this._now();
    this._sweep(now);
    const byAddress = this._recent(this._asked, addressKey(ip), now);
    const byPage = this._recent(this._askedPage, pageKey, now);
    if (byAddress.length >= this.limits.codesPerMinute || byPage.length >= this.limits.codesPerPageMinute
      || this._codes.size >= this.limits.maxCodes) return null;
    byAddress.push(now);
    byPage.push(now);
    const code = { value: secret(), pageKey, expires: now + CODE_LIFE_MS, used: false, cancelled: false, pairing: null };
    this._codes.set(code.value, code);
    const issued = { code: code.value, link: connectLink(this.endpoint, this.manifestB64, code.value), expiresAt: new Date(code.expires).toISOString() };
    if (this.checks) issued.check = connectCheck(code.value);
    return issued;
  }

  _recent(map, key, now) {
    const times = map.get(key) ?? [];
    while (times.length > 0 && now - times[0] >= 60_000) times.shift();
    map.set(key, times);
    return times;
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
    this.emit('forgotten', pairing);
    return true;
  }

  get pairingCount() { return this._pairings.size; }

  /** Open WebSockets, welcomed or not. */
  get socketCount() { return this._sockets.size; }

  /**
   * Starts serving wallet connections on a Node `http.Server` (or `https.Server`) at `path`. Call it once per server.
   * Behind a reverse proxy, forward the WebSocket upgrade for that path (Apache: `ProxyPass /byoa/ws ws://127.0.0.1:PORT/byoa/ws`).
   */
  async attach(server, path = '/byoa/ws') {
    const { WebSocketServer } = await importWs();
    this._wss ??= new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME_BYTES });
    const onUpgrade = (request, socket, head) => {
      const pathname = (request.url ?? '').split('?')[0];
      if (pathname !== path) return; // not ours; let other upgrade handlers see it
      if (this._sockets.size >= this.limits.maxSockets) { socket.destroy(); return; }
      this._wss.handleUpgrade(request, socket, head, (ws) => this._serve(ws));
    };
    server.on('upgrade', onUpgrade);
    this._servers.push({ server, onUpgrade });
    return this;
  }

  /** Stops listening for wallets on every attached server (open connections stay until `closeAll`). */
  detach() {
    for (const { server, onUpgrade } of this._servers.splice(0)) server.off('upgrade', onUpgrade);
  }

  /** Serves one already-accepted `ws` WebSocket (for frameworks that do their own upgrade). */
  serve(ws) { return this._serve(ws); }

  /** Closes every wallet connection, welcomed or not, and stops the housekeeping timer (shutdown). */
  closeAll() {
    clearInterval(this._sweeper);
    for (const ws of this._sockets) { try { ws.close(1001, 'server stopping'); } catch { /* already gone */ } }
  }

  _serve(ws) {
    const conn = new WalletConnection(ws, this.connection);
    this._sockets.add(ws);
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
      if (frame.type === 'result') {
        conn._result(frame);
        this._touch(conn);
      } else if (frame.type === 'chunk') {
        conn._chunk(frame);
      } else if (frame.type === 'grant') {
        // the visitor changed this site's models or limits in the wallet: whole objects, as in hello. A grant frame carries no
        // capabilities: what the wallet said it understands at hello still stands.
        conn.grant = { ...conn.grant, limits: cleanLimits(frame.limits), models: cleanModels(frame.models), modelsBySkill: cleanModelsBySkill(frame.models) };
        conn._pump(); // a raised parallel limit lets waiting calls go
        conn.emit('grant', conn);
      } else if (frame.type === 'bye') {
        conn.close(1000, 'bye');
      }
      // ping and anything else: nothing to do
    });
    ws.on('close', () => { this._sockets.delete(ws); this._gone(conn); conn._ended(); });
    ws.on('error', () => conn.terminate());
  }

  _touch(conn) {
    const p = conn.pairing ? this._pairings.get(conn.pairing) : undefined;
    if (p?.live === conn) p.lastUsed = this._now();
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
    conn.grant = {
      skills,
      expiresAt: typeof frame.grant?.expiresAt === 'string' ? frame.grant.expiresAt : null,
      limits: cleanLimits(frame.grant?.limits),
      capabilities: cleanCapabilities(frame.grant?.capabilities) ?? [],
      models: cleanModels(frame.grant?.models),
      modelsBySkill: cleanModelsBySkill(frame.grant?.models),
    };
    conn._send({ type: 'welcome', pairing: welcome.pairing, manifest: welcome.manifest });
    armIdle();
    // a long-lived connection is let go now and then; the wallet comes back by itself with its pairing
    conn._lifeTimer = setTimeout(() => conn.bye(), this.limits.maxLifetimeMs);
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
      if (p.live && p.live !== conn) { p.live.replaced = true; p.live.bye(); } // at most one connection per pairing: the newest wins
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

  // A full table makes room instead of refusing: first the oldest pairing without a wallet; failing that, the live
  // pairing that has been used least recently (a connection that only pings is never "used", so it goes first).
  _makeRoom() {
    if (this._pairings.size < this.limits.maxPairings) return true;
    let victim = null;
    for (const p of this._pairings.values()) {
      const rank = (x) => (x.live ? 1 : 0);
      if (!victim || rank(p) < rank(victim) || (rank(p) === rank(victim) && p.lastUsed < victim.lastUsed)) victim = p;
    }
    if (!victim) return false;
    this._pairings.delete(victim.token);
    if (victim.live) { victim.live.bye(); }
    this.emit('evicted', victim.token);
    return true;
  }

  _sweep(now) {
    for (const [value, c] of this._codes) {
      if (now >= c.expires && (!c.used || now - c.expires > 10 * 60_000)) this._codes.delete(value); // a used code is kept a while: the page may still ask
    }
    for (const [token, p] of this._pairings) {
      if (!p.live && now - p.lastUsed > PAIRING_IDLE_MS) this._pairings.delete(token);
    }
    for (const map of [this._asked, this._askedPage]) {
      for (const [key, times] of map) {
        while (times.length > 0 && now - times[0] >= 60_000) times.shift();
        if (times.length === 0) map.delete(key);
      }
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
