// BYOA agent wallet 0.1: the bridge in the extension's isolated world. It receives the page's messages from
// page.js and passes them to the service worker. The click rule is judged HERE, from the browser's own
// navigator.userActivation, never from anything the page sends. The origin is added by the service worker
// from Chrome's sender information.
(() => {
  "use strict";
  const CHANNEL = "byoa-0.1";
  const TYPES = ["request", "call", "release"];
  const target = window.location.origin;

  window.addEventListener("message", (event) => {
    if (event.source !== window) return;
    const d = event.data;
    if (!d || typeof d !== "object" || d.channel !== CHANNEL || d.dir !== "to-wallet") return;
    if (!TYPES.includes(d.type) || typeof d.id !== "number") return;

    // Transient user activation: the page was clicked (or a key pressed) within the last few seconds.
    const userActivation = d.type === "request" && !!(navigator.userActivation && navigator.userActivation.isActive);
    const fallback = d.type === "call" ? { ok: false } : null;
    let answered = false;
    const answer = (result) => {
      if (answered) return;
      answered = true;
      window.postMessage({ channel: CHANNEL, dir: "to-page", id: d.id, result }, target);
    };

    try {
      chrome.runtime.sendMessage({ kind: "page", type: d.type, userActivation, payload: d.payload }, (response) => {
        if (chrome.runtime.lastError || !response || typeof response !== "object") {
          answer(fallback);
          return;
        }
        answer("result" in response ? response.result : fallback);
      });
    } catch (e) {
      // The extension was reloaded or removed: the page hears the same as with no wallet.
      answer(fallback);
    }
  });
})();
