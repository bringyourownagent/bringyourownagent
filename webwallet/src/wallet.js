// The web wallet's side of the direct link (spec/CONTRACT-DIRECT.md section 4, spec/CONTRACT-PAIR.md section 4): after the visitor's Allow it opens a
// WebSocket to the site's endpoint, says hello, checks that the welcome echoes the link's manifest, and answers `text.generate` calls by asking
// OpenRouter with the visitor's key. It makes no network request of any kind before `start()`, which the consent screen calls only after Allow.
// All input and output goes through objects handed in (the socket, `fetch`, the timers, the clock), so a test can run it against a pretend site.

import { checkCall } from '../vendor/byoa/gate.js';
import { chat as openRouterChat } from './openrouter.js';

export const WALLET = Object.freeze({ name: 'Agent wallet (web)', version: '0.1', kind: 'web' });
export const MAX_FRAME_BYTES = 4 * 1024 * 1024;
export const MAX_MESSAGES = 128;
export const MAX_ANSWER_CHARS = 128_000;
export const CHUNK_EVERY_MS = 80;
export const CHUNK_MAX_CHARS = 16_000;
const PING_MS = 30_000;
const WELCOME_MS = 15_000;
const RETRY_WINDOW_MS = 60_000;
const BACKOFF_MS = [1_000, 2_000, 5_000, 10_000, 30_000];
const GRANT_HOURS = 12;
const ROLES = ['system', 'user', 'assistant'];

const byteLength = (text) => new TextEncoder().encode(text).length;
const iso = (ms) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/** A money string the gate reads: whole micro-dollars, rounded up so that a price is never rounded down to nothing. */
export function usd(amount) {
  return `${(Math.ceil(Math.max(0, amount) * 1e6) / 1e6).toFixed(6)} USD`;
}

/** A receipt's cost: at least three decimals ("0.000 USD"). */
function receiptCost(amount) {
  const text = (Math.ceil(Math.max(0, amount) * 1e6) / 1e6).toFixed(6).replace(/0{1,3}$/, '');
  return `${text} USD`;
}

/** One of the models the visitor ticked, as the wallet keeps it. `shown*` is what the site is told. */
export function grantedModels(picked, { showNames }) {
  return picked.map((m, i) => ({
    ref: `m${i + 1}`,
    id: m.id,
    free: m.free === true,
    pricePerMillion: m.pricePerMillion ?? { prompt: 0, completion: 0 },
    wire: {
      ref: `m${i + 1}`,
      name: showNames ? m.name : `Model ${String.fromCharCode(65 + (i % 26))}`,
      id: showNames ? m.id.replace(/:free$/, '') : null,
      runs: 'api-key',
      kind: 'model',
    },
  }));
}

/** Reads a call's messages: 1 to 128, each with a known role and a text. Null for anything else. */
function readMessages(input) {
  const list = input?.messages;
  if (!Array.isArray(list) || list.length === 0 || list.length > MAX_MESSAGES) return null;
  const messages = [];
  let chars = 0;
  for (const m of list) {
    if (!isObject(m) || !ROLES.includes(m.role) || typeof m.content !== 'string') return null;
    chars += m.content.length;
    messages.push({ role: m.role, content: m.content });
  }
  return { messages, chars };
}

