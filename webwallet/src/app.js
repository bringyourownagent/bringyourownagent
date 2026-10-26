// The web wallet's page: the screens and nothing else. The rules are in link.js, consent.js, wallet.js and openrouter.js, which are tested without a
// browser. Everything here is built with createElement and set as text: no innerHTML, no inline handlers, no script from anywhere but this folder.

import { buildConsent, canAllow } from './consent.js';
import { checkCode, linkFromFragment, normalizeHanded, parseLink } from './link.js';
import { authUrl, challengeOf, exchangeCode, listModels, makeVerifier } from './openrouter.js';
import { createStore } from './store.js';
import { grantedModels, SiteConnection } from './wallet.js';

const root = document.getElementById('app');
const store = createStore();
const OAUTH = 'byoa-web:oauth';
const PENDING = 'byoa-web:pending-link';
let connection = null;
let models = null;

function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) {
    if (name === 'class') el.className = value;
    else if (name.startsWith('on')) el.addEventListener(name.slice(2), value);
    else if (value === true) el.setAttribute(name, '');
    else if (value !== false && value != null) el.setAttribute(name, String(value));
  }
  for (const kid of kids.flat()) el.append(kid instanceof Node ? kid : String(kid));
  return el;
}

function show(...nodes) {
  root.replaceChildren(h('h1', {}, 'Agent wallet (web)'), ...nodes);
}

const trusted = (handler) => (event) => { if (event.isTrusted) handler(event); };    // a script cannot press Allow for the visitor

function home(message) {
  const box = h('textarea', { id: 'paste', rows: 4, placeholder: 'bringyourownagent://connect?...', 'aria-label': 'Paste the link a site gave you' });
  show(
    message ? h('p', { class: 'note', role: 'status' }, message) : '',
    h('p', {}, 'Lends your free OpenRouter models to a site you choose, after you allow it. Nothing is installed; keep this tab open while a site uses it.'),
    store.getKey() ? h('p', {}, 'You are signed in. ', h('button', { type: 'button', onclick: () => { store.clearKey(); home('Signed out. The key is gone from this browser.'); } }, 'Sign out')) : h('p', {}, h('button', { type: 'button', onclick: () => signIn(null) }, 'Sign in with OpenRouter')),
    h('h2', {}, 'Connect a site'),
    h('p', {}, 'A site shows a link and a short code. Paste the link here, or open it from the site.'),
    box,
    h('p', {}, h('button', { type: 'button', onclick: trusted(() => take(box.value)) }, 'Connect')),
  );
}

async function signIn(pendingLink) {
  const verifier = makeVerifier();
  const state = makeVerifier();
  sessionStorage.setItem(OAUTH, JSON.stringify({ state, verifier }));
  if (pendingLink) sessionStorage.setItem(PENDING, pendingLink);                  // the link lives 120 seconds: it is kept only as long as this tab
  const callbackUrl = `${location.origin}${location.pathname}`;
  location.assign(authUrl({ callbackUrl, challenge: await challengeOf(verifier), state }));
}

async function finishSignIn(params) {
  history.replaceState(null, '', location.pathname);
  let saved;
  try { saved = JSON.parse(sessionStorage.getItem(OAUTH) ?? 'null'); } catch { saved = null; }
  sessionStorage.removeItem(OAUTH);
  if (!saved || saved.state !== params.get('state') || !params.get('code')) return home('The sign-in did not match this tab. Try again.');
  try {
    const { key } = await exchangeCode({ code: params.get('code'), verifier: saved.verifier });
    if (!store.setKey(key)) return home('This browser would not keep the key. Check that it allows site storage.');
  } catch (error) {
    return home(`The sign-in failed (${error.kind ?? 'error'}). Try again.`);
  }
  const link = sessionStorage.getItem(PENDING);
  sessionStorage.removeItem(PENDING);
  return link ? take(link) : home('Signed in.');
}

async function take(text) {
  const handed = normalizeHanded(text);
  if (!handed.ok) return home(handed.why);
  const parsed = parseLink(handed.link);
  if (!parsed.ok) return home(`That link cannot be used: ${parsed.why}.`);
  if (!store.getKey()) {
    show(h('p', {}, `${parsed.link.host} wants to use your AI. Sign in with OpenRouter first; nothing is sent to the site before you allow it.`), h('button', { type: 'button', onclick: trusted(() => signIn(handed.link)) }, 'Sign in with OpenRouter'));
    return undefined;
  }
  show(h('p', { role: 'status' }, 'Loading the list of free models…'));
  if (models === null) {
    try { models = (await listModels()).filter((m) => m.free); } catch { return home('Could not load the list of models from OpenRouter. Try again.'); }
  }
  return consent(parsed.link, await checkCode(parsed.link.session));
}

