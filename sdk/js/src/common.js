// Small helpers shared by the validators. Internal; not part of the public API.

import { SKILL_ID_MAX_LENGTH } from './rules.js';

// Unicode White_Space, spelled out so that every implementation agrees exactly.
const BLANK_RE = /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/;

const SEGMENT = '[a-z][a-z0-9]*(?:-[a-z0-9]+)*';
const SKILL_ID_RE = new RegExp(`^${SEGMENT}\\.${SEGMENT}$`);

const hasOwn = (obj, key) => Object.prototype.hasOwnProperty.call(obj, key);

/** A JSON object (not null, not an array). */
export function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** A JSON array. */
export function isArray(value) {
  return Array.isArray(value);
}

/** A finite JSON number. Booleans are not numbers. */
export function isNumber(value) {
  return typeof value === 'number' && Number.isFinite(value);
}

/**
 * The largest number accepted anywhere: 2**53 - 1, the largest integer every
 * JSON implementation reads exactly. Larger values are invalid, so all
 * implementations compare the same numbers the same way.
 */
export const MAX_NUMBER = 9007199254740991;

/** A number from 0 to MAX_NUMBER. */
export function isNonNegative(value) {
  return isNumber(value) && value >= 0 && value <= MAX_NUMBER;
}

/** A whole number from 0 to MAX_NUMBER; 20.0 counts as whole. */
export function isCount(value) {
  return isNonNegative(value) && Number.isInteger(value);
}

export function isBlank(text) {
  return BLANK_RE.test(text);
}

export function isSkillId(value) {
  return typeof value === 'string' && value.length <= SKILL_ID_MAX_LENGTH && SKILL_ID_RE.test(value);
}

/** Number of Unicode code points (not UTF-16 units). */
export function codePointLength(text) {
  let count = 0;
  // eslint-disable-next-line no-unused-vars
  for (const _ of text) count += 1;
  return count;
}

/**
 * Own field of a JSON object. A field whose value is null is treated as absent:
 * both give undefined.
 */
export function get(obj, key) {
  if (!hasOwn(obj, key)) return undefined;
  const value = obj[key];
  return value === null ? undefined : value;
}

/** Compare two strings by Unicode code points (JS `<` compares UTF-16 units). */
export function compareCodePoints(a, b) {
  const left = a[Symbol.iterator]();
  const right = b[Symbol.iterator]();
  for (;;) {
    const x = left.next();
    const y = right.next();
    if (x.done || y.done) return (x.done ? 0 : 1) - (y.done ? 0 : 1);
    const diff = x.value.codePointAt(0) - y.value.codePointAt(0);
    if (diff !== 0) return diff;
  }
}

/** Keys whose value is not null, in Unicode code point order. */
export function presentKeys(obj) {
  return Object.keys(obj)
    .filter((key) => obj[key] !== null && obj[key] !== undefined)
    .sort(compareCodePoints);
}

/** Build a JSON Pointer (RFC 6901) from path segments. */
export function pointer(...parts) {
  return parts.map((part) => '/' + String(part).replace(/~/g, '~0').replace(/\//g, '~1')).join('');
}

/** Show a JSON value inside a message. */
export function quote(value) {
  try {
    const text = JSON.stringify(value);
    return text === undefined ? String(value) : text;
  } catch {
    return String(value);
  }
}

export function formatNumber(value) {
  return String(value);
}
