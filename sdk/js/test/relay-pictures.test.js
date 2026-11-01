// A picture call (`image.generate`, wallet 0.37): the wallet sends each picture in `image` parts, then the `result` that says what each
// one is; the relay joins and checks them, and anything that does not add up is a failed call (spec/CONTRACT-DIRECT.md section 4).

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { describe, test } from 'node:test';
import { PictureParts, sniffPicture } from '../src/site/relay.js';
import { hello, registerCleanup, start } from './relay-helpers.js';

registerCleanup();

async function connect(env) {
  const { code } = env.relay.newCode('page-1', { ip: '1.1.1.1' });
  const w = env.wallet();
  await w.open;
  const base = hello(code);
  w.send({ ...base, grant: { ...base.grant, skills: ['text.generate', 'image.generate'], capabilities: ['cancel', 'image.generate', 'parts'] } });
  const welcome = await w.next();
  return { w, wallet: env.relay.walletFor(welcome.pairing) };
}

/** Bytes that begin like a PNG file and go on with noise (the relay looks at the first bytes only). */
function png(size, seed = 1) {
  const bytes = Buffer.alloc(size);
  let x = seed;
  for (let i = 0; i < size; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; bytes[i] = x & 0xff; }
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(bytes, 0);
  return bytes;
}

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');

function parts(id, index, bytes, partChars = 524_288, mime = 'image/png') {
  const data = bytes.toString('base64');
  const pieces = [];
  for (let i = 0; i < data.length; i += partChars) pieces.push(data.slice(i, i + partChars));
  return pieces.map((piece, seq) => ({ type: 'image', id, index, seq, of: pieces.length, mime, data: piece }));
}

const result = (id, pictures) => ({ type: 'result', id, ok: true, output: { images: pictures }, receipt: { cost: '0.068 USD', provider: 'api-key', model: 'm3' } });

