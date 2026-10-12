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

/** One use of a granted skill through the extension. Never throws: `{ ok: true, output, receipt }` or `{ ok: false }`. */
export async function callWithExtension(grant, skill, input, params = {}) {
  if (!hasExtension() || !grant?.grantId) return { ok: false };
  try {
    const answer = await window.byoa.call(grant.grantId, skill, input, params);
    return answer?.ok === true ? answer : { ok: false };
  } catch { return { ok: false }; }
}

/** Lets the wallet forget the grant when the page is done with it. */
export async function releaseWithExtension(grant) {
  try { await window.byoa?.release?.(grant.grantId); } catch { /* nothing to do */ }
}

/** Starts the wallet with its link. The page stays where it is: the browser asks the visitor first. */
export function openWalletLink(link) {
  if (typeof link !== 'string' || !link.startsWith('bringyourownagent://')) throw new TypeError('not a BYOA link');
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
 * @param {(link: string) => void} [options.open]  Replaces `openWalletLink` (tests).
 * @returns {Promise<{ state: 'connected', pairing: string } | { state: 'expired' | 'timeout' | 'cancelled' | 'error' }>}
 */
export async function connectWithoutExtension(options = {}) {
  const {
    newCodeUrl = '/byoa/code', stateUrl = '/byoa/state', cancelUrl = '/byoa/cancel',
    waitMs = 90_000, pollMs = 1000, signal, open = openWalletLink,
  } = options;
  const fetchJson = async (url, init) => {
    const response = await fetch(url, { credentials: 'same-origin', ...init });
    if (!response.ok) throw new Error(`${response.status}`);
    return response.json();
  };
  let issued;
  try { issued = await fetchJson(newCodeUrl, { method: 'POST' }); } catch { return { state: 'error' }; }
  if (typeof issued?.link !== 'string' || typeof issued?.code !== 'string') return { state: 'error' };
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
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => { clearTimeout(timer); resolve(); }, { once: true });
  });
}
