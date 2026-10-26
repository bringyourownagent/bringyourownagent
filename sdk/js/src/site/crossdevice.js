// The page's "Open on another device" block (spec/CONTRACT-PAIR.md): a visitor who has the site open on a phone, or on a computer
// with no wallet, can hand the connection to the wallet he has somewhere else. The block shows the check code, the link (or the
// hand-off address the site made for it) as text he can copy, a Share button where the browser has one, a QR code, and, if the
// site offers one, a button for a web wallet. No dependencies, no network, no innerHTML: everything is built with the DOM and
// set as text, so a link or a label can never be read as markup.
//
// Call `createCrossDeviceBlock` after the site's server has answered `POST /byoa/code` with `{ link, check }`, and put the
// element where the page waits for the wallet. The page still polls its server as always: the wallet that takes the link
// connects to the site's server, and the page sees it there.

import { capacityBytes, encodeQr, qrPath } from './qr.js';

const SCHEME = 'bringyourownagent://connect?';

/**
 * The web wallet of the project ("Agent wallet (web)"): the block offers it, and the QR code opens it, unless the site passes its own address
 * or `webWallet: null`. A phone's camera does not open a `bringyourownagent://` link, but it opens this.
 */
export const DEFAULT_WEB_WALLET = 'https://wallet.bringyourownagent.org/';
const SVG_NS = 'http://www.w3.org/2000/svg';
const CHECK_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

/** The words of the block, in English. A site in another language passes its own in `labels`. */
export const DEFAULT_LABELS = Object.freeze({
  title: 'Open on another device',
  check: 'Check code',
  checkHow: 'Your wallet will show this code. Allow only if it matches.',
  linkHow: 'Send this link to the device where your wallet is. There, in the wallet, choose “Connect a site by link” and paste it.',
  handoffHow: 'Open this address on the device where your wallet is, and press “Open my wallet”.',
  walletAsks: 'The wallet asks you before it connects.',
  copy: 'Copy',
  copied: 'Copied.',
  copyFailed: 'Could not copy. Select the text and copy it by hand.',
  share: 'Share…',
  shareTitle: 'Agent wallet link',
  shareFailed: 'Could not share. Copy the text instead.',
  webWallet: 'Use the web wallet in this browser',
  qrLink: 'Or scan this with a device that has a wallet.',
  qrHandoff: 'Or scan this with the camera of the device where your wallet is.',
  qrWebWallet: 'Or scan this with a phone’s camera to use the web wallet there.',
  qrAlt: 'QR code',
});

/**
 * A little CSS for the block (the site adds it to its own style sheet, so its own content security policy decides how). The
 * check code is shown large and in a fixed-width face, as the wallet's window shows it.
 */
export const CROSS_DEVICE_CSS = `
.byoa-crossdevice { max-width: 28rem; }
.byoa-check-code { display: block; font: 700 2.2rem/1.2 ui-monospace, Consolas, monospace; letter-spacing: 0.3em; }
.byoa-link { width: 100%; box-sizing: border-box; font: 0.85rem ui-monospace, Consolas, monospace; }
.byoa-qr svg { width: 15rem; height: 15rem; max-width: 100%; }
`;

/** True for a phone or tablet browser: the page then does not fire the wallet's link itself (nothing there handles it) and offers this block. */
export function isMobileBrowser(nav = globalThis.navigator) {
  if (!nav) return false;
  if (typeof nav.userAgentData?.mobile === 'boolean') return nav.userAgentData.mobile;
  const ua = String(nav.userAgent ?? '');
  if (/Android|iPhone|iPad|iPod|Mobile|Silk/i.test(ua)) return true;
  return /Macintosh/.test(ua) && Number(nav.maxTouchPoints) > 1;          // an iPad that says it is a Mac
}

/** True where the browser has the Web Share API. */
export function canShare(nav = globalThis.navigator) {
  return typeof nav?.share === 'function';
}

function isLoopbackHttp(url) {
  return url.protocol === 'http:' && (url.hostname === 'localhost' || url.hostname === '127.0.0.1');
}

function assertLink(link) {
  if (typeof link !== 'string' || !link.startsWith(SCHEME)) throw new TypeError('not a BYOA link');
  if (!/^[A-Za-z0-9\-._~:/?&=%+]+$/.test(link)) throw new TypeError('the link has characters a link cannot have');
}

