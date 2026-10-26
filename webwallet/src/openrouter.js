// The web wallet's one source: OpenRouter, with the visitor's own key. Three things happen here: the sign-in (OAuth with PKCE, from OpenRouter's
// documentation), the list of free models (a public list, no key), and one chat call (whole, or in pieces as it is written).
// Every function takes `fetch` so that a test can stand in for the network. The key goes only into the Authorization header of a request
// to OpenRouter; it is never put in an error, a log or a message.

export const ORIGIN = 'https://openrouter.ai';
const AUTH_URL = `${ORIGIN}/auth`;
const KEYS_URL = `${ORIGIN}/api/v1/auth/keys`;
const MODELS_URL = `${ORIGIN}/api/v1/models`;
const CHAT_URL = `${ORIGIN}/api/v1/chat/completions`;

/** A failure that says only what kind it was and the HTTP status: a provider's own words may carry the visitor's text, so they are never kept. */
export class SourceError extends Error {
  constructor(kind, status) {
    super(status ? `${kind} (HTTP ${status})` : kind);
    this.name = 'SourceError';
    this.kind = kind;
    this.status = status ?? null;
  }
}

const kindOf = (status) => (status === 401 || status === 403 ? 'key' : status === 402 ? 'credits' : status === 429 ? 'quota' : status >= 500 ? 'provider' : 'refused');

function base64Url(bytes) {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

/** A fresh code verifier: 256 random bits, 43 URL-safe characters (RFC 7636 asks for 43 to 128). */
export function makeVerifier(random = globalThis.crypto) {
  return base64Url(random.getRandomValues(new Uint8Array(32)));
}

/** The S256 challenge of a verifier: base64url of its SHA-256. */
export async function challengeOf(verifier, subtle = globalThis.crypto?.subtle) {
  return base64Url(new Uint8Array(await subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
}

/**
 * Where the visitor is sent to sign in. The callback is this wallet's own page; `state` comes back unchanged and the page checks it.
 * @param {{ callbackUrl: string, challenge: string, state: string, label?: string }} options
 */
export function authUrl({ callbackUrl, challenge, state, label = 'Agent wallet (web)' }) {
  const url = new URL(AUTH_URL);
  url.searchParams.set('callback_url', callbackUrl);
  url.searchParams.set('code_challenge', challenge);
  url.searchParams.set('code_challenge_method', 'S256');
  url.searchParams.set('state', state);
  url.searchParams.set('key_label', label);
  return url.href;
}

/** Swaps the code the sign-in returned for the visitor's key. Throws a SourceError (never with the key or the provider's words). */
export async function exchangeCode({ code, verifier, fetch = globalThis.fetch }) {
  let response;
  try {
    response = await fetch(KEYS_URL, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ code, code_verifier: verifier, code_challenge_method: 'S256' }),
      referrerPolicy: 'no-referrer',
      credentials: 'omit',
    });
  } catch {
    throw new SourceError('network');
  }
  if (!response.ok) throw new SourceError(kindOf(response.status), response.status);
  let body;
  try { body = await response.json(); } catch { throw new SourceError('provider', response.status); }
  if (typeof body?.key !== 'string' || body.key.length === 0) throw new SourceError('provider', response.status);
  return { key: body.key };
}

/**
 * A model of OpenRouter's public list, as the wallet needs it.
 * @typedef {{ id: string, name: string, free: boolean, context: number|null, pricePerMillion: { prompt: number, completion: number } }} ModelInfo
 */

const priceOf = (value) => { const n = Number(value); return Number.isFinite(n) && n >= 0 ? n : null; };

