// Money strings such as "0.06 USD". Internal; not part of the public API.
//
// Amounts are kept as BigInt micro-units (millionths), never as floating point.
// The format allows at most 12 digits before the point and 6 after it.

const MONEY_RE = /^(0|[1-9][0-9]{0,11})(?:\.([0-9]{1,6}))? ([A-Z]{3})$/;
const MICROS = 1000000n;

/**
 * @typedef {{ micros: bigint, currency: string }} Money
 */

/**
 * Parse "<amount> <CUR>"; return null when the value is not valid money.
 * @param {unknown} value
 * @returns {Money | null}
 */
export function parseMoney(value) {
  if (typeof value !== 'string') return null;
  const match = MONEY_RE.exec(value);
  if (match === null) return null;
  const fraction = (match[2] === undefined ? '' : match[2]).padEnd(6, '0');
  return { micros: BigInt(match[1]) * MICROS + BigInt(fraction), currency: match[3] };
}

/** @param {string} currency @returns {Money} */
export function zero(currency) {
  return { micros: 0n, currency };
}

/** @param {Money} a @param {Money} b @returns {Money} */
export function addMoney(a, b) {
  if (a.currency !== b.currency) throw new RangeError(`cannot add ${b.currency} to ${a.currency}`);
  return { micros: a.micros + b.micros, currency: a.currency };
}

/**
 * Shortest form: "0.06 USD", "0 USD", "12 EUR".
 * @param {Money} money
 */
export function formatMoney(money) {
  const whole = money.micros / MICROS;
  const fraction = (money.micros % MICROS).toString().padStart(6, '0').replace(/0+$/, '');
  return `${whole}${fraction ? '.' + fraction : ''} ${money.currency}`;
}
