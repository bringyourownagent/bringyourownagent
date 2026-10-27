// The extension's only window: is the wallet running, the versions, and a link that opens the wallet.
"use strict";
const chip = document.getElementById("chip");
const note = document.getElementById("note");
const versions = document.getElementById("versions");
const extensionVersion = chrome.runtime.getManifest().version;

function ask(type) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage({ kind: "popup", type }, (response) => {
      resolve(chrome.runtime.lastError || !response ? { result: null } : response);
    });
  });
}

async function refresh() {
  const { result } = await ask("status");
  const running = !!(result && result.running);
  chip.textContent = running ? "Wallet connected" : "Wallet not running";
  chip.className = running ? "chip" : "chip off";
  if (!running && result && result.installed === false) {
    chip.textContent = "Wallet not installed";
    note.textContent = "Install the BYOA agent wallet on this computer, then reload this extension.";
  } else if (!running) {
    note.textContent = "It starts when a page asks for your AI, or with the button below.";
  } else {
    note.textContent = result.quiet > 0 ? result.quiet + " request(s) without a click were held back." : "";
  }
  versions.textContent = "Extension " + extensionVersion + (running && result.version ? " · wallet " + result.version : "");
}

document.getElementById("open").addEventListener("click", async () => {
  chip.textContent = "Opening…";
  await ask("open");
  refresh();
});

refresh();