function consent(link, check) {
  const before = store.getSite(link.origin);
  const spec = buildConsent({ host: link.host, manifest: link.manifestJson, check, hasKey: true, pickedIds: [] }).models;
  const wanted = before?.models ?? models.slice(0, spec?.min ?? 1).map((m) => m.id);
  const picked = new Set(wanted.filter((id) => models.some((m) => m.id === id)));
  let confirmed = false;
  let showNames = before?.showNames ?? true;

  const problems = h('ul', { class: 'problems' });
  const allow = h('button', { type: 'button', class: 'allow', onclick: trusted(() => grant(link, [...picked], showNames)) }, 'Allow');
  const list = h('div', { class: 'models', role: 'group', 'aria-label': 'Models to give this site' });
  const refresh = () => {
    const model = buildConsent({ host: link.host, manifest: link.manifestJson, check, hasKey: true, pickedIds: [...picked] });
    problems.replaceChildren(...model.blockers.map((b) => h('li', { class: 'blocker' }, b)), ...model.warnings.map((w) => h('li', { class: 'warning' }, w)));
    allow.disabled = !canAllow(model, { confirmed });
  };
  for (const m of models) {
    const box = h('input', { type: 'checkbox', checked: picked.has(m.id), onchange: () => { if (box.checked) picked.add(m.id); else picked.delete(m.id); refresh(); } });
    list.append(h('label', {}, box, ` ${m.name}`));
  }

  const view = buildConsent({ host: link.host, manifest: link.manifestJson, check, hasKey: true, pickedIds: [...picked] });
  const tick = h('input', { type: 'checkbox', onchange: (e) => { confirmed = e.target.checked; refresh(); } });
  show(
    h('h2', {}, view.host),
    h('p', { class: 'note' }, 'This link came from outside the browser tab. It is not proved by the browser: check the code.'),
    h('p', { class: 'check' }, 'Check code ', h('strong', { class: 'code', translate: 'no' }, view.check.code)),
    view.check.required
      ? h('p', {}, 'The page where you pressed Connect shows the same code. If you did not just press Connect on this site yourself, Decline.', h('br'), h('label', {}, tick, ' The code is the same on the site\'s page'))
      : h('p', { class: 'blocker' }, 'This site gives no way to check that you started this connection. Allow only if you pressed Connect on its page a moment ago.'),
    h('blockquote', {}, view.summary),
    h('p', {}, `Asks for: ${view.skills.map((s) => s.name + (s.served ? '' : ' (not available here)')).join(', ')}`),
    h('h3', {}, 'Give it these free models'),
    list,
    view.models?.names ? h('p', {}, h('label', {}, h('input', { type: 'checkbox', checked: showNames, onchange: (e) => { showNames = e.target.checked; } }), ' Let the site see the models\' names')) : '',
    h('p', {}, `Budget: nothing. Only free models are given, so this site cannot spend your credits.`),
    problems,
    h('ul', { class: 'notes' }, ...view.notes.map((n) => h('li', {}, n))),
    h('p', {}, allow, ' ', h('button', { type: 'button', onclick: trusted(() => home('Declined. Nothing was sent to the site.')) }, 'Decline')),
  );
  refresh();
}

async function grant(link, ids, showNames) {
  let keepAwake = null;                                                          // keeps a phone's screen on while a site is served
  const picked = ids.map((id) => models.find((m) => m.id === id)).filter(Boolean);
  const consentModel = buildConsent({ host: link.host, manifest: link.manifestJson, check: '', hasKey: true, pickedIds: ids });
  store.setSite(link.origin, { models: ids, showNames, budgetUsd: 0, allowedAt: new Date().toISOString() });
  const activity = h('ul', { class: 'activity', 'aria-label': 'Activity' });
  const status = h('p', { role: 'status' }, `Connecting to ${link.host}…`);
  connection = new SiteConnection({
    link,
    models: grantedModels(picked, { showNames }),
    limits: consentModel.limits,
    budgetUsd: 0,
    getKey: () => store.getKey(),
    onEvent: (event) => {
      if (event.type === 'state' && event.state === 'closed') keepAwake?.release();
      if (event.type === 'state') status.textContent = event.state === 'connected' ? `Connected to ${link.host}. Keep this tab open.` : event.state === 'closed' ? `Disconnected (${event.reason}).` : status.textContent;
      if (event.type === 'call') activity.prepend(h('li', {}, `${new Date().toLocaleTimeString()} ${event.ok ? 'answered' : `refused (${event.why})`} · ${event.ref ?? '-'}`));
      if (event.type === 'manifest-differs') status.textContent = 'The site\'s manifest differs from its link, so the wallet disconnected.';
    },
  });
  show(h('h2', {}, link.host), status, h('p', {}, h('button', { type: 'button', onclick: () => { connection?.close('you disconnected'); home('Disconnected.'); } }, 'Disconnect')), h('h3', {}, 'Activity'), activity);
  keepAwake = await navigator.wakeLock?.request('screen').catch(() => null);
  const result = await connection.start();
  if (!result.ok) status.textContent = `Could not connect (${result.reason}).`;
}

(async function boot() {
  const params = new URLSearchParams(location.search);
  if (params.has('code') && params.has('state')) return finishSignIn(params);
  const fromFragment = linkFromFragment(location.hash);
  if (fromFragment !== null) {
    history.replaceState(null, '', location.pathname + location.search);             // the link leaves the address bar and the history at once
    return take(fromFragment);
  }
  return home();
}());
