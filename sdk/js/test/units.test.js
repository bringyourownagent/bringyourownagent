// JavaScript-specific unit tests: money, timestamps, the purpose judge hook, the API.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import * as api from '../src/index.js';
import { applyOpinion, checkCall, noOpinion, validateManifest } from '../src/index.js';
import { addMoney, formatMoney, parseMoney } from '../src/money.js';
import { parseTimestamp } from '../src/time.js';

const DEFAULTS = JSON.parse(readFileSync(new URL('../../test-vectors/check-call.json', import.meta.url), 'utf8'))
  .defaults;

function docs() {
  const copy = JSON.parse(JSON.stringify(DEFAULTS));
  return [copy.manifest, copy.grant, copy.call, copy.state];
}

describe('money', () => {
  test('parses into integer micro-units', () => {
    assert.deepEqual(parseMoney('0.06 USD'), { micros: 60000n, currency: 'USD' });
    assert.deepEqual(parseMoney('0 EUR'), { micros: 0n, currency: 'EUR' });
    assert.equal(parseMoney('0.06 USD\n'), null);
    assert.equal(parseMoney(0.06), null);
    assert.equal(parseMoney('\u0661 USD'), null);
  });

  test('sums are exact (no floating point)', () => {
    assert.equal(0.1 + 0.2 === 0.3, false); // the reason for all this
    const total = addMoney(parseMoney('0.1 USD'), parseMoney('0.2 USD'));
    assert.equal(formatMoney(total), '0.3 USD');
    const biggest = parseMoney('999999999999.999999 USD');
    assert.equal(formatMoney(addMoney(biggest, biggest)), '1999999999999.999998 USD');
  });

  test('currencies never mix', () => {
    assert.throws(() => addMoney(parseMoney('1 USD'), parseMoney('1 EUR')), RangeError);
  });

  test('formats in the shortest form', () => {
    assert.equal(formatMoney(parseMoney('0.060 USD')), '0.06 USD');
    assert.equal(formatMoney(parseMoney('12.000 EUR')), '12 EUR');
    assert.equal(formatMoney(parseMoney('0 USD')), '0 USD');
  });
});

describe('timestamps', () => {
  test('epoch and offsets', () => {
    assert.equal(parseTimestamp('1970-01-01T00:00:00Z'), 0);
    assert.equal(parseTimestamp('1970-01-01T01:00:00+01:00'), 0);
    assert.equal(parseTimestamp('1969-12-31T23:59:59.999Z'), -1);
    assert.equal(parseTimestamp('2026-10-10t12:00:00z'), parseTimestamp('2026-10-10T12:00:00Z'));
  });

  test('agrees with Date for ordinary instants', () => {
    for (const s of ['2026-10-10T12:00:00Z', '2000-02-29T23:59:59.5+05:30', '0001-01-01T00:00:00Z']) {
      assert.equal(parseTimestamp(s), Date.parse(s), s);
    }
    assert.equal(parseTimestamp('2026-10-10T12:00:00Z'), 1791633600000);
  });

  test('rejects what RFC 3339 does not allow', () => {
    for (const bad of [
      '2026-10-10T12:00:00',
      '2026-10-10 12:00:00Z',
      '2026-13-01T00:00:00Z',
      '2026-02-29T00:00:00Z',
      '2026-10-10T24:00:00Z',
      '2026-10-10T23:59:60Z',
      '2026-10-10T12:00:00+24:00',
      '2026-10-10T12:00:00.Z',
      null,
      1791633600000,
    ]) {
      assert.equal(parseTimestamp(bad), null, String(bad));
    }
  });

  test('cuts extra fraction digits', () => {
    assert.equal(parseTimestamp('1970-01-01T00:00:00.123999999Z'), 123);
  });
});

