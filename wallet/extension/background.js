// BYOA agent wallet 0.1: the service worker. It talks to the native host org.bringyourownagent.wallet
// (byoa-host.exe), which relays to the desktop wallet. It adds the page's origin from Chrome's sender
// information (never from the page), keeps only top frames of https pages and http://localhost pages,
// enforces time limits, and passes the wallet's answers back in the contract's shapes only.
"use strict";

const HOST = "org.bringyourownagent.wallet";
const TIMEOUT_MS = { request: 125000, call: 90000, release: 10000, status: 4000, open: 30000 };
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

// Only the contract's shapes reach the page.
function shape(type, result) {
  if (type === "request") {
    if (!result || !isString(result.grantId) || !Array.isArray(result.skills) || !isString(result.expiresAt)) return null;
    return { grantId: result.grantId, skills: result.skills.filter(isString), expiresAt: result.expiresAt };
  }
  if (type === "call") {
    if (!result || result.ok !== true || !result.output || typeof result.output !== "object") return { ok: false };
    const receipt = result.receipt || {};
    return {
      ok: true,
      output: result.output,
      receipt: { cost: isString(receipt.cost) ? receipt.cost : "0.000 USD", provider: receipt.provider === "api-key" ? "api-key" : "local" },
    };
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
