// The bringyourownagent command line tool, run in-process and once as a real process.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { describe, test } from 'node:test';
import { main } from '../src/cli.js';

const example = (name) => fileURLToPath(new URL(`../../examples/${name}`, import.meta.url));
const vectorsReadme = fileURLToPath(new URL('../../test-vectors/README.md', import.meta.url));
const bin = fileURLToPath(new URL('../bin/bringyourownagent.js', import.meta.url));

function run(...args) {
  let out = '';
  let err = '';
  const code = main(args, { stdout: { write: (s) => (out += s) }, stderr: { write: (s) => (err += s) } });
  return { code, out, err };
}

describe('bringyourownagent validate', () => {
  test('a valid manifest', () => {
    const { code, out, err } = run('validate', example('manifest.json'));
    assert.equal(code, 0);
    assert.ok(out.endsWith('manifest.json: valid\n'), out);
    assert.equal(err, '');
  });

  test('an invalid manifest lists its problems', () => {
    const { code, out } = run('validate', example('manifest-invalid.json'));
    assert.equal(code, 1);
    assert.ok(out.includes('invalid, 4 errors, 2 warnings'), out);
    assert.ok(out.includes('  error privacy-unknown at /privacy: '), out);
    assert.ok(out.includes('  warning limit-unknown at /purpose/limits/maxTokens: '), out);
  });

  test('JSON output', () => {
    const { code, out } = run('validate', '--json', example('manifest-invalid.json'));
    assert.equal(code, 1);
    const result = JSON.parse(out);
    assert.equal(result.ok, false);
    assert.deepEqual(
      result.errors.map((e) => e.code),
      ['limit-invalid', 'skill-required-invalid', 'privacy-unknown', 'cost-invalid'],
    );
  });

  test('a missing file', () => {
    const { code, out, err } = run('validate', example('no-such-file.json'));
    assert.equal(code, 2);
    assert.equal(out, '');
    assert.ok(err.includes('cannot read'), err);
  });

  test('a file that is not JSON', () => {
    const { code, err } = run('validate', vectorsReadme);
    assert.equal(code, 2);
    assert.ok(err.includes('is not valid JSON'), err);
  });
});

describe('bringyourownagent check', () => {
  const check = (call, ...extra) =>
    run(
      'check',
      '--manifest', example('manifest.json'),
      '--grant=' + example('grant.json'),
      '--call', example(call),
      '--state', example('state.json'),
      ...extra,
    );

  test('allowed', () => {
    const { code, out } = check('call.json');
    assert.deepEqual([code, out], [0, 'decision: allowed\n']);
  });

  test('blocked', () => {
    const { code, out } = check('call-long-clip.json');
    assert.equal(code, 1);
    assert.equal(out, 'decision: blocked\n  blocked clip-too-long: the clip is 42 seconds; the limit is 15\n');
  });

  test('warn still proceeds', () => {
    const { code, out } = check('call-text.json', '--json');
    assert.equal(code, 0);
    assert.equal(JSON.parse(out).decision, 'warn');
  });

  test('state is optional', () => {
    const { code, out } = run(
      'check', '--manifest', example('manifest.json'), '--grant', example('grant.json'), '--call', example('call.json'),
    );
    assert.deepEqual([code, out], [0, 'decision: allowed\n']);
  });
});

describe('usage', () => {
  test('help and version', () => {
    let r = run('--help');
    assert.equal(r.code, 0);
    assert.ok(r.out.includes('usage: bringyourownagent <command>'));
    r = run('-V');
    assert.equal(r.code, 0);
    assert.ok(r.out.startsWith('bringyourownagent 0.'));
  });

  test('usage errors exit with 2', () => {
    for (const args of [
      [],
      ['frobnicate'],
      ['validate'],
      ['validate', 'a.json', 'b.json'],
      ['validate', '--bogus', 'a.json'],
      ['check', '--manifest', example('manifest.json')],
      ['check', '--call'],
    ]) {
      const { code, out, err } = run(...args);
      assert.equal(code, 2, args.join(' '));
      assert.equal(out, '');
      assert.ok(err.startsWith('bringyourownagent: '), err);
    }
  });

  test('the bin script runs as a process', () => {
    const done = spawnSync(process.execPath, [bin, 'check',
      '--manifest', example('manifest.json'), '--grant', example('grant.json'), '--call', example('call-long-clip.json')],
    { encoding: 'utf8' });
    assert.equal(done.status, 1, done.stderr);
    assert.ok(done.stdout.startsWith('decision: blocked\n'), done.stdout);
  });
});