/**
 * The address that opens a web wallet with this link: `<wallet address>#link=<the link, as it is>`. The fragment is never sent to
 * a server; the web wallet reads it and takes it out of the address bar at once. A link holds only characters a fragment allows,
 * so it is not encoded again, which keeps the QR code of it small.
 * @param {string} walletBase  The web wallet's address, https (http only for localhost, in tests).
 * @param {string} link        The `bringyourownagent://connect?...` link.
 */
export function webWalletUrl(walletBase, link) {
  assertLink(link);
  const base = new URL(walletBase);
  if (base.protocol !== 'https:' && !isLoopbackHttp(base)) throw new TypeError('the web wallet address must be https');
  return `${base.origin}${base.pathname}#link=${link}`;
}

/** The session code of a link, or null. */
export function sessionOfLink(link) {
  if (typeof link !== 'string') return null;
  const at = link.indexOf('?');
  return at < 0 ? null : new URLSearchParams(link.slice(at + 1)).get('session');
}

/**
 * The connect check code of a session (CONTRACT-DIRECT section 3a): four characters of SHA-256("BYOA-CHECK-1:" + session).
 * The site's server already sends it as `check`; this is for a page that has only the link.
 */
export async function checkCodeOf(session, subtle = globalThis.crypto?.subtle) {
  if (!subtle) throw new Error('no Web Crypto here');
  const digest = new Uint8Array(await subtle.digest('SHA-256', new TextEncoder().encode(`BYOA-CHECK-1:${session}`)));
  return Array.from(digest.slice(0, 4), (b) => CHECK_ALPHABET[b % CHECK_ALPHABET.length]).join('');
}

/**
 * What the block offers: the text to copy and share (the hand-off address when the site has one, since an https address is clickable
 * everywhere, else the link), and what the QR code carries (the web wallet's address when a web wallet is offered, else the same text).
 * @returns {{ text: string, textKind: 'handoff'|'link', walletUrl: string|null, qrText: string, qrKind: 'webwallet'|'handoff'|'link' }}
 */
export function planCrossDevice({ link, handoff = null, webWallet = null }) {
  assertLink(link);
  if (handoff != null) {
    const url = new URL(handoff);
    if (url.protocol !== 'https:' && !isLoopbackHttp(url)) throw new TypeError('the hand-off address must be https');
  }
  const walletUrl = webWallet ? webWalletUrl(webWallet, link) : null;
  const text = handoff ?? link;
  return {
    text,
    textKind: handoff ? 'handoff' : 'link',
    walletUrl,
    qrText: walletUrl ?? text,
    qrKind: walletUrl ? 'webwallet' : handoff ? 'handoff' : 'link',
  };
}

function el(doc, tag, attrs = {}, ...children) {
  const node = doc.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value);
  for (const child of children) node.append(child);
  return node;
}

function qrElement(doc, qr, label, pixels) {
  const margin = 4;
  const total = qr.size + 2 * margin;
  const svg = doc.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', `0 0 ${total} ${total}`);
  svg.setAttribute('width', String(pixels));
  svg.setAttribute('height', String(pixels));
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', label);
  svg.setAttribute('shape-rendering', 'crispEdges');
  const ground = doc.createElementNS(SVG_NS, 'rect');
  ground.setAttribute('width', String(total));
  ground.setAttribute('height', String(total));
  ground.setAttribute('fill', '#ffffff');
  const dark = doc.createElementNS(SVG_NS, 'path');
  dark.setAttribute('d', qrPath(qr, margin));
  dark.setAttribute('fill', '#000000');
  svg.append(ground, dark);
  return svg;
}

/**
 * Builds the block.
 *
 * @param {object} options
 * @param {string} options.link         The `bringyourownagent://connect?...` link the server made for this page.
 * @param {string} [options.check]      The check code the server sent next to the link; shown large, with the line that tells the visitor to compare it.
 * @param {string} [options.handoff]    An https address the site made for this link (CONTRACT-PAIR section 2); offered instead of the raw link.
 * @param {string|null} [options.webWallet=DEFAULT_WEB_WALLET]  The address of a web wallet the site recommends; adds a button and makes the QR code open it. `null` offers none.
 * @param {boolean|'auto'} [options.showQr='auto']  'auto' hides the QR code on a phone (a phone cannot scan itself).
 * @param {'L'|'M'|'Q'|'H'} [options.qrEcl='L']
 * @param {number} [options.qrPixels=240]
 * @param {Record<string,string>} [options.labels]  Replaces words of DEFAULT_LABELS.
 * @param {Document} [options.document]  For tests.
 * @param {Navigator} [options.navigator]  For tests.
 * @returns {HTMLElement}
 */