describe('purpose judge hook', () => {
  test('the default judge has no opinion', () => {
    assert.equal(noOpinion({}, {}, {}), null);
    assert.deepEqual(checkCall(...docs(), { judge: noOpinion }), checkCall(...docs()));
  });

  test('a judge can make the decision stricter', () => {
    const result = checkCall(...docs(), { judge: () => ({ decision: 'ask', message: 'this does not look like a game' }) });
    assert.equal(result.decision, 'ask');
    assert.equal(result.reasons.at(-1).code, 'purpose-doubt');
  });

  test('a judge cannot relax a decision', () => {
    const [manifest, grant, call, state] = docs();
    state.spent = '0.199 USD';
    assert.equal(checkCall(manifest, grant, call, state, { judge: () => ({ decision: 'allowed' }) }).decision, 'ask');
    const result = checkCall(manifest, grant, call, state, { judge: () => ({ decision: 'warn', code: 'odd' }) });
    assert.equal(result.decision, 'ask');
    assert.deepEqual(result.reasons.map((r) => r.code), ['budget-exceeded', 'odd']);
  });

  test('the judge is not consulted when the call is blocked', () => {
    const [manifest, grant, call, state] = docs();
    call.input.clipSeconds = 42;
    let consulted = false;
    checkCall(manifest, grant, call, state, {
      judge: () => {
        consulted = true;
        return null;
      },
    });
    assert.equal(consulted, false);
  });

  test('a failing judge means ask', () => {
    const result = checkCall(...docs(), {
      judge: () => {
        throw new Error('model offline');
      },
    });
    assert.equal(result.decision, 'ask');
    assert.equal(result.reasons.at(-1).code, 'purpose-judge-error');
  });

  test('an invalid opinion means ask', () => {
    for (const bad of [{ decision: 'maybe' }, 'blocked', { decision: 'warn', code: '' }, { decision: 'warn', message: 3 }]) {
      const result = checkCall(...docs(), { judge: () => bad });
      assert.equal(result.decision, 'ask', JSON.stringify(bad));
      assert.equal(result.reasons.at(-1).code, 'purpose-judge-error');
    }
  });

  test('an async judge is refused inside checkCall, but applyOpinion works', async () => {
    const asyncJudge = async () => ({ decision: 'warn', code: 'purpose-unclear' });
    const refused = checkCall(...docs(), { judge: asyncJudge });
    assert.equal(refused.reasons.at(-1).code, 'purpose-judge-error');

    const base = checkCall(...docs());
    const merged = applyOpinion(base, await asyncJudge(...docs()));
    assert.equal(merged.decision, 'warn');
    assert.equal(merged.reasons.at(-1).code, 'purpose-unclear');
    assert.deepEqual(base.reasons, []); // applyOpinion does not mutate
  });
});

describe('API', () => {
  test('never throws on odd input', () => {
    for (const odd of [null, undefined, 1, 'x', [], {}, { skills: { 1: 2 } }, { purpose: { summary: null } }]) {
      assert.equal(validateManifest(odd).ok, false);
      assert.equal(checkCall(odd, odd, odd, odd).decision, 'blocked');
    }
  });

  test('prototype keys are not mistaken for fields', () => {
    const manifest = docs()[0];
    manifest.skills = JSON.parse('{"__proto__": {"required": true}, "audio.transcribe": {"required": true}}');
    const result = validateManifest(manifest);
    assert.deepEqual(result.errors.map((e) => e.path), ['/skills/__proto__']);
    assert.equal(validateManifest({ ...docs()[0], toString: null }).warnings.length, 0);
  });

  test('inputs are not mutated', () => {
    const inputs = docs();
    const before = JSON.stringify(inputs);
    checkCall(...inputs);
    validateManifest(inputs[0]);
    assert.equal(JSON.stringify(inputs), before);
  });

  test('VERSION matches package.json', () => {
    const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
    assert.equal(api.VERSION, pkg.version);
  });

  test('index.d.ts declares every runtime export', () => {
    const dts = readFileSync(new URL('../index.d.ts', import.meta.url), 'utf8');
    for (const name of Object.keys(api)) {
      assert.match(dts, new RegExp(`export (const|function) ${name}\\b`), name);
    }
  });

  test('constant lists are frozen', () => {
    assert.ok(Object.isFrozen(api.DECISIONS));
    assert.deepEqual([...api.DECISIONS], ['allowed', 'warn', 'ask', 'blocked']);
  });
});
