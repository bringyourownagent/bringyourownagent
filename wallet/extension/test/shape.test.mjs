// The extension's shaping of a grant: what reaches window.byoa and what is cut. There is no browser harness for the
// extension, so background.js is loaded in a bare VM with a stub for chrome; it needs nothing else at load time.
// Run: node --test wallet/extension/test

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../background.js", import.meta.url), "utf8");
const sandbox = { chrome: { runtime: { onMessage: { addListener() {} } } } };
vm.createContext(sandbox);
vm.runInContext(source + "\nglobalThis.__shaping = { shape, shapeModels, shapeCapabilities, shapeVerdicts };", sandbox);
const { shape } = sandbox.__shaping;

// Values built in the VM have that realm's prototypes; bring them into this one before comparing.
const shaped = (...args) => JSON.parse(JSON.stringify(shape(...args)));

/** A grant answer as the wallet host hands it to the extension. */
const grant = (extra = {}) => ({
  grantId: "g1",
  skills: ["text.generate", "image.generate"],
  expiresAt: "2026-10-31T20:00:00Z",
  ...extra,
});

test("capabilities: the closed list reaches the page, unknown and repeated names do not", () => {
  const answer = shaped("request", grant({ capabilities: ["needs", "can", "bogus", "needs", "image.generate", "kind"] }));
  assert.deepEqual(answer.capabilities, ["needs", "can", "image.generate", "kind"]);
});

test("capabilities: `cancel` does not reach the page, which has no way to cancel", () => {
  assert.deepEqual(shaped("request", grant({ capabilities: ["needs", "cancel", "can"] })).capabilities, ["needs", "can"]);
});

test("a failed call keeps its reason from the closed list, any other word becomes failed, and no reason stays none", () => {
  assert.deepEqual(shaped("call", { ok: false, reason: "limit" }), { ok: false, reason: "limit" });
  assert.deepEqual(shaped("call", { ok: false, reason: "the model is not installed" }), { ok: false, reason: "failed" });
  assert.deepEqual(shaped("call", { ok: false }), { ok: false });
  assert.deepEqual(shaped("call", { ok: true, output: null, reason: "timeout" }), { ok: false, reason: "timeout" });
  assert.deepEqual(shaped("call", null), { ok: false });
});

test("a call that worked carries what the agent lacked as ability names only, once each", () => {
  const answer = shaped("call", { ok: true, output: { text: "hi" }, receipt: { cost: "0.000 USD", provider: "local" }, lacks: ["web.read", "web.read", "Not An Ability", 7, "image.read"] });
  assert.deepEqual(answer.lacks, ["web.read", "image.read"]);
  assert.equal("lacks" in shaped("call", { ok: true, output: { text: "hi" }, lacks: [] }), false);
});

test("capabilities: an empty list is passed on (the wallet understands none), a missing one stays missing", () => {
  assert.deepEqual(shaped("request", grant({ capabilities: [] })).capabilities, []);
  assert.equal("capabilities" in shaped("request", grant()), false);                     // an older wallet says nothing about what it understands
  assert.equal(shaped("request", grant({ capabilities: "needs" })).capabilities, undefined);
});

test("can: booleans only, the four properties only, at most four keys, on every skill", () => {
  const answer = shaped("request", grant({
    models: {
      "text.generate": [
        { ref: "m1", name: "Model A", id: null, runs: "api-key", kind: "model",
          can: { input: true, output: false, context: "yes", runs: true, surprise: true } },
      ],
      "image.generate": [
        { ref: "i1", name: "Drawer", id: "google/gemini", runs: "api-key", kind: "model", can: { output: true } },
      ],
    },
  }));
  assert.deepEqual(answer.models["text.generate"][0].can, { input: true, output: false, runs: true });
  assert.deepEqual(answer.models["image.generate"][0].can, { output: true });
});

test("can and abilities: earlier keys win when more than four are offered", () => {
  const answer = shaped("request", grant({
    models: {
      "text.generate": [
        { ref: "m1", name: "Agent", id: "agent/one", runs: "subscription", kind: "agent",
          can: { input: true, output: true, context: true, runs: true, extra: true },
          abilities: { "web.search": true, "web.read": false, "x-a": true, "x-b": true, "x-c": true, "x-d": true } },
      ],
    },
  }));
  const entry = answer.models["text.generate"][0];
  assert.deepEqual(entry.can, { input: true, output: true, context: true, runs: true });
  assert.deepEqual(entry.abilities, { "web.search": true, "web.read": false, "x-a": true, "x-b": true });
});

test("abilities belong to an agent entry; a model's are dropped, a non-boolean or badly named verdict too", () => {
  const answer = shaped("request", grant({
    models: {
      "text.generate": [
        { ref: "m1", name: "Model", id: null, runs: "local", kind: "model", abilities: { "web.search": true } },
        { ref: "m2", name: "Agent", id: "agent/two", runs: "subscription", kind: "agent",
          abilities: { "web.search": true, bad: 1, "x ok": true, "": true } },
      ],
    },
  }));
  assert.equal("abilities" in answer.models["text.generate"][0], false);
  assert.deepEqual(answer.models["text.generate"][1].abilities, { "web.search": true });
});

test("every skill's models are passed on, under the contract's own skill ids", () => {
  const answer = shaped("request", grant({
    models: {
      "text.generate": [{ ref: "m1", name: "Text", id: null, runs: "local" }],
      "image.generate": [{ ref: "i1", name: "Picture", id: null, runs: "api-key" }],
      "Not-A-Skill": [{ ref: "x", name: "no", id: null, runs: "local" }],
      "audio.transcribe": "not a list",
    },
  }));
  assert.deepEqual(Object.keys(answer.models), ["text.generate", "image.generate"]);
  assert.deepEqual(answer.models["image.generate"].map((m) => m.ref), ["i1"]);
});

test("an older wallet's grant: no capabilities, no can, no abilities, and the models as before", () => {
  const answer = shaped("request", grant({ models: { "text.generate": [{ ref: "m1", name: "Old", id: null, runs: "local" }] } }));
  assert.equal("capabilities" in answer, false);
  assert.deepEqual(answer.models["text.generate"], [{ ref: "m1", name: "Old", id: null, runs: "local", kind: "model" }]);
});

test("a refusal keeps its shape and carries nothing new", () => {
  assert.deepEqual(shaped("call", { ok: false }), { ok: false });
  assert.equal(shaped("request", null), null);
});