/** The one of `choices` that an answer is, or null: trimmed, quotes and punctuation stripped, case ignored. */
export function pickChoice(choices, answer) {
  const clean = (s) => String(s).trim().replace(/^[\s"'`*_(\[]+|[\s"'`*_.,;:!?)\]]+$/g, '').toLowerCase();
  const wanted = clean(answer);
  return choices.find((c) => clean(c) === wanted) ?? null;
}

export class SiteConnection {
  /**
   * @param {object} options
   * @param {{ endpoint: string, manifest: string, manifestJson: object, session: string, origin: string, host: string }} options.link  The parsed link.
   * @param {ReturnType<typeof grantedModels>} options.models  The models the visitor gave the site, in his order.
   * @param {ReturnType<import('./consent.js').effectiveLimits>} options.limits
   * @param {number} [options.budgetUsd=0]  What the site may spend on paid models in this session (free models cost nothing).
   * @param {() => string | null} options.getKey  Called when a call needs the key; the wallet never holds it elsewhere.
   * @param {(url: string) => WebSocket} [options.createSocket]
   * @param {typeof fetch} [options.fetch]
   * @param {typeof openRouterChat} [options.chat]
   * @param {() => number} [options.now]
   * @param {{ setTimeout: Function, clearTimeout: Function, setInterval: Function, clearInterval: Function }} [options.timers]
   * @param {(event: object) => void} [options.onEvent]  State changes and one line per call; never a prompt or an answer.
   */
  constructor(options) {
    this.link = options.link;
    this.models = options.models;
    this.limits = options.limits;
    this.budgetUsd = options.budgetUsd ?? 0;
    this.getKey = options.getKey;
    this.createSocket = options.createSocket ?? ((url) => new WebSocket(url));
    this.fetch = options.fetch ?? globalThis.fetch?.bind(globalThis);
    this.chat = options.chat ?? openRouterChat;
    this.now = options.now ?? (() => Date.now());
    this.timers = options.timers ?? { setTimeout: (...a) => setTimeout(...a), clearTimeout: (...a) => clearTimeout(...a), setInterval: (...a) => setInterval(...a), clearInterval: (...a) => clearInterval(...a) };
    this.onEvent = options.onEvent ?? (() => {});
    this.state = 'idle';
    this.spentUsd = 0;
    this.callTimes = [];
    this.inFlight = new Set();
    this.expiresAt = 0;
    this.socket = null;
    this._pingTimer = null;
    this._welcomeTimer = null;
    this._settle = null;
    this._closed = false;
  }

  /** The `skills` the site asked for are served only if the visitor ticked models; the hello says what was granted.
   * `capabilities` is the closed list of CONTRACT.md section 3.3: this wallet reads none of those features, and
   * says so plainly instead of letting a site guess from missing keys. */
  helloFrame() {
    const asked = isObject(this.link.manifestJson?.skills?.['text.generate']?.models);
    const grant = {
      skills: ['text.generate'],
      expiresAt: iso(this.expiresAt),
      limits: { ...this.limits },
      capabilities: [],
    };
    if (asked) grant.models = { 'text.generate': this.models.map((m) => m.wire) };
    return { type: 'hello', v: '0.1', session: this.link.session, pairing: null, wallet: { ...WALLET }, grant };
  }

  /**
   * Connects. Resolves `{ ok: true }` once the site's welcome has echoed the link's manifest, or `{ ok: false, reason }`; never throws.
   * A site that answers `rate-limited` or `busy`, or cannot be reached yet, is tried again by the backoff for 60 seconds after the call.
   */
  async start() {
    if (this.state !== 'idle') return { ok: false, reason: 'already started' };
    this.state = 'connecting';
    this.expiresAt = this.now() + GRANT_HOURS * 3_600_000;
    const began = this.now();
    this.onEvent({ type: 'state', state: 'connecting' });
    for (let attempt = 0; ; attempt++) {
      const outcome = await this._attempt();
      if (outcome.ok) return outcome;
      const again = !this._closed && (outcome.reason === 'rate-limited' || outcome.reason === 'busy' || outcome.reason === 'network');
      const wait = BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)];
      if (!again || this.now() - began + wait > RETRY_WINDOW_MS) {
        this.close(outcome.reason);
        return outcome;
      }
      await new Promise((resolve) => this.timers.setTimeout(resolve, wait));
      if (this._closed) return outcome;
    }
  }

  _attempt() {
    return new Promise((resolve) => {
      let settled = false;
      const settle = (outcome) => {
        if (settled) return;
        settled = true;
        this.timers.clearTimeout(this._welcomeTimer);
        resolve(outcome);
      };
      this._settle = settle;
      let socket;
      try {
        socket = this.createSocket(this.link.endpoint);
      } catch {
        settle({ ok: false, reason: 'network' });
        return;
      }
      this.socket = socket;
      this._welcomed = false;
      socket.onopen = () => {
        this._send(this.helloFrame());
        this._welcomeTimer = this.timers.setTimeout(() => { this._dropSocket(); settle({ ok: false, reason: 'no welcome' }); }, WELCOME_MS);
      };
      socket.onmessage = (event) => this._onMessage(event.data, settle);
      socket.onerror = () => {};                       // a failure arrives as a close; the browser tells a page nothing more
      socket.onclose = () => {
        if (!this._welcomed) settle({ ok: false, reason: 'network' });
        else this.close('the connection ended');
      };
    });
  }

  _dropSocket() {
    const socket = this.socket;
    this.socket = null;
    if (socket) {
      socket.onmessage = socket.onclose = socket.onerror = socket.onopen = null;
      try { socket.close(); } catch { /* already closed */ }
    }
  }

  _send(frame) {
    try {
      this.socket?.send(JSON.stringify(frame));
      return true;
    } catch {
      return false;
    }
  }

  _onMessage(data, settle) {
    if (typeof data !== 'string') return;              // a binary frame is not part of the protocol: ignored
    if (data.length > MAX_FRAME_BYTES / 3 && byteLength(data) > MAX_FRAME_BYTES) { this.close('a frame over 4 MB'); settle({ ok: false, reason: 'frame too big' }); return; }
    let frame;
    try { frame = JSON.parse(data); } catch { frame = undefined; }
    if (!isObject(frame) || typeof frame.type !== 'string') { this.close('a frame that is not a message'); settle({ ok: false, reason: 'bad frame' }); return; }
    if (!this._welcomed) {
      if (frame.type === 'error') {
        const code = typeof frame.code === 'string' ? frame.code : 'error';
        this._dropSocket();
        settle({ ok: false, reason: code });
        return;
      }
      if (frame.type !== 'welcome') { this.close('the site spoke before its welcome'); settle({ ok: false, reason: 'no welcome' }); return; }
      if (frame.manifest !== this.link.manifest) {
        this._send({ type: 'bye' });
        this.onEvent({ type: 'manifest-differs' });
        this._dropSocket();
        settle({ ok: false, reason: 'manifest-differs' });
        return;
      }
      this._welcomed = true;
      this.state = 'connected';
      this._pingTimer = this.timers.setInterval(() => this._send({ type: 'ping' }), PING_MS);
      this.onEvent({ type: 'state', state: 'connected' });
      settle({ ok: true });
      return;
    }
    if (frame.type === 'call') void this._onCall(frame);
    else if (frame.type === 'bye') this.close('the site said bye');
    // any other frame is not ours to understand: ignored (CONTRACT-DIRECT.md section 5)
  }

  /** Says bye, closes the socket, stops the timers and drops the calls in flight. Safe to call twice. */
  close(reason = 'closed') {
    if (this._closed) return;
    this._closed = true;
    const wasConnected = this.state === 'connected';
    this.state = 'closed';
    this.timers.clearInterval(this._pingTimer);
    this.timers.clearTimeout(this._welcomeTimer);
    for (const abort of this.inFlight) abort.abort();
    this.inFlight.clear();
    if (wasConnected) this._send({ type: 'bye' });
    this._dropSocket();
    this._settle?.({ ok: false, reason });
    this.onEvent({ type: 'state', state: 'closed', reason });
  }

  _refuse(id, skill, ref, why) {
    this._send({ type: 'result', id, ok: false });
    this.onEvent({ type: 'call', skill, ref: ref ?? null, ok: false, why, costUsd: 0 });
  }

  /** The gate of the rules (limits, rate, budget, expiry) for one call, with what the wallet itself holds the site to. */
  _gate(manifest, textChars, estimateUsd) {
    const at = this.now();
    const grant = {
      byoa: '0.1-draft',
      skills: ['text.generate'],
      budget: { perSession: usd(this.budgetUsd) },
      expiresAt: iso(this.expiresAt),
      limits: { maxCallsPerMinute: this.limits.maxCallsPerMinute, maxTextChars: this.limits.maxTextChars },
    };
    const call = { skill: 'text.generate', at: iso(at), provider: 'api-key', estimatedCost: usd(estimateUsd), input: { textChars } };
    const state = { spent: usd(this.spentUsd), callTimes: this.callTimes.map(iso) };
    return checkCall(manifest, grant, call, state);
  }

  async _onCall(frame) {
    const id = frame.id;
    if (!Number.isInteger(id)) return;                                         // there is no one to answer
    const skill = typeof frame.skill === 'string' ? frame.skill : '';
    const started = this.now();
    const input = readMessages(frame.input);
    const params = isObject(frame.params) ? frame.params : {};
    if (skill !== 'text.generate') return this._refuse(id, skill, null, 'skill not served');
    if (!input) return this._refuse(id, skill, null, 'bad messages');
    if (input.chars > this.limits.maxTextChars) return this._refuse(id, skill, null, 'text too long');
    if (this.inFlight.size >= this.limits.maxParallelCalls) return this._refuse(id, skill, null, 'too many at once');
    if (this.now() >= this.expiresAt) return this._refuse(id, skill, null, 'grant ended');

    // which models: the one the call names (never swapped for another), or the visitor's own order until one answers
    let route = this.models;
    const named = typeof params.model === 'string' ? params.model : null;
    if (named !== null) {
      route = this.models.filter((m) => m.ref === named);
      if (route.length === 0) return this._refuse(id, skill, named, 'model not given');
    }
    if (route.length === 0) return this._refuse(id, skill, null, 'no model');

    const choices = Array.isArray(params.choices) && params.choices.length > 0 && params.choices.length <= 64
      && params.choices.every((c) => typeof c === 'string' && c.length > 0 && c.length <= 200) ? params.choices : null;
    if (Array.isArray(params.choices) && choices === null) return this._refuse(id, skill, named, 'bad choices');
    const maxTokens = Math.max(1, Math.min(this.limits.maxAnswerTokens, Number.isFinite(params.maxTokens) ? Math.floor(params.maxTokens) : this.limits.maxAnswerTokens));
    const temperature = Number.isFinite(params.temperature) ? Math.min(1, Math.max(0, params.temperature)) : undefined;
    const stream = params.stream === true && choices === null;
    const messages = choices === null ? input.messages
      : [...input.messages, { role: 'system', content: `Answer with exactly one of these and nothing else: ${choices.join(' | ')}` }];

    const abort = new AbortController();
    this.inFlight.add(abort);
    const timeout = this.timers.setTimeout(() => abort.abort(), this.limits.maxCallSeconds * 1000);
    const pieces = new Chunker((text) => this._send({ type: 'chunk', id, text }), this.now, this.timers);
    try {
      for (const model of route) {
        const estimate = model.free ? 0 : (input.chars / 4) * (model.pricePerMillion.prompt / 1e6) + maxTokens * (model.pricePerMillion.completion / 1e6);
        const verdict = this._gate(this.link.manifestJson, input.chars, estimate);
        if (verdict.decision === 'blocked' || verdict.decision === 'ask') {
          if (named !== null || model === route[route.length - 1]) return this._refuse(id, skill, named, verdict.reasons[0]?.code ?? verdict.decision);
          continue;
        }
        const key = this.getKey();
        if (!key) return this._refuse(id, skill, named, 'no key');
        this.callTimes.push(this.now());
        let answer;
        try {
          answer = await this.chat({
            key, model: model.id, messages, maxTokens, temperature, stream, signal: abort.signal, fetch: this.fetch,
            onDelta: stream ? (piece) => pieces.add(piece) : undefined,
          });
        } catch (error) {
          if (abort.signal.aborted) return this._refuse(id, skill, named, this._closed ? 'closed' : 'timed out');
          if (named !== null || model === route[route.length - 1]) return this._refuse(id, skill, named, error?.kind ?? 'failed');
          continue;
        }
        const cost = answer.costUsd ?? (model.free ? 0 : estimate);
        this.spentUsd += cost;
        let text = answer.text.trim();
        if (choices !== null) text = pickChoice(choices, text) ?? '';
        if (text.length === 0) {
          if (named !== null || model === route[route.length - 1]) return this._refuse(id, skill, named, 'empty answer');
          continue;
        }
        pieces.flush();
        if (this._closed) return undefined;
        text = text.slice(0, MAX_ANSWER_CHARS);
        const receipt = { cost: receiptCost(cost), provider: 'api-key' };
        if (named !== null) receipt.model = named;
        this._send({ type: 'result', id, ok: true, output: { text }, receipt });
        this.onEvent({ type: 'call', skill, ref: model.ref, ok: true, costUsd: cost, ms: this.now() - started });
        return undefined;
      }
      return this._refuse(id, skill, named, 'no model answered');
    } finally {
      pieces.stop();
      this.timers.clearTimeout(timeout);
      this.inFlight.delete(abort);
    }
  }
}

/** Gathers the pieces of a streamed answer into `chunk` frames: at most one every 80 ms, none over 16,000 characters. */
class Chunker {
  constructor(send, now, timers) {
    this.send = send;
    this.now = now;
    this.timers = timers;
    this.buffer = '';
    this.last = -Infinity;
    this.timer = null;
  }

  add(piece) {
    this.buffer += piece;
    if (this.timer !== null) return;
    const wait = Math.max(0, this.last + CHUNK_EVERY_MS - this.now());
    if (wait === 0) this.flush();
    else this.timer = this.timers.setTimeout(() => { this.timer = null; this.flush(); }, wait);
  }

  flush() {
    while (this.buffer.length > 0) {
      const text = this.buffer.slice(0, CHUNK_MAX_CHARS);
      this.buffer = this.buffer.slice(text.length);
      this.send(text);
      this.last = this.now();
    }
  }

  stop() {
    this.timers.clearTimeout(this.timer);
    this.timer = null;
    this.buffer = '';
  }
}
