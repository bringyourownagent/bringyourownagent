// BYOA agent wallet 0.1: the service worker. It talks to the native host org.bringyourownagent.wallet
// (byoa-host.exe), which relays to the desktop wallet. It adds the page's origin from Chrome's sender
// information (never from the page), keeps only top frames of https pages and http://localhost pages,
// enforces time limits, and passes the wallet's answers back in the contract's shapes only.
"use strict";

const HOST = "org.bringyourownagent.wallet";
// A call may wait in the wallet for its own limit (up to ten minutes: a model that is still starting, a long answer), so the extension waits a little longer.
const TIMEOUT_MS = { request: 605000, call: 610000, release: 10000, status: 4000, open: 30000 };
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
    if (message.type === "image") {
      // a part of a picture: the call's result comes after all its parts
      if (waiting.pictures) waiting.pictures.add(message);
      return;
    }
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

function toWallet(type, origin, userActivation, payload, pictures) {
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
    pending.set(id, { type, resolve, timer, pictures });
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
const CAN_NAMES = ["input", "output"];
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

// A picture call's answer (image.generate, wallet 0.37) comes in parts, because Chrome takes at most 1 MB in one message from the
// native host: the wallet sends each picture as { id, type: "image", index, seq, of, mime, data } messages, then the call's result,
// whose output.images says what each picture is (CONTRACT-DIRECT.md section 4). The parts are joined in seq order and checked
// against the result: size, SHA-256, the type the bytes really are, at most 4 pictures and 8,000,000 bytes together.
const PICTURE_TYPES = ["image/png", "image/jpeg", "image/webp", "image/gif"];
const MAX_PICTURES = 4;
const MAX_PART_CHARS = 524288;
const MAX_PARTS = 32;
const MAX_PICTURE_BYTES = 8000000;
const MAX_PICTURE_CHARS = Math.ceil(MAX_PICTURE_BYTES / 3) * 4;

// The type the bytes are, by their first bytes (never by a field); null for anything else.
function sniffPicture(b) {
  const at = (i, ...v) => v.every((x, k) => b[i + k] === x);
  if (b.length >= 8 && at(0, 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)) return "image/png";
  if (b.length >= 3 && at(0, 0xff, 0xd8, 0xff)) return "image/jpeg";
  if (b.length >= 6 && at(0, 0x47, 0x49, 0x46, 0x38) && (b[4] === 0x37 || b[4] === 0x39) && b[5] === 0x61) return "image/gif";
  if (b.length >= 12 && at(0, 0x52, 0x49, 0x46, 0x46) && at(8, 0x57, 0x45, 0x42, 0x50)) return "image/webp";
  return null;
}

class PictureParts {
  constructor() {
    this.pictures = new Map();
    this.chars = 0;
    this.broken = false;
  }

  // One part. A part out of shape, a repeat, one that disagrees with its picture's other parts, or one past the budget spoils the answer.
  add(m) {
    if (this.broken) return;
    const fits = Number.isInteger(m.index) && m.index >= 0 && m.index < MAX_PICTURES && Number.isInteger(m.of) && m.of >= 1 && m.of <= MAX_PARTS
      && Number.isInteger(m.seq) && m.seq >= 0 && m.seq < m.of && PICTURE_TYPES.includes(m.mime) && isString(m.data) && m.data.length > 0
      && m.data.length <= MAX_PART_CHARS && (this.chars += m.data.length) <= MAX_PICTURE_CHARS;
    if (!fits) { this.broken = true; return; }
    let picture = this.pictures.get(m.index);
    if (!picture) this.pictures.set(m.index, (picture = { mime: m.mime, parts: new Array(m.of).fill(null) }));
    if (picture.mime !== m.mime || picture.parts.length !== m.of || picture.parts[m.seq] !== null) { this.broken = true; return; }
    picture.parts[m.seq] = m.data;
  }

  // The pictures the result describes, joined and checked: [{ mime, size, sha256, data, reduced }], or null.
  async join(described) {
    if (this.broken || !Array.isArray(described) || described.length === 0 || described.length > MAX_PICTURES
      || described.length !== this.pictures.size) return null;
    const out = [];
    let total = 0;
    for (let i = 0; i < described.length; i++) {
      const info = described[i];
      const picture = this.pictures.get(i);
      if (!info || typeof info !== "object" || !picture || picture.parts.includes(null) || info.mime !== picture.mime) return null;
      const data = picture.parts.join("");
      let bytes;
      try {
        bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
      } catch (e) {
        return null;
      }
      total += bytes.length;
      if (total > MAX_PICTURE_BYTES || info.size !== bytes.length || sniffPicture(bytes) !== picture.mime) return null;
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
      const sha256 = Array.from(digest, (x) => x.toString(16).padStart(2, "0")).join("");
      if (!isString(info.sha256) || info.sha256.toLowerCase() !== sha256) return null;
      out.push({ mime: picture.mime, size: bytes.length, sha256, data, reduced: info.reduced === true });
    }
    return out;
  }
}

// A picture call's answer for the page: the joined pictures in output.images, or a failed call when they do not add up.
async function shapePictures(result, pictures) {
  if (!result || result.ok !== true) return shape("call", result);
  const images = result.output && typeof result.output === "object" ? await pictures.join(result.output.images) : null;
  return images ? shape("call", { ...result, output: { images } }) : { ok: false, reason: "failed" };
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
    const payload = message.payload && typeof message.payload === "object" ? message.payload : {};
    const pictures = type === "call" && payload.skill === "image.generate" ? new PictureParts() : undefined;
    toWallet(type, origin, activation, payload, pictures)
      .then((result) => (pictures ? shapePictures(result, pictures) : shape(type, result)))
      .then((result) => sendResponse({ result }), () => sendResponse({ result: fb }));
    return true;
  }

  // The action popup: is the wallet running, and "open the wallet".
  if (message.kind === "popup" && isString(sender.url) && sender.url.startsWith(chrome.runtime.getURL(""))) {
    const type = message.type === "open" ? "open" : "status";
    toWallet(type, "", false, {}).then((result) => sendResponse({ result: result ?? fallback(type), error: lastError }));
    return true;
  }
});
