import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildConsent, canAllow, CEILINGS, DEFAULT_LIMITS, effectiveLimits, modelSpec, nameMatches, normalizeModelName, unmetRequirements } from '../src/consent.js';
import { MANIFEST } from './helpers.js';

const build = (over = {}) => buildConsent({ host: 'quiz.example.org', manifest: MANIFEST, check: 'K7MQ', hasKey: true, pickedIds: ['a/one:free'], ...over });

test('a good request has no blockers, shows the host, the code and that the check is required', () => {
  const m = build();
  assert.deepEqual(m.blockers, []);
  assert.equal(m.host, 'quiz.example.org');
  assert.equal(m.check.code, 'K7MQ');
  assert.equal(m.check.required, true);
  assert.equal(m.summary, MANIFEST.purpose.summary);
});

test('Allow waits for the tick that the code is the same, when the site shows one', () => {
  const m = build();
  assert.equal(canAllow(m, { confirmed: false }), false);
  assert.equal(canAllow(m, { confirmed: true }), true);
  const noCheck = build({ manifest: { ...MANIFEST, connect: undefined } });
  assert.equal(noCheck.check.required, false);
  assert.equal(canAllow(noCheck, { confirmed: false }), true);
});

test('what blocks Allow: no key, no model, too few models, an unserved required skill, data a cloud must not see', () => {
  assert.match(build({ hasKey: false }).blockers[0], /Sign in/);
  assert.match(build({ pickedIds: [] }).blockers.join(), /at least one model/);
  const two = { ...MANIFEST, skills: { 'text.generate': { models: { count: { min: 2 } } } } };
  assert.match(build({ manifest: two }).blockers.join(), /at least 2/);
  const audio = { ...MANIFEST, skills: { 'text.generate': {}, 'audio.transcribe': { required: true } } };
  assert.match(build({ manifest: audio }).blockers.join(), /audio\.transcribe/);
  const onlyAudio = { ...MANIFEST, skills: { 'audio.transcribe': { required: false } } };
  assert.match(build({ manifest: onlyAudio }).blockers.join(), /nothing this wallet can do/);
  for (const privacy of ['sensitive', 'personal-voice', 'local-only']) {
    const m = build({ manifest: { ...MANIFEST, privacy } });
    assert.match(m.blockers.join(), /must not go to a cloud/);
    assert.equal(canAllow(m, { confirmed: true }), false);
  }
  const personal = build({ manifest: { ...MANIFEST, privacy: 'personal' } });
  assert.deepEqual(personal.blockers, []);
  assert.match(personal.warnings.join(), /personal/);
});

test('an optional skill the wallet cannot serve does not block', () => {
  const m = build({ manifest: { ...MANIFEST, skills: { 'text.generate': {}, 'audio.transcribe': { required: false } } } });
  assert.deepEqual(m.blockers, []);
  assert.equal(m.skills.find((s) => s.id === 'audio.transcribe').served, false);
});

test('model names compare as the contract says: case, dots, a date and a variant do not matter', () => {
  assert.equal(normalizeModelName('Vendor/Model-3.5:free'), 'vendor/model-3-5');
  assert.equal(normalizeModelName('vendor/model-2024-05-13'), 'vendor/model');
  assert.equal(nameMatches('llama-3.3-70b*', 'meta-llama/llama-3.3-70b-instruct:free'), true);    // no vendor in the pattern: the model part only
  assert.equal(nameMatches('other/llama-3.3*', 'meta/llama-3.3-70b-instruct:free'), false);
  assert.equal(nameMatches('llama-3.3*', 'meta/llama-3.3-70b-instruct:free'), true);
  assert.equal(nameMatches('meta/llama-3.3*', 'meta/llama-3.3-70b-instruct'), true);
  assert.equal(nameMatches('gemma-2', 'google/gemma-2:free'), true);
  assert.equal(nameMatches('gemma-2', 'google/gemma-3:free'), false);
});

test('a required model that was not ticked blocks; an entry that is a list means any of them', () => {
  const spec = modelSpec({ skills: { 'text.generate': { models: { require: ['gemma-2', ['qwen*', 'llama*']] } } } });
  assert.deepEqual(unmetRequirements(spec, ['google/gemma-2:free', 'x/llama-3:free']), []);
  assert.deepEqual(unmetRequirements(spec, ['google/gemma-2:free']), [['qwen*', 'llama*']]);
  assert.deepEqual(unmetRequirements(null, []), []);
  const m = build({ manifest: { ...MANIFEST, skills: { 'text.generate': { models: { require: ['gemma-2'] } } } } });
  assert.match(m.blockers.join(), /requires gemma-2/);
});

test('model counts default to 1 to 5 and are clamped', () => {
  assert.equal(modelSpec({ skills: { 'text.generate': { models: {} } } }).max, 5);
  assert.equal(modelSpec({ skills: { 'text.generate': { models: { count: 3 } } } }).min, 3);
  assert.equal(modelSpec({ skills: { 'text.generate': { models: { count: { min: 99 } } } } }).min, 16);
  assert.equal(modelSpec({ skills: { 'text.generate': {} } }), null);
});

test('limits: the wallet\'s, tightened by the site, never over the ceilings', () => {
  assert.deepEqual(effectiveLimits({}), { ...DEFAULT_LIMITS });
  const l = effectiveLimits({ purpose: { limits: { maxCallsPerMinute: 5, maxTextChars: 99_999_999, maxAnswerTokens: 0, maxParallelCalls: 'many' } } });
  assert.equal(l.maxCallsPerMinute, 5);
  assert.equal(l.maxTextChars, DEFAULT_LIMITS.maxTextChars);
  assert.equal(l.maxAnswerTokens, DEFAULT_LIMITS.maxAnswerTokens);
  assert.equal(l.maxParallelCalls, DEFAULT_LIMITS.maxParallelCalls);
  assert.equal(effectiveLimits({}, { maxTextChars: 10 ** 9 }).maxTextChars, CEILINGS.maxTextChars);
});

test('the notes say prompts go to a cloud and that the tab is the wallet', () => {
  const notes = build().notes.join(' ');
  assert.match(notes, /OpenRouter/);
  assert.match(notes, /Keep it open/);
});
