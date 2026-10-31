// BYOA agent wallet 0.1: the service worker. It talks to the native host org.bringyourownagent.wallet
// (byoa-host.exe), which relays to the desktop wallet. It adds the page's origin from Chrome's sender
// information (never from the page), keeps only top frames of https pages and http://localhost pages,
// enforces time limits, and passes the wallet's answers back in the contract's shapes only.
"use strict";

const HOST = "org.bringyourownagent.wallet";
// A call may wait in the wallet for its own limit (up to ten minutes: a model that is still starting, a long answer), so the extension waits a little longer.
const TIMEOUT_MS = { request: 125000, call: 610000, release: 10000, status: 4000, open: 30000 };
const IDLE_MS = 120000;

let port = null;
let lastError = "";
let nextId = 1;
let idleTimer = null;
const pending = new Map();

function fallback(type) {
  if (type === "call") return { ok: false };
  if (type === "status") return { running: false, installed: !/not found/i.test(lastError) };
  return null;
}

function connect() {
  if (port) return port;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    lastError = String(e && e.message || e);
    port = null;
    return null;
  }
  lastError = "";
  port.onMessage.addListener((message) => {
    const id = message && message.id;
    const waiting = pending.get(id);
    if (!waiting) return;
    pending.delete(id);
    clearTimeout(waiting.timer);
    waiting.resolve(message.result);
    scheduleIdle();
  });
  port.onDisconnect.addListener(() => {
    lastError = (chrome.runtime.lastError && chrome.runtime.lastError.message) || "disconnected";
    port = null;
    for (const [, waiting] of pending) {
      clearTimeout(waiting.timer);
      waiting.resolve(fallback(waiting.type));
    }
    pending.clear();
  });
  return port;
}

// An idle port is closed after a while, so that the service worker and the host process can rest.
function scheduleIdle() {
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    if (port && pending.size === 0) {
      port.disconnect();
      port = null;
    }
  }, IDLE_MS);
}

function toWallet(type, origin, userActivation, payload) {
  return new Promise((resolve) => {
    const p = connect();
    if (!p) {
      resolve(fallback(type));
      return;
    }
    const id = nextId++;
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve(fallback(type));
    }, TIMEOUT_MS[type] || 30000);
    pending.set(id, { type, resolve, timer });
    clearTimeout(idleTimer);
    try {
      p.postMessage({ id, type, origin, userActivation, payload });
    } catch (e) {
      clearTimeout(timer);
      pending.delete(id);
      resolve(fallback(type));
    }
  });
}

// The origin of the frame that sent the message, as Chrome reports it.
function originOf(sender) {
  let origin = sender.origin;
  if (!origin && sender.url) {
    try {
      origin = new URL(sender.url).origin;
    } catch (e) {
      return null;
    }
  }
  if (!origin || origin === "null") return null;
  let url;
  try {
    url = new URL(origin);
  } catch (e) {
    return null;
  }
  if (url.protocol === "https:") return origin;
  if (url.protocol === "http:" && (url.hostname === "localhost" || url.hostname === "127.0.0.1")) return origin;
  return null;
}

const isString = (v) => typeof v === "string";

const isCount = (v) => Number.isInteger(v) && v >= 0 && v <= 9007199254740991;

// The closed list of CONTRACT.md section 3.3 (wallet 0.36): feature names a site may find in grant.capabilities.
const CAPABILITIES = ["needs", "can", "kind", "cancel", "image.generate", "images-in", "parts", "frame-limit"];
const CAN_NAMES = ["input", "output", "context", "runs"];
// The page API has no cancel call (CONTRACT-DIRECT.md section 4: not in the browser binding in this release), so a feature the page cannot use
// is not offered to it: `cancel` is the wallet's, for the direct link, and is not passed on here.
const PAGE_CAPABILITIES = CAPABILITIES.filter((name) => name !== "cancel");
// Why a call failed (a closed list) and the ability names an agent lacked: the only extra words a call's answer may carry.
const REASONS = ["timeout", "cancelled", "limit", "failed"];
const ABILITY_NAME = /^[a-z][a-z0-9.-]{0,31}$/;

// The wallet's verdicts for the conditions (can) or abilities (abilities) the site asked about: booleans only, at most four
// keys, only names a site can ask about. Anything else is dropped before the page sees it.
function shapeVerdicts(value, allowed) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const out = {};
  let n = 0;
  for (const key of Object.keys(value)) {
    if (n >= 4) break;
    if (allowed && !allowed.includes(key)) continue;
    if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$/.test(key)) continue;
    if (typeof value[key] !== "boolean") continue;
    out[key] = value[key];
    n++;
  }
  return n ? out : undefined;
}