export function createCrossDeviceBlock(options) {
  const doc = options.document ?? globalThis.document;
  const nav = options.navigator ?? globalThis.navigator;
  const labels = { ...DEFAULT_LABELS, ...options.labels };
  const plan = planCrossDevice({ ...options, webWallet: options.webWallet === undefined ? DEFAULT_WEB_WALLET : options.webWallet });
  if (options.check != null && !/^[ABCDEFGHJKMNPQRSTUVWXYZ2-9]{4}$/.test(options.check)) throw new TypeError('check must be four characters of the check alphabet');

  const root = el(doc, 'section', { class: 'byoa-crossdevice', 'aria-label': labels.title });
  root.append(el(doc, 'h3', {}, labels.title));

  if (options.check) {
    const code = el(doc, 'strong', { class: 'byoa-check-code', 'data-byoa-role': 'check', translate: 'no' }, options.check);
    root.append(el(doc, 'p', { class: 'byoa-check' }, el(doc, 'span', { class: 'byoa-check-label' }, labels.check), code));
    root.append(el(doc, 'p', { class: 'byoa-check-how' }, labels.checkHow));
  }

  root.append(el(doc, 'p', { class: 'byoa-how' }, `${plan.textKind === 'handoff' ? labels.handoffHow : labels.linkHow} ${labels.walletAsks}`));

  const box = el(doc, 'textarea', {
    class: 'byoa-link', 'data-byoa-role': 'text', readonly: '', rows: '4', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off',
    'aria-label': plan.textKind === 'handoff' ? labels.handoffHow : labels.linkHow,
  });
  box.value = plan.text;
  box.addEventListener('focus', () => box.select());
  root.append(box);

  const status = el(doc, 'p', { class: 'byoa-status', role: 'status', 'data-byoa-role': 'status' });
  const say = (text) => { status.textContent = text; };
  const buttons = el(doc, 'p', { class: 'byoa-buttons' });

  const copy = el(doc, 'button', { type: 'button', class: 'byoa-copy', 'data-byoa-role': 'copy' }, labels.copy);
  copy.addEventListener('click', async () => {
    say((await copyText(plan.text, nav, doc, box)) ? labels.copied : labels.copyFailed);
  });
  buttons.append(copy);

  if (canShare(nav)) {
    const share = el(doc, 'button', { type: 'button', class: 'byoa-share', 'data-byoa-role': 'share' }, labels.share);
    share.addEventListener('click', async () => {
      // An https address goes as a URL; the raw link goes as text, because a share sheet may not take a URL of an unknown scheme
      const data = plan.textKind === 'handoff' ? { title: labels.shareTitle, url: plan.text } : { title: labels.shareTitle, text: plan.text };
      try {
        await nav.share(data);
      } catch (error) {
        if (error?.name !== 'AbortError') say(labels.shareFailed);       // closing the share sheet is not a failure
      }
    });
    buttons.append(share);
  }
  root.append(buttons, status);

  if (plan.walletUrl) {
    const open = el(doc, 'a', { class: 'byoa-webwallet', 'data-byoa-role': 'webwallet', href: plan.walletUrl, target: '_blank', rel: 'noopener noreferrer' }, labels.webWallet);
    root.append(el(doc, 'p', {}, open));
  }

  const wantQr = options.showQr === true || ((options.showQr ?? 'auto') === 'auto' && !isMobileBrowser(nav));
  if (wantQr) {
    const ecl = options.qrEcl ?? 'L';
    const qr = plan.qrText.length <= capacityBytes(ecl) ? encodeQr(plan.qrText, ecl) : null;        // too long for any symbol: no code, and no complaint
    if (qr) {
      const note = plan.qrKind === 'webwallet' ? labels.qrWebWallet : plan.qrKind === 'handoff' ? labels.qrHandoff : labels.qrLink;
      root.append(el(doc, 'div', { class: 'byoa-qr', 'data-byoa-role': 'qr' }, qrElement(doc, qr, labels.qrAlt, options.qrPixels ?? 240), el(doc, 'p', { class: 'byoa-qr-note' }, note)));
    }
  }
  return root;
}

async function copyText(text, nav, doc, box) {
  try {
    await nav.clipboard.writeText(text);
    return true;
  } catch {
    // no clipboard permission, or an insecure page: select the text and ask the browser to copy it
  }
  try {
    box.select();
    box.setSelectionRange?.(0, text.length);
    return doc.execCommand?.('copy') === true;
  } catch {
    return false;
  }
}
