// The small QR encoder behind the "Open on another device" block.
//
// The golden hashes below are of symbols that an independent decoder (zxing-cpp) read back to exactly the input text when
// they were made, and so were 322 more: every version from 1 to 40 at each of the four error correction levels, packed full
// and half full. A hash that changes means the symbol changed: look at it with a decoder before accepting the new one.

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { describe, test } from 'node:test';
import { capacityBytes, dataCodewords, encodeQr, qrPath, qrRows, rsDivisor, rsRemainder } from '../src/site/qr.js';

const sha = (qr) => createHash('sha256').update(qrRows(qr).join('\n')).digest('hex');

describe('error correction codewords', () => {
  test('the generator polynomial of degree 7', () => {
    assert.deepEqual(rsDivisor(7), [127, 122, 154, 164, 11, 68, 117]);
  });

  test('the worked example of the standard (01234567, version 1-M): ten codewords', () => {
    const data = [0x10, 0x20, 0x0c, 0x56, 0x61, 0x80, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11];
    assert.deepEqual(rsRemainder(data, rsDivisor(10)), [0xa5, 0x24, 0xd4, 0xc1, 0xed, 0x36, 0xc7, 0x87, 0x2c, 0x55]);
  });
});

describe('capacity', () => {
  test('the most bytes one symbol holds at each level', () => {
    assert.equal(capacityBytes('L'), 2953);
    assert.equal(capacityBytes('M'), 2331);
    assert.equal(capacityBytes('Q'), 1663);
    assert.equal(capacityBytes('H'), 1273);
  });

  test('data codewords of every version, as the standard lists them', () => {
    const table = {
      L: [19, 34, 55, 80, 108, 136, 156, 194, 232, 274, 324, 370, 428, 461, 523, 589, 647, 721, 795, 861, 932, 1006, 1094, 1174, 1276, 1370, 1468, 1531, 1631, 1735, 1843, 1955, 2071, 2191, 2306, 2434, 2566, 2702, 2812, 2956],
      M: [16, 28, 44, 64, 86, 108, 124, 154, 182, 216, 254, 290, 334, 365, 415, 453, 507, 563, 627, 669, 714, 782, 860, 914, 1000, 1062, 1128, 1193, 1267, 1373, 1455, 1541, 1631, 1725, 1812, 1914, 1992, 2102, 2216, 2334],
      Q: [13, 22, 34, 48, 62, 76, 88, 110, 132, 154, 180, 206, 244, 261, 295, 325, 367, 397, 445, 485, 512, 568, 614, 664, 718, 754, 808, 871, 911, 985, 1033, 1115, 1171, 1231, 1286, 1354, 1426, 1502, 1582, 1666],
      H: [9, 16, 26, 36, 46, 60, 66, 86, 100, 122, 140, 158, 180, 197, 223, 253, 283, 313, 341, 385, 406, 442, 464, 514, 538, 596, 628, 661, 701, 745, 793, 845, 901, 961, 986, 1054, 1096, 1142, 1222, 1276],
    };
    for (const [ecl, list] of Object.entries(table)) {
      list.forEach((expected, i) => assert.equal(dataCodewords(i + 1, ecl), expected, `version ${i + 1}, ${ecl}`));
    }
  });
});

