// A picture call through the extension (image.generate, wallet 0.37): the native host may send at most 1 MB in one message, so the
// wallet sends each picture as { id, type: "image", ... } parts before the call's result; the service worker joins and checks them,
// and the page gets the pictures whole, or a failed call. background.js runs in a bare VM with stubs for chrome and its native port.
// Run: node --test wallet/extension/test

import assert from "node:assert/strict";
import { createHash, webcrypto } from "node:crypto";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../background.js", import.meta.url), "utf8");

/** The service worker with a fake native host: `sent` holds what it posted, `reply` answers as the host would. */
function load() {
  const listeners = { page: null, native: null };
  const sent = [];
  const port = {
    onMessage: { addListener(f) { listeners.native = f; } },
    onDisconnect: { addListener() {} },
    postMessage(message) { sent.push(JSON.parse(JSON.stringify(message))); },
    disconnect() {},
  };
  const chrome = { runtime: { id: "ext", onMessage: { addListener(f) { listeners.page = f; } }, connectNative: () => port, getURL: () => "chrome-extension://ext/" } };
  // the worker's own timers (a call may wait ten minutes, an idle port two) must not keep the test run alive
  const unref = (f, ms) => { const timer = setTimeout(f, ms); timer.unref(); return timer; };
  const sandbox = { chrome, atob, crypto: webcrypto, URL, setTimeout: unref, clearTimeout, console };
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);
  /** A page's call; resolves with what the page would get. */
  const call = (skill, input = {}) => new Promise((resolve) => {
    listeners.page({ kind: "page", type: "call", payload: { grantId: "g1", skill, input, params: { model: "m3" } } },
      { id: "ext", tab: { id: 1 }, frameId: 0, origin: "https://icons.example" },
      (response) => resolve(JSON.parse(JSON.stringify(response.result))));
  });
  return { sent, call, host: (message) => listeners.native(message) };
}

function png(size, seed = 1) {
  const bytes = Buffer.alloc(size);
  let x = seed;
  for (let i = 0; i < size; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; bytes[i] = x & 0xff; }
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(bytes, 0);
  return bytes;
}

const sha = (bytes) => createHash("sha256").update(bytes).digest("hex");

function parts(id, index, bytes, partChars = 524288, mime = "image/png") {
  const data = bytes.toString("base64");
  const pieces = [];
  for (let i = 0; i < data.length; i += partChars) pieces.push(data.slice(i, i + partChars));
  return pieces.map((piece, seq) => ({ id, type: "image", index, seq, of: pieces.length, mime, data: piece }));
}

const result = (id, images) => ({ id, result: { ok: true, output: { images }, receipt: { cost: "0.068 USD", provider: "api-key", model: "m3" } } });

test("the parts of a picture reach the page as one picture, every message from the host under 1 MB", async () => {
  const { sent, call, host } = load();
  const answer = call("image.generate", { prompt: "A fox" });
  const id = sent[0].id;
  assert.equal(sent[0].payload.skill, "image.generate");
  const picture = png(1_500_000);
  const messages = parts(id, 0, picture);
  assert.ok(messages.every((m) => JSON.stringify(m).length < 1024 * 1024));
  for (const m of messages) host(m);
  host(result(id, [{ mime: "image/png", size: picture.length, sha256: sha(picture), reduced: true }]));
  const page = await answer;
  assert.equal(page.ok, true);
  assert.deepEqual(page.receipt, { cost: "0.068 USD", provider: "api-key", model: "m3" });
  assert.equal(page.output.images.length, 1);
  const [image] = page.output.images;
  assert.equal(image.mime, "image/png");
  assert.equal(image.size, picture.length);
  assert.equal(image.sha256, sha(picture));
  assert.equal(image.reduced, true);
  assert.ok(Buffer.from(image.data, "base64").equals(picture));
});

for (const fault of ["missing part", "wrong sha256", "wrong size", "not a picture", "a picture more than described", "a part twice", "no pictures at all"]) {
  test(`a picture answer that does not add up is a failed call: ${fault}`, async () => {
    const { sent, call, host } = load();
    const answer = call("image.generate", { prompt: "A fox" });
    const id = sent[0].id;
    const picture = fault === "not a picture" ? Buffer.from("plain text, no picture") : png(30_000);
    const messages = parts(id, 0, picture, 10_000);
    let described = [{ mime: "image/png", size: picture.length, sha256: sha(picture) }];
    if (fault === "missing part") messages.splice(1, 1);
    if (fault === "wrong sha256") described = [{ ...described[0], sha256: sha(png(10)) }];
    if (fault === "wrong size") described = [{ ...described[0], size: picture.length + 1 }];
    if (fault === "a picture more than described") messages.push(...parts(id, 1, png(300, 2)));
    if (fault === "a part twice") messages.push(messages[0]);
    if (fault === "no pictures at all") described = [];
    for (const m of messages) host(m);
    host(result(id, described));
    assert.deepEqual(await answer, { ok: false, reason: "failed" });
  });
}

test("a text call ignores picture parts, and a refused picture call keeps the wallet's reason", async () => {
  const { sent, call, host } = load();
  const text = call("text.generate", { messages: [{ role: "user", content: "Hi?" }] });
  const textId = sent[0].id;
  for (const m of parts(textId, 0, png(400))) host(m);
  host({ id: textId, result: { ok: true, output: { text: "Hi." }, receipt: { cost: "0.000 USD", provider: "local" } } });
  assert.deepEqual((await text).output, { text: "Hi." });

  const refused = call("image.generate", { prompt: "A fox" });
  const id = sent[1].id;
  host(parts(id, 0, png(400))[0]);
  host({ id, result: { ok: false, reason: "limit" } });
  assert.deepEqual(await refused, { ok: false, reason: "limit" });
});
