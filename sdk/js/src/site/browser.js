// The page's side of BYOA (spec/CONTRACT.md and spec/CONTRACT-DIRECT.md): ask the visitor's agent wallet for
// permission and talk to it, through the browser extension (`window.byoa`) or, without any extension, through a
// bringyourownagent:// link that starts the wallet. No dependencies; runs in a browser as an ES module.
//
// Call both connect functions from a click handler: the extension needs a user click to show its notice, and
// a browser launches a custom protocol only after one.

/** True when the wallet's browser extension is installed (it puts `window.byoa` on the page). */
export function hasExtension() {
  return typeof window !== 'undefined' && typeof window.byoa?.request === 'function';
}

/**
 * Asks through the extension. Resolves with `{ grantId, skills, expiresAt }`, or `null`: declined, blocked,
 * no wallet, no fitting model, no answer: the page cannot tell these apart, and must use its own path.
 */
export async function requestWithExtension(manifest) {
  if (!hasExtension()) return null;
  try { return (await window.byoa.request(manifest)) ?? null; } catch { return null; }
}

const REASONS = ['timeout', 'cancelled', 'limit', 'failed'];

/** One use of a granted skill through the extension. Never throws: `{ ok: true, output, receipt }` or `{ ok: false }` (with `reason` when the wallet said why). */
export async function callWithExtension(grant, skill, input, params = {}) {
  if (!hasExtension() || !grant?.grantId) return { ok: false };
  try {
    const answer = await window.byoa.call(grant.grantId, skill, input, params);
    if (answer?.ok === true) return answer;
    return REASONS.includes(answer?.reason) ? { ok: false, reason: answer.reason } : { ok: false };
  } catch { return { ok: false }; }
}

/** Lets the wallet forget the grant when the page is done with it. */
export async function releaseWithExtension(grant) {
  try { await window.byoa?.release?.(grant.grantId); } catch { /* nothing to do */ }
}

/**
 * Resolves true as soon as the extension has put `window.byoa` on the page, or false after `timeoutMs`. The extension
 * announces itself with a `byoa:ready` event that may fire before your script runs, so `hasExtension()` first.
 */
export function whenExtensionReady(timeoutMs = 1500) {
  return new Promise((resolve) => {
    if (hasExtension()) { resolve(true); return; }
    if (typeof window === 'undefined') { resolve(false); return; }
    const done = (value) => { clearTimeout(timer); window.removeEventListener('byoa:ready', onReady); resolve(value); };
    const onReady = () => done(hasExtension());
    const timer = setTimeout(() => done(hasExtension()), timeoutMs);
    window.addEventListener('byoa:ready', onReady);
  });
}

/** Starts the wallet with its link. The page stays where it is: the browser asks the visitor first. */
export function openWalletLink(link) {
  if (typeof link !== 'string' || !link.startsWith('bringyourownagent://connect?')) throw new TypeError('not a BYOA link');
  window.location.href = link;
}

/**
 * Connects without the extension. Asks the site's server for a one-time code (it answers `{ code, link }`, see
 * `relay.newCode`), opens the link, and waits until the wallet has connected to the server.
 *
 * @param {object} [options]
 * @param {string} [options.newCodeUrl='/byoa/code']   POST: answers `{ code, link }`.
 * @param {string} [options.stateUrl='/byoa/state']    GET `?code=...`: answers `{ state, pairing? }`.
 * @param {string} [options.cancelUrl='/byoa/cancel']  POST `{ code }`: the page stopped waiting.
 * @param {number} [options.waitMs=90000]   After this long: `{ state: 'timeout' }`, with no other detail.
 * @param {number} [options.pollMs=1000]
 * @param {AbortSignal} [options.signal]    Abort to cancel: `{ state: 'cancelled' }`.
 * @param {(issued: { code: string, link: string, check?: string }) => void} [options.onIssued]  Called once the
 *   server has answered, before the link is opened: show `check` (the connect check code) in large letters while you
 *   wait, and the link itself, in case the visitor closed the browser's question.
 * @param {(link: string) => void} [options.open]  Replaces `openWalletLink`; on a phone, where nothing handles the
 *   link, pass `() => {}` and offer the link for another device instead (`bringyourownagent/crossdevice`).
 * @returns {Promise<{ state: 'connected', pairing: string } | { state: 'expired' | 'timeout' | 'cancelled' | 'error' }>}
 */
export function connectWithoutExtension(options = {}) {
  // a second click while the first is still waiting would open a second link: it gets the first one's answer
  if (connecting) return connecting;
  connecting = connect(options).finally(() => { connecting = null; });
  return connecting;
}

let connecting = null;

async function connect(options) {
  const {
    newCodeUrl = '/byoa/code', stateUrl = '/byoa/state', cancelUrl = '/byoa/cancel',
    waitMs = 90_000, signal, open = openWalletLink, onIssued,
  } = options;
  const pollMs = Math.max(options.pollMs ?? 1000, 5); // never a busy loop
  const fetchJson = async (url, init) => {
    const response = await fetch(url, { credentials: 'same-origin', ...init });
    if (!response.ok) throw new Error(`${response.status}`);
    return response.json();
  };
  let issued;
  try { issued = await fetchJson(newCodeUrl, { method: 'POST' }); } catch { return { state: 'error' }; }
  // `code` is this library's name for the one-time code; the first site that used the protocol called it `session`
  const code = issued?.code ?? issued?.session;
  if (typeof issued?.link !== 'string' || typeof code !== 'string') return { state: 'error' };
  issued.code = code;
  const check = typeof issued.check === 'string' ? issued.check : undefined;
  try { onIssued?.({ code, link: issued.link, ...(check ? { check } : {}) }); } catch { /* the page's own trouble */ }
  try { open(issued.link); } catch { return { state: 'error' }; }

  const until = Date.now() + waitMs;
  while (Date.now() < until) {
    if (signal?.aborted) break;
    await sleep(pollMs, signal);
    if (signal?.aborted) break;
    let answer;
    try { answer = await fetchJson(`${stateUrl}?code=${encodeURIComponent(issued.code)}`); } catch { continue; }
    if (answer?.state === 'connected' && typeof answer.pairing === 'string') return { state: 'connected', pairing: answer.pairing };
    if (answer?.state === 'expired') return { state: 'expired' };
  }
  fetch(cancelUrl, {
    method: 'POST', credentials: 'same-origin',
    headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code: issued.code }),
  }).catch(() => {});
  return { state: signal?.aborted ? 'cancelled' : 'timeout' };
}

function sleep(ms, signal) {
  return new Promise((resolve) => {
    const finish = () => { clearTimeout(timer); signal?.removeEventListener('abort', finish); resolve(); };
    const timer = setTimeout(finish, ms);
    signal?.addEventListener('abort', finish, { once: true });
  });
}