describe('encodeQr', () => {
  test('the smallest version that fits is used', () => {
    assert.equal(encodeQr('a'.repeat(17), 'L').version, 1);
    assert.equal(encodeQr('a'.repeat(18), 'L').version, 2);
    assert.equal(encodeQr('a'.repeat(14), 'M').version, 1);
    assert.equal(encodeQr('a'.repeat(15), 'M').version, 2);
  });

  test('text is counted in UTF-8 bytes, not in characters', () => {
    const eAcute = String.fromCharCode(233);                        // two bytes in UTF-8
    assert.equal(encodeQr(eAcute.repeat(8), 'L').version, 1);       // 16 bytes fit version 1 (17)
    assert.equal(encodeQr(eAcute.repeat(9), 'L').version, 2);       // 18 bytes do not
  });

  test('a text too long for any symbol gives null; the longest that fits gives a version 40', () => {
    assert.equal(encodeQr('a'.repeat(capacityBytes('L') + 1), 'L'), null);
    assert.equal(encodeQr('a'.repeat(capacityBytes('L')), 'L').version, 40);
  });

  test('an unknown error correction level is refused', () => {
    assert.throws(() => encodeQr('x', 'Z'), RangeError);
  });

  test('the symbol has its fixed patterns', () => {
    const qr = encodeQr('BYOA', 'M');
    const n = qr.size;
    assert.equal(n, qr.version * 4 + 17);
    const m = qr.modules;
    const finder = (x0, y0) => {
      for (let dy = 0; dy < 7; dy++) {
        for (let dx = 0; dx < 7; dx++) {
          const ring = Math.max(Math.abs(dx - 3), Math.abs(dy - 3));
          assert.equal(m[y0 + dy][x0 + dx], ring !== 2, `finder at ${x0},${y0}: ${dx},${dy}`);
        }
      }
    };
    finder(0, 0);
    finder(n - 7, 0);
    finder(0, n - 7);
    for (let i = 8; i < n - 8; i++) {
      assert.equal(m[6][i], i % 2 === 0, `timing row at ${i}`);
      assert.equal(m[i][6], i % 2 === 0, `timing column at ${i}`);
    }
    assert.equal(m[n - 8][8], true, 'the dark module');
  });

  test('the symbol is the same every time for the same text', () => {
    assert.equal(sha(encodeQr('same', 'L')), sha(encodeQr('same', 'L')));
    assert.notEqual(sha(encodeQr('same', 'L')), sha(encodeQr('same', 'M')));
  });

  test('golden symbols, read back correctly by an independent decoder', () => {
    const link = 'bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2Fgeo.example.org%2Fbyoa%2Fws&manifest=' + 'A'.repeat(300) + '&session=abcdefghijklmnopqrstuvwx';
    const golden = [
      ['HELLO WORLD', 'L', 1, 3, 'a279bbd7ebb2c0c26b2020c240b69c4bed2bf062d82934e50f86a2ac20bb22b3'],
      ['https://bringyourownagent.org/', 'M', 3, 6, '2b23ac3f8931bb777c173d342a07367da847ed220c2683fcfa57aa738980f9ad'],
      ['The quick brown fox jumps over the lazy dog', 'Q', 4, 0, '7466040f800d4808c57da7137aede7ac4df9a74002f6caa8e4f2eb79cea92e7c'],
      [link, 'L', 13, 3, '6a7322230a65181a75bcb64d91a54185e4c33a1cdde779146d9eb2ab8ec36727'],
      ['x'.repeat(1000), 'H', 36, 0, '19dee63129106a4239a0f9f8f6cb2aaeb9a87daab74bd587fec8da41ee012c4c'],
      ['y'.repeat(2900), 'L', 40, 1, '78fa9e9a66a4845cf6b6ee1f6979a21d03adcfaf7c685fae9f26a9fb671ddfdf'],
    ];
    for (const [text, ecl, version, mask, hash] of golden) {
      const qr = encodeQr(text, ecl);
      assert.deepEqual([qr.version, qr.mask, sha(qr)], [version, mask, hash], `${text.slice(0, 20)}... ${ecl}`);
    }
  });
});

describe('qrPath', () => {
  test('draws exactly the dark modules, a run at a time, inside a margin', () => {
    const qr = encodeQr('path', 'L');
    const path = qrPath(qr, 4);
    let dark = 0;
    for (const row of qr.modules) for (const v of row) if (v) dark++;
    const runs = [...path.matchAll(/M(\d+),(\d+)h(\d+)v1h-(\d+)z/g)];
    assert.equal(runs.reduce((sum, r) => sum + Number(r[3]), 0), dark);
    for (const r of runs) {
      assert.equal(r[3], r[4]);
      assert.ok(Number(r[1]) >= 4 && Number(r[1]) + Number(r[3]) <= qr.size + 4);
      assert.ok(Number(r[2]) >= 4 && Number(r[2]) < qr.size + 4);
    }
  });
});
