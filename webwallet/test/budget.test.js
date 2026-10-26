// Paid models in the web wallet: the budget the visitor must give, where its box starts, and how a price is shown.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { BUDGET, buildConsent, parseBudget, priceText, startBudget } from '../src/consent.js';
import { MANIFEST } from './helpers.js';

const build = (over = {}) => buildConsent({ host: 'quiz.example.org', manifest: MANIFEST, check: 'K7MQ', hasKey: true, pickedIds: ['a/one:free'], ...over });

test('a paid model needs a budget from the least to the most; free models need none', () => {
  assert.deepEqual(build({ paid: false, budgetUsd: Number.NaN }).blockers, []);
  assert.match(build({ paid: true, budgetUsd: Number.NaN }).blockers.join(), /Set a budget/);
  assert.match(build({ paid: true, budgetUsd: 0 }).blockers.join(), /Set a budget/);
  assert.match(build({ paid: true, budgetUsd: BUDGET.max + 1 }).blockers.join(), /Set a budget/);
  assert.deepEqual(build({ paid: true, budgetUsd: 0.5 }).blockers, []);
  assert.deepEqual(build({ paid: true, budgetUsd: BUDGET.max }).blockers, []);
});

test('the notes say paid models are billed to the visitor and the connection stops at its budget, only when a paid model is ticked', () => {
  assert.doesNotMatch(build().notes.join(' '), /billed/);
  const notes = build({ paid: true, budgetUsd: 1 }).notes.join(' ');
  assert.match(notes, /billed to your OpenRouter account/);
  assert.match(notes, /stops at the budget/);
});

test('the budget box starts at what the visitor gave the site, else what the site expects, else a small start, and never above the most', () => {
  assert.equal(startBudget({}, 0.4), 0.4);
  assert.equal(startBudget({}, 99), BUDGET.max);
  assert.equal(startBudget({ expectedCost: { perSession: '0.60 USD' } }), 0.6);
  assert.equal(startBudget({ expectedCost: { perSession: '0 USD' } }), BUDGET.start);
  assert.equal(startBudget({ expectedCost: { perSession: 'a lot' } }), BUDGET.start);
  assert.equal(startBudget({}), BUDGET.start);
  assert.equal(startBudget({ expectedCost: { perSession: '40 USD' } }), BUDGET.max);
});

test('a budget box is read as dollars, with a comma too, and anything else is not an amount', () => {
  assert.equal(parseBudget('0.25'), 0.25);
  assert.equal(parseBudget(' 1,5 '), 1.5);
  assert.equal(parseBudget('0.001'), null);
  assert.equal(parseBudget('5.01'), null);
  assert.equal(parseBudget('abc'), null);
  assert.equal(parseBudget(''), null);
});

test('a price is shown as free, or as dollars in and out per million tokens', () => {
  assert.equal(priceText({ free: true }), 'free');
  assert.equal(priceText({ free: false, pricePerMillion: { prompt: 0.15, completion: 0.6 } }), '$0.15 in / $0.60 out per million tokens');
  assert.equal(priceText({ free: false, pricePerMillion: { prompt: 0.001, completion: 3 } }), '<$0.01 in / $3.00 out per million tokens');
});