// grant.capabilities: the closed list, repeats and unknown names dropped; an empty array is kept (a wallet that understands none).
function shapeCapabilities(value) {
  if (!Array.isArray(value)) return undefined;
  const out = [];
  for (const name of value) {
    if (PAGE_CAPABILITIES.includes(name) && !out.includes(name)) out.push(name);
    if (out.length === PAGE_CAPABILITIES.length) break;
  }
  return out;
}

// The limits the site was given for text, in the manifest's names; anything else is dropped.
function shapeLimits(limits) {
  if (!limits || typeof limits !== "object") return undefined;
  const out = {};
  for (const key of ["maxTextChars", "maxAnswerTokens", "maxCallSeconds", "maxParallelCalls", "maxCallsPerMinute"]) {
    if (isCount(limits[key])) out[key] = limits[key];
  }
  return Object.keys(out).length ? out : undefined;
}

// The models the site was given, per skill: { ref, name, id (or null), runs, kind, can?, abilities? }; at most 8 skills,
// 64 a skill, short strings only.
function shapeModels(models) {
  if (!models || typeof models !== "object" || Array.isArray(models)) return undefined;
  const out = {};
  for (const skill of Object.keys(models).slice(0, 8)) {
    const list = models[skill];
    if (!/^[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$/.test(skill) || !Array.isArray(list)) continue;
    out[skill] = list.slice(0, 64)
      .filter((m) => m && isString(m.ref) && m.ref.length <= 16 && isString(m.name))
      .map((m) => {
        const kind = m.kind === "agent" ? "agent" : "model";
        const shaped = {
          ref: m.ref,
          name: m.name.slice(0, 120),
          id: isString(m.id) ? m.id.slice(0, 120) : null,
          runs: ["local", "home-server", "api-key"].includes(m.runs) ? m.runs : "api-key",
          kind,
        };
        const can = shapeVerdicts(m.can, CAN_NAMES);
        if (can) shaped.can = can;
        const abilities = kind === "agent" ? shapeVerdicts(m.abilities, null) : undefined;
        if (abilities) shaped.abilities = abilities;
        return shaped;
      });
  }
  return Object.keys(out).length ? out : undefined;
}

// Only the contract's shapes reach the page.
function shape(type, result) {
  if (type === "request") {
    if (!result || !isString(result.grantId) || !Array.isArray(result.skills) || !isString(result.expiresAt)) return null;
    const granted = { grantId: result.grantId, skills: result.skills.filter(isString), expiresAt: result.expiresAt };
    const limits = shapeLimits(result.limits);
    if (limits) granted.limits = limits;
    const capabilities = shapeCapabilities(result.capabilities);
    if (capabilities) granted.capabilities = capabilities;
    const models = shapeModels(result.models);
    if (models) granted.models = models;
    return granted;
  }
  if (type === "call") {
    if (!result || result.ok !== true || !result.output || typeof result.output !== "object") {
      const failed = { ok: false };
      if (result && result.reason !== undefined) failed.reason = REASONS.includes(result.reason) ? result.reason : "failed";
      return failed;
    }
    const receipt = result.receipt || {};
    const shaped = { cost: isString(receipt.cost) ? receipt.cost : "0.000 USD", provider: receipt.provider === "api-key" ? "api-key" : "local" };
    if (isString(receipt.model) && receipt.model.length <= 16) shaped.model = receipt.model;
    const answer = { ok: true, output: result.output, receipt: shaped };
    const lacks = Array.isArray(result.lacks) ? [...new Set(result.lacks.filter((a) => isString(a) && ABILITY_NAME.test(a)))].slice(0, 8) : [];
    if (lacks.length > 0) answer.lacks = lacks;
    return answer;
  }
  return null;
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !message || typeof message !== "object") return;

  if (message.kind === "page") {
    const type = message.type;
    const fb = fallback(type);
    if (!["request", "call", "release"].includes(type) || !sender.tab || sender.frameId !== 0) {
      sendResponse({ result: fb });
      return;
    }
    const origin = originOf(sender);
    if (!origin) {
      sendResponse({ result: fb });
      return;
    }
    const activation = type === "request" && message.userActivation === true;
    toWallet(type, origin, activation, message.payload && typeof message.payload === "object" ? message.payload : {})
      .then((result) => sendResponse({ result: shape(type, result) }));
    return true;
  }

  // The action popup: is the wallet running, and "open the wallet".
  if (message.kind === "popup" && isString(sender.url) && sender.url.startsWith(chrome.runtime.getURL(""))) {
    const type = message.type === "open" ? "open" : "status";
    toWallet(type, "", false, {}).then((result) => sendResponse({ result: result ?? fallback(type), error: lastError }));
    return true;
  }
});