/** Reads OpenRouter's list into ModelInfo: only models that take text and answer in text. A model is free when both prices are zero. */
export function readModels(body) {
  const list = Array.isArray(body?.data) ? body.data : [];
  const models = [];
  for (const m of list) {
    if (typeof m?.id !== 'string' || m.id.length === 0 || m.id.length > 200) continue;
    const out = m.architecture?.output_modalities;
    if (Array.isArray(out) && !out.includes('text')) continue;
    const input = m.architecture?.input_modalities;
    if (Array.isArray(input) && !input.includes('text')) continue;
    const prompt = priceOf(m.pricing?.prompt);
    const completion = priceOf(m.pricing?.completion);
    if (prompt === null || completion === null) continue;                       // a price the wallet cannot read is not a price it will spend on
    models.push({
      id: m.id,
      name: typeof m.name === 'string' && m.name.length > 0 ? m.name.slice(0, 120) : m.id,
      free: prompt === 0 && completion === 0,
      context: Number.isFinite(m.context_length) ? m.context_length : null,
      pricePerMillion: { prompt: prompt * 1e6, completion: completion * 1e6 },
    });
  }
  return models.sort((a, b) => a.name.localeCompare(b.name));
}

/** The public model list (no key is sent). */
export async function listModels({ fetch = globalThis.fetch } = {}) {
  let response;
  try {
    response = await fetch(MODELS_URL, { method: 'GET', referrerPolicy: 'no-referrer', credentials: 'omit' });
  } catch {
    throw new SourceError('network');
  }
  if (!response.ok) throw new SourceError(kindOf(response.status), response.status);
  try { return readModels(await response.json()); } catch { throw new SourceError('provider', response.status); }
}

/** Parses an event stream as it arrives in pieces: calls `onEvent` with each `data:` payload that is JSON, and returns when the stream ends. */
async function readEvents(body, onEvent) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let pending = '';
  const flush = (line) => {
    if (!line.startsWith('data:')) return;                                      // comments (": OPENROUTER PROCESSING") and other fields
    const data = line.slice(5).trim();
    if (data === '' || data === '[DONE]') return;
    let parsed;
    try { parsed = JSON.parse(data); } catch { return; }
    onEvent(parsed);
  };
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    pending += decoder.decode(value, { stream: true });
    let at;
    while ((at = pending.search(/\r?\n/)) >= 0) {
      flush(pending.slice(0, at));
      pending = pending.slice(pending[at] === '\r' ? at + 2 : at + 1);
    }
  }
  pending += decoder.decode();
  if (pending.length > 0) flush(pending);
}

/**
 * One chat call. With `stream`, `onDelta` gets each piece of the answer as it is written. Returns the whole answer and what it cost.
 * @param {object} call
 * @param {string} call.key
 * @param {string} call.model
 * @param {{ role: string, content: string }[]} call.messages
 * @param {number} [call.maxTokens]
 * @param {number} [call.temperature]
 * @param {boolean} [call.stream]
 * @param {(piece: string) => void} [call.onDelta]
 * @param {AbortSignal} [call.signal]
 * @returns {Promise<{ text: string, costUsd: number|null }>}
 */
export async function chat({ key, model, messages, maxTokens, temperature, stream = false, onDelta, signal, fetch = globalThis.fetch }) {
  const body = { model, messages, stream, usage: { include: true } };
  if (maxTokens !== undefined) body.max_tokens = maxTokens;
  if (temperature !== undefined) body.temperature = temperature;
  let response;
  try {
    response = await fetch(CHAT_URL, {
      method: 'POST',
      headers: { authorization: `Bearer ${key}`, 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal,
      referrerPolicy: 'no-referrer',
      credentials: 'omit',
    });
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    throw new SourceError('network');
  }
  if (!response.ok) throw new SourceError(kindOf(response.status), response.status);
  if (!stream) {
    let data;
    try { data = await response.json(); } catch { throw new SourceError('provider', response.status); }
    const text = data?.choices?.[0]?.message?.content;
    return { text: typeof text === 'string' ? text : '', costUsd: priceOf(data?.usage?.cost) };
  }
  if (!response.body) throw new SourceError('provider', response.status);
  let text = '';
  let costUsd = null;
  await readEvents(response.body, (event) => {
    const piece = event?.choices?.[0]?.delta?.content;
    if (typeof piece === 'string' && piece.length > 0) {
      text += piece;
      onDelta?.(piece);
    }
    if (event?.usage?.cost !== undefined) costUsd = priceOf(event.usage.cost);
  });
  return { text, costUsd };
}
