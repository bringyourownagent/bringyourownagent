// The shared test vectors in sdk/test-vectors: the contract with other implementations.
// One test is generated per vector case.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import { checkCall, DECISIONS, validateManifest } from '../src/index.js';

const VECTORS = new URL('../../test-vectors/', import.meta.url);

function loadVectors(name) {
  return JSON.parse(readFileSync(new URL(name, VECTORS), 'utf8'));
}

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
// structuredClone keeps Infinity (from 1e400); a JSON round trip would turn it into null.
const clone = (v) => (v === undefined ? undefined : structuredClone(v));

/** JSON Merge Patch, RFC 7396. */
function mergePatch(target, patch) {
  if (!isObject(patch)) return clone(patch);
  const result = isObject(target) ? clone(target) : {};
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete result[key];
    else result[key] = mergePatch(result[key], value);
  }
  return result;
}

function caseDocument(testCase, defaults, name) {
  if (Object.prototype.hasOwnProperty.call(testCase, name)) return clone(testCase[name]);
  if (Object.prototype.hasOwnProperty.call(testCase, name + 'Patch')) {
    return mergePatch(defaults[name], testCase[name + 'Patch']);
  }
  return clone(defaults[name]);
}

describe('validate-manifest.json', () => {
  const data = loadVectors('validate-manifest.json');
  assert.ok(data.cases.length > 0);
  for (const testCase of data.cases) {
    test(testCase.name, () => {
      const result = validateManifest(caseDocument(testCase, data.defaults, 'manifest'));
      const project = (issue) => ({ code: issue.code, path: issue.path });
      assert.deepEqual(
        { ok: result.ok, errors: result.errors.map(project), warnings: result.warnings.map(project) },
        testCase.expect,
      );
      for (const issue of [...result.errors, ...result.warnings]) {
        assert.equal(typeof issue.message, 'string');
        assert.ok(issue.message.length > 0);
      }
    });
  }
});

/** Build the judge a purpose-judge.json case asks for. */
function makeJudge(spec) {
  if (spec === undefined || spec === null) return null;
  if ('returns' in spec) return () => clone(spec.returns);
  if ('throws' in spec) {
    return () => {
      throw new Error(spec.throws);
    };
  }
  if ('notCallable' in spec) return spec.notCallable;
  throw new Error(`unknown judge spec ${JSON.stringify(spec)}`);
}

describe('purpose-judge.json', () => {
  const data = loadVectors('purpose-judge.json');
  assert.ok(data.cases.length > 0);
  for (const testCase of data.cases) {
    test(testCase.name, () => {
      const [manifest, grant, call, state] = ['manifest', 'grant', 'call', 'state'].map((name) =>
        caseDocument(testCase, data.defaults, name),
      );
      const result = checkCall(manifest, grant, call, state, { judge: makeJudge(testCase.judge) });
      const project = (r) => ('path' in r ? { code: r.code, path: r.path } : { code: r.code });
      assert.deepEqual({ decision: result.decision, reasons: result.reasons.map(project) }, testCase.expect);
    });
  }
});

describe('check-call.json', () => {
  const data = loadVectors('check-call.json');
  assert.ok(data.cases.length > 0);
  for (const testCase of data.cases) {
    test(testCase.name, () => {
      const [manifest, grant, call, state] = ['manifest', 'grant', 'call', 'state'].map((name) =>
        caseDocument(testCase, data.defaults, name),
      );
      const result = checkCall(manifest, grant, call, state);
      const project = (r) => ('path' in r ? { code: r.code, path: r.path } : { code: r.code });
      assert.deepEqual({ decision: result.decision, reasons: result.reasons.map(project) }, testCase.expect);
      for (const reason of result.reasons) {
        assert.ok(DECISIONS.includes(reason.decision));
        assert.ok(reason.message.length > 0);
      }
    });
  }
});
