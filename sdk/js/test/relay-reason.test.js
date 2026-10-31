// What the wallet says about a finished call reaches the site: why it failed (`reason`), what an agent could not do (`lacks`), and the site's
// own timeout stops the call at the wallet (the `cancel` frame) when the wallet says it understands it (spec/CONTRACT-DIRECT.md section 4).

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { MANIFEST, fakeWallet, hello, registerCleanup, sleep, start } from './relay-helpers.js';

registerCleanup();

async function connectWith(env, capabilities) {
  const { code } = env.relay.newCode('page-1', { ip: '1.1.1.1' });
  const w = env.wallet();
  await w.open;
  const base = hello(code);
  w.send(capabilities === undefined ? base : { ...base, grant: { ...base.grant, capabilities } });
  const welcome = await w.next();
  return { w, wallet: env.relay.walletFor(welcome.pairing) };
}

describe('why a call failed, and what an agent lacked', () => {
  test('the wallet\'s reason reaches the site; a word outside the closed list is "failed"; no reason stays no reason', async () => {
    const env = await start();
    const { w, wallet } = await connectWith(env, ['needs', 'can', 'kind', 'cancel']);
    for (const [said, told] of [['timeout', 'timeout'], ['cancelled', 'cancelled'], ['limit', 'limit'], ['failed', 'failed'], ['no model for you', 'failed']]) {
      const answer = wallet.call('text.generate', {});
      const frame = await w.next();
      w.send({ type: 'result', id: frame.id, ok: false, reason: said });
      assert.deepEqual(await answer, { ok: false, reason: told });
    }

    const plain = wallet.call('text.generate', {});
    const frame = await w.next();
    w.send({ type: 'result', id: frame.id, ok: false });
    assert.deepEqual(await plain, { ok: false });                       // an older wallet says nothing: neither does the answer
  });

  test('what the agent could not do reaches the site as ability names only', async () => {
    const env = await start();
    const { w, wallet } = await connectWith(env);
    const answer = wallet.call('text.generate', {});
    const frame = await w.next();
    w.send({ type: 'result', id: frame.id, ok: true, output: { text: 'Done, without the web.' }, receipt: { cost: '0.000 USD', provider: 'local' },
      lacks: ['web.read', 'web.read', 'image.read', 'Not An Ability', 42, 'x'.repeat(40)] });
    const told = await answer;
    assert.equal(told.ok, true);
    assert.deepEqual(told.lacks, ['web.read', 'image.read']);

    const whole = wallet.call('text.generate', {});
    const next = await w.next();
    w.send({ type: 'result', id: next.id, ok: true, output: { text: 'Done.' }, lacks: [] });
    assert.equal('lacks' in (await whole), false);                      // nothing lacking: no field
  });
});

describe('the site\'s own timeout stops the call at the wallet', () => {
  test('a wallet that says it understands cancel gets the frame for the call that timed out', async () => {
    const env = await start({ callTimeoutMs: 80, firstCallTimeoutMs: 80 });
    const { w, wallet } = await connectWith(env, ['cancel']);
    const answer = wallet.call('text.generate', {});
    const call = await w.next();
    assert.equal(call.type, 'call');
    assert.deepEqual(await answer, { ok: false, timedOut: true, reason: 'timeout' });
    assert.deepEqual(await w.next(), { type: 'cancel', id: call.id });
  });

  test('a wallet that does not say so is not sent a frame it may not know', async () => {
    const env = await start({ callTimeoutMs: 80, firstCallTimeoutMs: 80 });
    const { w, wallet } = await connectWith(env);
    const answer = wallet.call('text.generate', {});
    await w.next();
    assert.equal((await answer).timedOut, true);
    await sleep(100);
    assert.equal(w.frames.length, 0);
  });
});
