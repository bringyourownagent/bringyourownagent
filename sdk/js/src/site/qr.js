// A small QR Code encoder for the page's "Open on another device" block: byte mode (UTF-8), versions 1 to 40, the four error
// correction levels, and the mask chosen by the standard's penalty rules. No dependencies, no network, nothing to load.
//
// Written for this package from ISO/IEC 18004. The way the symbol is drawn (function patterns first, then the interleaved
// codewords in a zigzag, then the mask) follows the public design of Nayuki's QR Code generator, which is the clearest
// description of the standard; no code was copied from it.

const ECL = Object.freeze({
  L: { ordinal: 0, formatBits: 1 },
  M: { ordinal: 1, formatBits: 0 },
  Q: { ordinal: 2, formatBits: 3 },
  H: { ordinal: 3, formatBits: 2 },
});

// [ecl ordinal][version]; index 0 of each row is unused
const ECC_PER_BLOCK = [
  [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
  [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
  [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
  [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
];
const BLOCKS = [
  [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
  [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
  [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
  [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
];

const MIN_VERSION = 1;
const MAX_VERSION = 40;

/** Data and error correction modules of a version (everything except the function patterns). */
function rawModules(version) {
  let result = (16 * version + 128) * version + 64;
  if (version >= 2) {
    const aligns = Math.floor(version / 7) + 2;
    result -= (25 * aligns - 10) * aligns - 55;
    if (version >= 7) result -= 36;
  }
  return result;
}

/** Data codewords (bytes) a version holds at an error correction level. */
export function dataCodewords(version, ecl = 'L') {
  const e = ECL[ecl].ordinal;
  return Math.floor(rawModules(version) / 8) - ECC_PER_BLOCK[e][version] * BLOCKS[e][version];
}

/** The most UTF-8 bytes one symbol can carry at an error correction level (version 40). */
export function capacityBytes(ecl = 'L') {
  return dataCodewords(MAX_VERSION, ecl) - 3;
}

function gfMultiply(x, y) {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z;
}

/** The generator polynomial of the given degree, without its leading term. */
export function rsDivisor(degree) {
  const result = new Array(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < degree; j++) {
      result[j] = gfMultiply(result[j], root);
      if (j + 1 < degree) result[j] ^= result[j + 1];
    }
    root = gfMultiply(root, 0x02);
  }
  return result;
}

/** The error correction codewords of a block of data. */
export function rsRemainder(data, divisor) {
  const result = divisor.map(() => 0);
  for (const b of data) {
    const factor = b ^ result.shift();
    result.push(0);
    divisor.forEach((coef, i) => { result[i] ^= gfMultiply(coef, factor); });
  }
  return result;
}

const bit = (value, i) => ((value >>> i) & 1) !== 0;

function alignmentPositions(version) {
  if (version === 1) return [];
  const count = Math.floor(version / 7) + 2;
  const step = version === 32 ? 26 : Math.ceil((version * 4 + 4) / (count * 2 - 2)) * 2;
  const result = [6];
  for (let pos = version * 4 + 10; result.length < count; pos -= step) result.splice(1, 0, pos);
  return result;
}

const MASKS = [
  (x, y) => (x + y) % 2 === 0,
  (x, y) => y % 2 === 0,
  (x, y) => x % 3 === 0,
  (x, y) => (x + y) % 3 === 0,
  (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
  (x, y) => ((x * y) % 2) + ((x * y) % 3) === 0,
  (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 === 0,
  (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 === 0,
];

class QrSymbol {
  constructor(version, ecl, codewords) {
    this.version = version;
    this.ecl = ecl;
    this.size = version * 4 + 17;
    const grid = () => Array.from({ length: this.size }, () => new Array(this.size).fill(false));
    this.modules = grid();
    this.isFunction = grid();
    this.drawFunctionPatterns();
    this.drawCodewords(this.interleave(codewords));
    let best = 0;
    let bestPenalty = Infinity;
    for (let m = 0; m < 8; m++) {
      this.applyMask(m);
      this.drawFormatBits(m);
      const p = this.penalty();
      if (p < bestPenalty) { best = m; bestPenalty = p; }
      this.applyMask(m);                                   // the same call undoes it
    }
    this.mask = best;
    this.applyMask(best);
    this.drawFormatBits(best);
  }

  setFunction(x, y, dark) {
    this.modules[y][x] = dark;
    this.isFunction[y][x] = true;
  }

  drawFunctionPatterns() {
    const n = this.size;
    for (let i = 0; i < n; i++) {
      this.setFunction(6, i, i % 2 === 0);
      this.setFunction(i, 6, i % 2 === 0);
    }
    this.drawFinder(3, 3);
    this.drawFinder(n - 4, 3);
    this.drawFinder(3, n - 4);
    const pos = alignmentPositions(this.version);
    for (let i = 0; i < pos.length; i++) {
      for (let j = 0; j < pos.length; j++) {
        if ((i === 0 && j === 0) || (i === 0 && j === pos.length - 1) || (i === pos.length - 1 && j === 0)) continue;
        this.drawAlignment(pos[i], pos[j]);
      }
    }
    this.drawFormatBits(0);                                // a placeholder that marks the area; the real bits come after the mask is chosen
    this.drawVersion();
  }

  drawFinder(x, y) {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const dist = Math.max(Math.abs(dx), Math.abs(dy));
        const xx = x + dx;
        const yy = y + dy;
        if (xx >= 0 && xx < this.size && yy >= 0 && yy < this.size) this.setFunction(xx, yy, dist !== 2 && dist !== 4);
      }
    }
  }

  drawAlignment(x, y) {
    for (let dy = -2; dy <= 2; dy++) {
      for (let dx = -2; dx <= 2; dx++) this.setFunction(x + dx, y + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
  }

  drawFormatBits(mask) {
    const n = this.size;
    const data = (ECL[this.ecl].formatBits << 3) | mask;
    let rem = data;
    for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
    const bits = ((data << 10) | rem) ^ 0x5412;
    for (let i = 0; i <= 5; i++) this.setFunction(8, i, bit(bits, i));
    this.setFunction(8, 7, bit(bits, 6));
    this.setFunction(8, 8, bit(bits, 7));
    this.setFunction(7, 8, bit(bits, 8));
    for (let i = 9; i < 15; i++) this.setFunction(14 - i, 8, bit(bits, i));
    for (let i = 0; i < 8; i++) this.setFunction(n - 1 - i, 8, bit(bits, i));
    for (let i = 8; i < 15; i++) this.setFunction(8, n - 15 + i, bit(bits, i));
    this.setFunction(8, n - 8, true);                      // the one module that is always dark
  }

  drawVersion() {
    if (this.version < 7) return;
    let rem = this.version;
    for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
    const bits = (this.version << 12) | rem;
    for (let i = 0; i < 18; i++) {
      const a = this.size - 11 + (i % 3);
      const b = Math.floor(i / 3);
      this.setFunction(a, b, bit(bits, i));
      this.setFunction(b, a, bit(bits, i));
    }
  }

  /** Splits the data codewords into blocks, adds each block's error correction, and interleaves them. */
  interleave(data) {
    const e = ECL[this.ecl].ordinal;
    const blocks = BLOCKS[e][this.version];
    const eccLen = ECC_PER_BLOCK[e][this.version];
    const raw = Math.floor(rawModules(this.version) / 8);
    const shortBlocks = blocks - (raw % blocks);
    const shortLen = Math.floor(raw / blocks);
    const divisor = rsDivisor(eccLen);
    const list = [];
    for (let i = 0, k = 0; i < blocks; i++) {
      const part = data.slice(k, k + shortLen - eccLen + (i < shortBlocks ? 0 : 1));
      k += part.length;
      const ecc = rsRemainder(part, divisor);
      if (i < shortBlocks) part.push(0);                   // a placeholder so that all blocks line up; it is skipped below
      list.push(part.concat(ecc));
    }
    const result = [];
    for (let i = 0; i < list[0].length; i++) {
      list.forEach((block, j) => {
        if (i !== shortLen - eccLen || j >= shortBlocks) result.push(block[i]);
      });
    }
    return result;
  }

  drawCodewords(data) {
    const n = this.size;
    let i = 0;
    for (let right = n - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (let vert = 0; vert < n; vert++) {
        for (let j = 0; j < 2; j++) {
          const x = right - j;
          const upward = ((right + 1) & 2) === 0;
          const y = upward ? n - 1 - vert : vert;
          if (!this.isFunction[y][x] && i < data.length * 8) {
            this.modules[y][x] = bit(data[i >>> 3], 7 - (i & 7));
            i++;
          }
        }
      }
    }
  }

  applyMask(mask) {
    const invert = MASKS[mask];
    for (let y = 0; y < this.size; y++) {
      for (let x = 0; x < this.size; x++) {
        if (!this.isFunction[y][x] && invert(x, y)) this.modules[y][x] = !this.modules[y][x];
      }
    }
  }

  /** The standard's four penalties: runs of five or more, 2x2 blocks, finder-like patterns, and an unbalanced share of dark modules. */
  penalty() {
    const n = this.size;
    const m = this.modules;
    let total = 0;
    const lines = [];
    for (let y = 0; y < n; y++) lines.push(m[y]);
    for (let x = 0; x < n; x++) lines.push(m.map((row) => row[x]));
    const finderA = [true, false, true, true, true, false, true, false, false, false, false];
    const finderB = [false, false, false, false, true, false, true, true, true, false, true];
    for (const line of lines) {
      let run = 1;
      for (let i = 1; i <= n; i++) {
        if (i < n && line[i] === line[i - 1]) { run++; continue; }
        if (run >= 5) total += 3 + (run - 5);
        run = 1;
      }
      for (let i = 0; i + 11 <= n; i++) {
        if (finderA.every((v, k) => line[i + k] === v)) total += 40;
        if (finderB.every((v, k) => line[i + k] === v)) total += 40;
      }
    }
    for (let y = 0; y + 1 < n; y++) {
      for (let x = 0; x + 1 < n; x++) {
        if (m[y][x] === m[y][x + 1] && m[y][x] === m[y + 1][x] && m[y][x] === m[y + 1][x + 1]) total += 3;
      }
    }
    let dark = 0;
    for (const row of m) for (const v of row) if (v) dark++;
    total += (Math.ceil(Math.abs(dark * 20 - n * n * 10) / (n * n)) - 1) * 10;
    return total;
  }
}

/**
 * Encodes text (as UTF-8 bytes) into the smallest symbol that holds it.
 * @param {string} text
 * @param {'L'|'M'|'Q'|'H'} [ecl='L']  Error correction: L holds the most, H survives the most damage.
 * @returns {{ version: number, size: number, ecl: string, mask: number, modules: boolean[][] } | null} null when the text is too long for any symbol.
 */
export function encodeQr(text, ecl = 'L') {
  if (!ECL[ecl]) throw new RangeError(`unknown error correction level ${ecl}`);
  const bytes = Array.from(new TextEncoder().encode(String(text)));
  let version = MIN_VERSION;
  for (;; version++) {
    if (version > MAX_VERSION) return null;
    const countBits = version <= 9 ? 8 : 16;
    if (4 + countBits + bytes.length * 8 <= dataCodewords(version, ecl) * 8 && bytes.length < (1 << countBits)) break;
  }
  const bits = [];
  const push = (value, count) => { for (let i = count - 1; i >= 0; i--) bits.push((value >>> i) & 1); };
  push(0x4, 4);                                            // byte mode
  push(bytes.length, version <= 9 ? 8 : 16);
  for (const b of bytes) push(b, 8);
  const capacityBits = dataCodewords(version, ecl) * 8;
  push(0, Math.min(4, capacityBits - bits.length));        // terminator
  push(0, (8 - (bits.length % 8)) % 8);
  for (let pad = 0xec; bits.length < capacityBits; pad ^= 0xec ^ 0x11) push(pad, 8);
  const codewords = new Array(bits.length / 8).fill(0);
  bits.forEach((b, i) => { codewords[i >>> 3] |= b << (7 - (i & 7)); });
  const symbol = new QrSymbol(version, ecl, codewords);
  return { version, size: symbol.size, ecl, mask: symbol.mask, modules: symbol.modules };
}

/**
 * The symbol as one SVG path (`d`), a run of dark modules at a time, on a grid where one module is one unit wide;
 * `border` units of light margin are added all round (the standard asks for 4).
 */
export function qrPath(qr, border = 4) {
  const parts = [];
  for (let y = 0; y < qr.size; y++) {
    let x = 0;
    while (x < qr.size) {
      if (!qr.modules[y][x]) { x++; continue; }
      let end = x;
      while (end < qr.size && qr.modules[y][end]) end++;
      parts.push(`M${x + border},${y + border}h${end - x}v1h-${end - x}z`);
      x = end;
    }
  }
  return parts.join('');
}

/** The symbol as lines of 0 and 1 (for tests and for looking at it). */
export function qrRows(qr) {
  return qr.modules.map((row) => row.map((v) => (v ? '1' : '0')).join(''));
}
