// BYOA agent wallet 0.1: window.byoa, injected into the page's MAIN world at document_start.
// It only carries messages to the extension's isolated world (bridge.js). It decides nothing: the click rule,
// the origin and every limit are enforced by the extension and the desktop wallet, never by this script.
(() => {
  "use strict";
  if (Object.prototype.hasOwnProperty.call(window, "byoa")) return;

  const CHANNEL = "byoa-0.1";
  // Taken at document_start, before the page's own scripts can replace them.
  const post = window.postMessage.bind(window);
  const listen = window.addEventListener.bind(window);
  const target = window.location.origin;
  const waiting = new Map();
  let next = 1;

  listen("message", (event) => {
    if (event.source !== window) return;
    const d = event.data;
    if (!d || typeof d !== "object" || d.channel !== CHANNEL || d.dir !== "to-page") return;
    const resolve = waiting.get(d.id);
    if (!resolve) return;
    waiting.delete(d.id);
    resolve(d.result);
  });

  // Plain JSON only: anything else (functions, DOM nodes, cycles) is refused here.
  function plain(value) {
    return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
  }

  function send(type, payload, fallback) {
    return new Promise((resolve) => {
      const id = next++;
      waiting.set(id, resolve);
      try {
        post({ channel: CHANNEL, dir: "to-wallet", id, type, payload }, target);
      } catch (e) {
        waiting.delete(id);
        resolve(fallback);
      }
    });
  }

  const api = {
    version: "0.1",

    // Ask for permission; call it from a click handler. Resolves with { grantId, skills, expiresAt } or null.
    request(manifest) {
      let m;
      try {
        m = plain(manifest);
      } catch (e) {
        return Promise.resolve(null);
      }
      if (!m || typeof m !== "object" || Array.isArray(m)) return Promise.resolve(null);
      return send("request", { manifest: m }, null);
    },

    // One use of a granted skill. Resolves with { ok: true, output, receipt } or { ok: false }.
    call(grantId, skill, input, params) {
      if (typeof grantId !== "string" || typeof skill !== "string") return Promise.resolve({ ok: false });
      let payload;
      try {
        payload = { grantId, skill, input: plain(input) ?? {}, params: plain(params) ?? {} };
      } catch (e) {
        return Promise.resolve({ ok: false });
      }
      return send("call", payload, { ok: false });
    },

    release(grantId) {
      if (typeof grantId !== "string") return Promise.resolve();
      return send("release", { grantId }, undefined).then(() => undefined);
    },
  };

  Object.freeze(api);
  Object.defineProperty(window, "byoa", { value: api, writable: false, configurable: false, enumerable: false });
  window.dispatchEvent(new Event("byoa:ready"));
})();
