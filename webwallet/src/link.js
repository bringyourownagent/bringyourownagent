// A bringyourownagent://connect link as the web wallet takes it (spec/CONTRACT-DIRECT.md sections 1 and 7, spec/CONTRACT-PAIR.md sections 1 and 4).
// The same checks as the native wallet makes, in the same order, so that a link one wallet refuses the other refuses too. Nothing here
// touches the network: the manifest comes in the link, the check code is computed from it, and the consent screen is built from both.

export const SCHEME = 'bringyourownagent';
export const MAX_LINK = 12288;
export const MAX_MANIFEST_BYTES = 4096;
const MAX_PASTED = MAX_LINK * 2;
const MAX_PARAMETERS = 16;
const MAX_URL = 2048;
const CHECK_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const LOCAL_SUFFIXES = ['.local', '.home.arpa', '.internal', '.lan', '.intranet'];
const chars = (...codes) => String.fromCharCode(...codes);
const ZERO_WIDTH = [0x200b, 0x200c, 0x200d, 0x2060, 0xfeff].map((c) => chars(c));
const WRAP_PAIRS = ['""', "''", '<>', '()', chars(0x201c, 0x201d), chars(0x2018, 0x2019)];
const LINE_BREAKS = new RegExp(`[${chars(0x0d, 0x0a, 0x85, 0x2028, 0x2029)}]+`);

const fail = (why) => ({ ok: false, why });

/** No control characters, no space, nothing outside plain ASCII: what a link and a URL are made of. */
const isPlainAscii = (text) => { for (const ch of text) { const c = ch.codePointAt(0); if (c <= 0x20 || c >= 0x7f) return false; } return true; };

/**
 * The link out of what the address bar's fragment holds: `#link=<the link, as it is>` (what a site's QR code carries) or the same
 * written as one percent-encoded value. Returns the text, or null when the fragment carries no link.
 */
export function linkFromFragment(hash) {
  if (typeof hash !== 'string' || !hash.startsWith('#link=')) return null;
  const value = hash.slice('#link='.length);
  if (value.toLowerCase().startsWith(`${SCHEME}%3a`)) {
    try { return decodeURIComponent(value); } catch { return null; }
  }
  return value;
}

/**
 * The one link in what the visitor pasted (CONTRACT-PAIR.md section 1): spaces, one pair of quotes or brackets and zero-width characters around
 * it are taken out, and so are line breaks inside it (a messenger wrapped it); a space inside a line is more text, and more than one link is refused.
 * @returns {{ ok: true, link: string } | { ok: false, why: string }}
 */
export function normalizeHanded(text) {
  let t = String(text ?? '').trim();
  if (t.length === 0) return fail('Nothing was pasted.');
  if (t.length > MAX_PASTED) return fail('That text is too long to be a link.');
  if (t.length >= 2) {
    const pair = `${t[0]}${t[t.length - 1]}`;
    if (WRAP_PAIRS.includes(pair)) t = t.slice(1, -1).trim();
  }
  let joined = '';
  for (const raw of t.split(LINE_BREAKS)) {
    const line = raw.trim();
    if (line.length === 0) continue;
    if (/\s/.test(line.replaceAll(new RegExp(`[${ZERO_WIDTH.join('')}]`, 'g'), ''))) return fail('That is not a single link. Paste only the one link the site gave you.');
    joined += line;
  }
  for (const z of ZERO_WIDTH) joined = joined.replaceAll(z, '');
  const first = joined.toLowerCase().indexOf(`${SCHEME}:`);
  if (first < 0) return fail(`That is not a link from a site. A site's link starts with ${SCHEME}://`);
  if (first > 0 || joined.toLowerCase().indexOf(`${SCHEME}:`, first + 1) >= 0) return fail('That is not a single link. Paste only the one link the site gave you.');
  return { ok: true, link: joined };
}

function base64UrlToBytes(text) {
  const padded = text.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - (text.length % 4)) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}

/** The manifest of a link: base64url without padding, at most 4096 bytes, a JSON object. */
function readManifest(text) {
  if (!text) return fail('the link has no manifest');
  if (text.length > Math.ceil((MAX_MANIFEST_BYTES * 4) / 3) + 2) return fail('the manifest is larger than 4096 bytes');
  if (!/^[A-Za-z0-9_-]+$/.test(text)) return fail('the manifest is not base64url without padding');
  if (text.length % 4 === 1) return fail('the manifest is not base64url without padding');
  let bytes;
  try { bytes = base64UrlToBytes(text); } catch { return fail('the manifest is not base64url without padding'); }
  if (bytes.length > MAX_MANIFEST_BYTES) return fail('the manifest is larger than 4096 bytes');
  let parsed;
  try { parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); } catch { return fail('the manifest is not JSON'); }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return fail('the manifest is not a JSON object');
  return { ok: true, manifest: parsed };
}

