// The key's two homes: this tab (until it is closed) and, only when the visitor asked, this browser.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createStore } from '../src/store.js';

const memory = () => {
  const items = new Map();
  return {
    items,
    getItem: (k) => (items.has(k) ? items.get(k) : null),
    setItem: (k, v) => { items.set(k, String(v)); },
    removeItem: (k) => { items.delete(k); },
  };
};

const KEY = 'byoa-web:key';

test('by default the key lives in the tab only and nothing is written to the browser\'s own storage', () => {
  const local = memory();
  const session = memory();
  const store = createStore(local, session);
  assert.equal(store.setKey('sk-or-v1-abc'), true);
  assert.equal(store.getKey(), 'sk-or-v1-abc');
  assert.equal(session.items.get(KEY), 'sk-or-v1-abc');
  assert.equal(local.items.has(KEY), false);
  assert.equal(store.remembers(), false);
});

test('a closed tab forgets the key: a new tab (an empty session storage) has none unless it was remembered', () => {
  const local = memory();
  const first = createStore(local, memory());
  first.setKey('sk-or-v1-abc');
  assert.equal(createStore(local, memory()).getKey(), null);

  const kept = memory();
  createStore(kept, memory()).setKey('sk-or-v1-abc', { remember: true });
  const later = createStore(kept, memory());
  assert.equal(later.getKey(), 'sk-or-v1-abc');
  assert.equal(later.remembers(), true);
});

test('remember writes both homes; signing in again without it forgets the remembered key', () => {
  const local = memory();
  const session = memory();
  const store = createStore(local, session);
  store.setKey('old-key', { remember: true });
  assert.equal(local.items.get(KEY), 'old-key');
  store.setKey('new-key');
  assert.equal(store.getKey(), 'new-key');
  assert.equal(local.items.has(KEY), false);
});

test('signing out clears both homes', () => {
  const local = memory();
  const session = memory();
  const store = createStore(local, session);
  store.setKey('sk', { remember: true });
  store.clearKey();
  assert.equal(store.getKey(), null);
  assert.equal(session.items.has(KEY), false);
  assert.equal(local.items.has(KEY), false);
});

test('a key that is empty or too long is not taken, and storage that refuses is reported', () => {
  const store = createStore(memory(), memory());
  assert.equal(store.setKey(''), false);
  assert.equal(store.setKey('x'.repeat(401)), false);
  const refusing = { getItem: () => null, setItem: () => { throw new Error('quota'); }, removeItem: () => {} };
  assert.equal(createStore(memory(), refusing).setKey('sk'), false);
  assert.equal(createStore(refusing, memory()).setKey('sk', { remember: true }), false);
});

test('what a site was allowed is kept in the browser\'s storage, not the tab\'s', () => {
  const local = memory();
  const session = memory();
  const store = createStore(local, session);
  store.setSite('https://quiz.example.org', { models: ['a/b:free'], showNames: false, budgetUsd: 0.5, allowedAt: '2026-10-26T10:00:00Z' });
  assert.equal(session.items.size, 0);
  assert.deepEqual(store.getSite('https://quiz.example.org'), { models: ['a/b:free'], showNames: false, budgetUsd: 0.5, allowedAt: '2026-10-26T10:00:00Z' });
  store.forgetSite('https://quiz.example.org');
  assert.equal(store.getSite('https://quiz.example.org'), null);
});
