// The relay and a wallet that works with text: its own parallel limit, the models it gave the site, streamed pieces (spec/CONTRACT-DIRECT.md §4).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { connect, hello, registerCleanup, sleep, start } from './relay-helpers.js';

registerCleanup();

async function connectWith(env, grant) {
  const { code } = env.relay.newCode('page-1', { ip: '1.1.1.1' });
  const w = env.wallet();
  await w.open;
  const frame = hello(code);
  frame.grant = { ...frame.grant, ...grant };
  w.send(frame);
  const welcome = await w.next();
  return { w, welcome, wallet: env.relay.walletFor(welcome.pairing) };
}

const MODELS = { 'text.generate': [
  { ref: 'm1', name: 'Claude Opus', id: 'anthropic/claude-opus-4.6', runs: 'api-key' },
  { ref: 'm2', name: 'Model B', id: null, runs: 'local' },
] };

test('the hello tells the limits and the models, and a connection keeps only what is well formed', async () => {
  const env = await start();
  const { wallet } = await connectWith(env, {
    limits: { maxTextChars: 100000, maxParallelCalls: 5, maxCallSeconds: -1, maxAnswerTokens: 'many', surprise: 1 },
    models: { ...MODELS, 'text.generate': [...MODELS['text.generate'], { name: 'no ref' }, 'junk'] },
  });
  assert.deepEqual(wallet.grant.limits, { maxTextChars: 100000, maxParallelCalls: 5 });
  assert.deepEqual(wallet.grant.models, [
    { ref: 'm1', name: 'Claude Opus', id: 'anthropic/claude-opus-4.6', runs: 'api-key', kind: 'model' },
    { ref: 'm2', name: 'Model B', id: null, runs: 'local', kind: 'model' },
  ]);
});

test('an older wallet tells neither: no limits, no models, four calls in flight as before', async () => {
  const env = await start();
  const { wallet } = await connectWith(env, {});
  assert.equal(wallet.grant.limits, null);
  assert.deepEqual(wallet.grant.models, []);
  assert.equal(wallet.maxInFlight, 4);
});

test('as many calls are in flight as the wallet allows, and the relay option can only lower it', async () => {
  const env = await start();
  const { w, wallet } = await connectWith(env, { limits: { maxParallelCalls: 2 } });
  assert.equal(wallet.maxInFlight, 2);
  const answers = Array.from({ length: 4 }, (_, i) => wallet.call('text.generate', { i }));
  await sleep(150);
  assert.equal(w.frames.length, 2);
  for (const f of w.frames.splice(0)) w.send({ type: 'result', id: f.id, ok: true, output: { text: 'a' } });
  await sleep(100);
  assert.equal(w.frames.length, 2);
  for (const f of w.frames.splice(0)) w.send({ type: 'result', id: f.id, ok: true, output: { text: 'b' } });
  assert.ok((await Promise.all(answers)).every((a) => a.ok));

  const capped = await start({ maxInFlight: 1 });
  const second = await connectWith(capped, { limits: { maxParallelCalls: 8 } });
  assert.equal(second.wallet.maxInFlight, 1);
});

test('a grant frame replaces the models and the limits, and a raised parallel limit lets waiting calls go', async () => {
  const env = await start();
  const { w, wallet } = await connectWith(env, { limits: { maxParallelCalls: 1 }, models: MODELS });
  const seen = [];
  wallet.on('grant', (c) => seen.push(c.grant));
  const answers = [wallet.call('text.generate', { n: 1 }), wallet.call('text.generate', { n: 2 }), wallet.call('text.generate', { n: 3 })];
  await sleep(150);
  assert.equal(w.frames.length, 1);

  w.send({ type: 'grant', models: { 'text.generate': [{ ref: 'm3', name: 'Model C', id: null, runs: 'api-key' }] }, limits: { maxParallelCalls: 3 } });
  await sleep(150);
  assert.equal(seen.length, 1);
  assert.deepEqual(wallet.grant.models.map((m) => m.ref), ['m3']);
  assert.equal(wallet.grant.limits.maxParallelCalls, 3);
  assert.deepEqual(wallet.grant.skills.length > 0, true);       // what the hello said stays
  assert.equal(w.frames.length, 3);                             // the two that waited went out at once
  for (const f of w.frames.splice(0)) w.send({ type: 'result', id: f.id, ok: true, output: { text: 'x' } });
  assert.ok((await Promise.all(answers)).every((a) => a.ok));
});

test('streamed pieces reach onChunk in order before the result, and the receipt names the model that answered', async () => {
  const env = await start();
  const { w, wallet } = await connectWith(env, { limits: { maxParallelCalls: 4 }, models: MODELS });
  const pieces = [];
  const asked = wallet.call('text.generate', { messages: [] }, { model: 'm1', stream: true }, { onChunk: (t) => pieces.push(t) });
  const f = await w.next();
  assert.equal(f.params.model, 'm1');
  assert.equal(f.params.stream, true);
  w.send({ type: 'chunk', id: f.id, text: 'Hel' });
  w.send({ type: 'chunk', id: f.id, text: 'lo' });
  w.send({ type: 'chunk', id: 9999, text: 'not ours' });
  w.send({ type: 'chunk', id: f.id, text: 42 });
  w.send({ type: 'result', id: f.id, ok: true, output: { text: 'Hello' }, receipt: { cost: '0.001 USD', provider: 'api-key', model: 'm1' } });
  const answer = await asked;
  assert.deepEqual(pieces, ['Hel', 'lo']);
  assert.equal(answer.ok, true);
  assert.deepEqual(answer.receipt, { cost: '0.001 USD', provider: 'api-key', model: 'm1' });
});

test('a callback that throws does not break the connection, and chunks without a callback are ignored', async () => {
  const env = await start();
  const { w, wallet } = await connectWith(env, {});
  const angry = wallet.call('text.generate', {}, { stream: true }, { onChunk: () => { throw new Error('site bug'); } });
  const quiet = wallet.call('text.generate', {}, { stream: true });
  const [a, b] = [await w.next(), await w.next()];
  w.send({ type: 'chunk', id: a.id, text: 'x' });
  w.send({ type: 'chunk', id: b.id, text: 'y' });
  w.send({ type: 'result', id: a.id, ok: true, output: { text: 'x' } });
  w.send({ type: 'result', id: b.id, ok: true, output: { text: 'y' } });
  assert.equal((await angry).ok, true);
  assert.equal((await quiet).ok, true);
  assert.equal(wallet.live, true);
});

test('a receipt model that is too long is dropped', async () => {
  const env = await start();
  const { w, wallet } = await connectWith(env, {});
  const asked = wallet.call('text.generate', {});
  const f = await w.next();
  w.send({ type: 'result', id: f.id, ok: true, output: { text: 'x' }, receipt: { cost: '0 USD', provider: 'local', model: 'm'.repeat(40) } });
  assert.deepEqual((await asked).receipt, { cost: '0 USD', provider: 'local' });
});