/** The endpoint: wss only, no user name, no fragment, only public DNS names. The browser cannot resolve names, so the address check the native wallet makes is left to the browser. */
function readEndpoint(text, devLocal) {
  if (text.length === 0 || text.length > MAX_URL) return fail('the endpoint URL is empty or too long');
  if (!isPlainAscii(text) || text.includes(String.fromCharCode(92))) return fail('the endpoint URL has characters a URL cannot have');
  let url;
  try { url = new URL(text); } catch { return fail('the endpoint is not a URL'); }
  const local = url.hostname === 'localhost';
  if (url.protocol === 'ws:') {
    if (!(devLocal && local)) return fail('the endpoint is not wss://');
  } else if (url.protocol !== 'wss:') {
    return fail('the endpoint is not wss://');
  }
  if (url.username || url.password) return fail('the endpoint URL has a user name');
  if (url.hash || text.includes('#')) return fail('the endpoint URL has a fragment');
  const secure = url.protocol === 'wss:';
  const name = url.hostname.toLowerCase();
  if (!(devLocal && local)) {
    if (name.startsWith('[') || name.includes(':') || /^[0-9.]+$/.test(name)) return fail('the endpoint host is an IP address');
    if (name.length > 253 || name.endsWith('.')) return fail('the endpoint host is not a usual DNS name');
    if (name === 'localhost' || name.endsWith('.localhost')) return fail('the endpoint host is localhost');
    if (LOCAL_SUFFIXES.some((s) => name.endsWith(s))) return fail('the endpoint host is a local network name');
    const labels = name.split('.');
    if (labels.length < 2) return fail('the endpoint host is a single-label (local) name');
    for (const label of labels) {
      if (label.length === 0 || label.length > 63 || label.startsWith('-') || label.endsWith('-') || !/^[a-z0-9-]+$/.test(label)) return fail('the endpoint host is not a usual DNS name');
    }
    if (/^[0-9]+$/.test(labels[labels.length - 1])) return fail('the endpoint host is an IP address');
  }
  const defaultPort = secure ? '443' : '80';
  const host = url.port === '' || url.port === defaultPort ? name : `${name}:${url.port}`;
  return { ok: true, endpoint: url.href, origin: `${secure ? 'https' : 'http'}://${host}`, host };
}

/** True for at least 128 bits of URL-safe characters: 22 to 256 of A-Z a-z 0-9 - _ . */
export function isSessionCode(code) {
  return typeof code === 'string' && code.length >= 22 && code.length <= 256 && /^[A-Za-z0-9_-]+$/.test(code);
}

/**
 * Parses and checks a link. On failure `why` says why, in a few words.
 * @param {string} text
 * @param {{ devLocal?: boolean }} [options]  devLocal allows ws://localhost (tests only).
 * @returns {{ ok: true, link: { endpoint: string, manifest: string, manifestJson: object, session: string, origin: string, host: string } } | { ok: false, why: string }}
 */
export function parseLink(text, { devLocal = false } = {}) {
  if (typeof text !== 'string' || text.length === 0) return fail('an empty link');
  if (text.length > MAX_LINK) return fail('the link is too long');
  if (!isPlainAscii(text)) return fail('the link has characters a link cannot have');
  const colon = text.indexOf(':');
  if (colon < 0 || text.slice(0, colon).toLowerCase() !== SCHEME) return fail('not a bringyourownagent link');
  if (text.includes('#')) return fail('the link has a fragment');
  const rest = text.slice(colon + 1);
  const question = rest.indexOf('?');
  if (!/^\/\/connect\/?$/i.test(question < 0 ? rest : rest.slice(0, question))) return fail('an unknown kind of link');
  if (question < 0) return fail('the link has no parameters');
  const parts = rest.slice(question + 1).split('&').filter((p) => p.length > 0);
  if (parts.length > MAX_PARAMETERS) return fail('the link has too many parameters');
  const params = new Map();
  for (const part of parts) {
    const eq = part.indexOf('=');
    const key = eq < 0 ? part : part.slice(0, eq);
    let value;
    try { value = eq < 0 ? '' : decodeURIComponent(part.slice(eq + 1)); } catch { return fail('the link is badly encoded'); }
    if (params.has(key)) return fail(`the link repeats the parameter ${key.slice(0, 24)}`);
    params.set(key, value);
  }
  if (!params.has('v')) return fail('the link has no version');
  if (params.get('v') !== '0.1') return fail('an unsupported link version');
  const session = params.get('session');
  if (!isSessionCode(session)) return fail('the link has no valid session code');
  if (!params.has('endpoint')) return fail('the link has no endpoint');
  if (!params.has('manifest')) return fail('the link has no manifest');
  const manifest = readManifest(params.get('manifest'));
  if (!manifest.ok) return manifest;
  const endpoint = readEndpoint(params.get('endpoint'), devLocal);
  if (!endpoint.ok) return endpoint;
  return {
    ok: true,
    link: { endpoint: endpoint.endpoint, manifest: params.get('manifest'), manifestJson: manifest.manifest, session, origin: endpoint.origin, host: endpoint.host },
  };
}

/** The connect check code of a session (CONTRACT-DIRECT.md section 3a): character i is ALPHABET[SHA-256("BYOA-CHECK-1:" + session)[i] mod 31]. */
export async function checkCode(session, subtle = globalThis.crypto?.subtle) {
  if (!subtle) throw new Error('no Web Crypto here');
  const digest = new Uint8Array(await subtle.digest('SHA-256', new TextEncoder().encode(`BYOA-CHECK-1:${session}`)));
  return Array.from(digest.slice(0, 4), (b) => CHECK_ALPHABET[b % CHECK_ALPHABET.length]).join('');
}
