// RFC 3339 timestamps. Internal; not part of the public API.
//
// Parsed by hand (not Date.parse, which accepts many other forms) so that every
// implementation accepts exactly the same strings and gets the same instant.

const TIMESTAMP_RE =
  /^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?(?:([Zz])|([+-])([0-9]{2}):([0-9]{2}))$/;

function isLeap(year) {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

function daysInMonth(year, month) {
  if (month === 2) return isLeap(year) ? 29 : 28;
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

/** Days since 1970-01-01 in the proleptic Gregorian calendar. */
function daysFromCivil(year, month, day) {
  const y = year - (month <= 2 ? 1 : 0);
  const era = Math.floor(y / 400);
  const yearOfEra = y - era * 400;
  const dayOfYear = Math.floor((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5) + day - 1;
  const dayOfEra = yearOfEra * 365 + Math.floor(yearOfEra / 4) - Math.floor(yearOfEra / 100) + dayOfYear;
  return era * 146097 + dayOfEra - 719468;
}

/**
 * Return milliseconds since the Unix epoch, or null if the value is not an
 * RFC 3339 date-time with an offset. Digits beyond milliseconds are cut off,
 * not rounded. Leap seconds are not accepted.
 * @param {unknown} value
 * @returns {number | null}
 */
export function parseTimestamp(value) {
  if (typeof value !== 'string') return null;
  const match = TIMESTAMP_RE.exec(value);
  if (match === null) return null;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  if (month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month)) return null;
  if (hour > 23 || minute > 59 || second > 59) return null;
  const millis = Number((match[7] === undefined ? '' : match[7]).padEnd(3, '0').slice(0, 3));
  let offset = 0;
  if (match[8] === undefined) {
    const offHour = Number(match[10]);
    const offMinute = Number(match[11]);
    if (offHour > 23 || offMinute > 59) return null;
    offset = (offHour * 60 + offMinute) * (match[9] === '+' ? 1 : -1);
  }
  const days = daysFromCivil(year, month, day);
  const totalMinutes = (days * 24 + hour) * 60 + minute - offset;
  return (totalMinutes * 60 + second) * 1000 + millis;
}