describe('picture calls', () => {
  test('the parts come back as one picture, with the receipt; the call carries the prompt and the ref as asked', async () => {
    const env = await start();
    const { w, wallet } = await connect(env);
    const asked = wallet.call('image.generate', { prompt: 'A fox', count: 1 }, { model: 'm3' });
    const call = await w.next();
    assert.equal(call.skill, 'image.generate');
    assert.deepEqual(call.params, { model: 'm3' });
    assert.deepEqual(call.input, { prompt: 'A fox', count: 1 });
    const picture = png(1_100_000);
    const sent = parts(call.id, 0, picture);
    assert.equal(sent.length, 3);
    for (const p of sent) w.send(p);
    w.send(result(call.id, [{ mime: 'image/png', size: picture.length, sha256: sha(picture) }]));
    const answer = await asked;
    assert.equal(answer.ok, true);
    assert.deepEqual(answer.receipt, { cost: '0.068 USD', provider: 'api-key', model: 'm3' });
    const [image] = answer.output.images;
    assert.equal(answer.output.images.length, 1);
    assert.equal(image.mime, 'image/png');
    assert.equal(image.size, picture.length);
    assert.equal(image.sha256, sha(picture));
    assert.equal(image.reduced, false);
    assert.ok(Buffer.from(image.data, 'base64').equals(picture));
  });

  test('parts are joined in their order whatever order they came in; two pictures stay apart; reduced is passed on', async () => {
    const env = await start();
    const { w, wallet } = await connect(env);
    const asked = wallet.call('image.generate', { prompt: 'Two', count: 2 }, { model: 'm3' });
    const call = await w.next();
    const a = png(30_000, 1);
    const b = png(25_000, 2);
    for (const p of [...parts(call.id, 0, a, 10_000), ...parts(call.id, 1, b, 10_000)].reverse()) w.send(p);
    w.send(result(call.id, [{ mime: 'image/png', size: a.length, sha256: sha(a), reduced: true }, { mime: 'image/png', size: b.length, sha256: sha(b) }]));
    const answer = await asked;
    assert.equal(answer.ok, true);
    assert.ok(Buffer.from(answer.output.images[0].data, 'base64').equals(a));
    assert.ok(Buffer.from(answer.output.images[1].data, 'base64').equals(b));
    assert.deepEqual(answer.output.images.map((i) => i.reduced), [true, false]);
  });

  for (const fault of ['missing part', 'wrong sha256', 'wrong size', 'not a picture', 'a picture more than described', 'a part twice',
    'parts that disagree', 'no pictures at all', 'type unlike the bytes']) {
    test(`an answer that does not add up is a failed call: ${fault}`, async () => {
      const env = await start();
      const { w, wallet } = await connect(env);
      const asked = wallet.call('image.generate', { prompt: 'A fox' }, { model: 'm3' });
      const call = await w.next();
      const picture = fault === 'not a picture' ? Buffer.from('hello, this is text and no picture at all') : png(30_000);
      let sent = parts(call.id, 0, picture, 10_000);
      let described = [{ mime: 'image/png', size: picture.length, sha256: sha(picture) }];
      if (fault === 'missing part') sent.splice(1, 1);
      if (fault === 'wrong sha256') described = [{ ...described[0], sha256: sha(png(10)) }];
      if (fault === 'wrong size') described = [{ ...described[0], size: picture.length - 1 }];
      if (fault === 'a picture more than described') sent.push(...parts(call.id, 1, png(500, 3)));
      if (fault === 'a part twice') sent.push(sent[0]);
      if (fault === 'parts that disagree') sent[1] = { ...sent[1], of: sent[1].of + 1 };
      if (fault === 'no pictures at all') described = [];
      if (fault === 'type unlike the bytes') {
        sent = parts(call.id, 0, picture, 10_000, 'image/jpeg');
        described = [{ ...described[0], mime: 'image/jpeg' }];
      }
      for (const p of sent) w.send(p);
      w.send(result(call.id, described));
      assert.deepEqual(await asked, { ok: false, reason: 'failed' });
    });
  }

  test('more than 8,000,000 picture bytes are a failed call', async () => {
    const env = await start();
    const { w, wallet } = await connect(env);
    const asked = wallet.call('image.generate', { prompt: 'Big', count: 2 }, { model: 'm3' });
    const call = await w.next();
    const a = png(4_500_000, 1);
    const b = png(4_500_000, 2);
    for (const p of [...parts(call.id, 0, a), ...parts(call.id, 1, b)]) w.send(p);
    w.send(result(call.id, [{ mime: 'image/png', size: a.length, sha256: sha(a) }, { mime: 'image/png', size: b.length, sha256: sha(b) }]));
    assert.deepEqual(await asked, { ok: false, reason: 'failed' });
  });

  test('parts for a text call, or for no call, are ignored; a wallet that says no gives its reason', async () => {
    const env = await start();
    const { w, wallet } = await connect(env);
    const text = wallet.call('text.generate', { messages: [{ role: 'user', content: 'Hi?' }] });
    const call = await w.next();
    for (const p of [...parts(call.id, 0, png(500)), ...parts(999, 0, png(500))]) w.send(p);
    w.send({ type: 'result', id: call.id, ok: true, output: { text: 'Hi.' } });
    assert.deepEqual((await text).output, { text: 'Hi.' });

    const refused = wallet.call('image.generate', { prompt: 'A fox' }, { model: 'm3' });
    const second = await w.next();
    w.send(parts(second.id, 0, png(500))[0]);
    w.send({ type: 'result', id: second.id, ok: false, reason: 'limit' });
    assert.deepEqual(await refused, { ok: false, reason: 'limit' });
  });

  test('the bytes, not a field, say the type', () => {
    assert.equal(sniffPicture(png(20)), 'image/png');
    assert.equal(sniffPicture(Buffer.from([0xff, 0xd8, 0xff, 0xe0])), 'image/jpeg');
    assert.equal(sniffPicture(Buffer.from('GIF89a....')), 'image/gif');
    assert.equal(sniffPicture(Buffer.from('RIFF\0\0\0\0WEBPVP8 ')), 'image/webp');
    assert.equal(sniffPicture(Buffer.from('<svg xmlns=')), null);
    const parts = new PictureParts();
    parts.add({ index: 0, seq: 0, of: 1, mime: 'image/svg+xml', data: 'PHN2Zz4=' });     // not a type a wallet may send
    assert.equal(parts.join([{ mime: 'image/svg+xml', size: 5, sha256: 'x' }]), null);
  });
});
