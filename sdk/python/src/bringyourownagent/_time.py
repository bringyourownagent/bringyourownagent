"""RFC 3339 timestamps. Internal; not part of the public API.

Parsed by hand (not datetime.fromisoformat) so that Python 3.9 and every other
implementation accept exactly the same strings and get the same instant.
"""

from __future__ import annotations

import re
from typing import Any, Optional

_TIMESTAMP_RE = re.compile(
    r"([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})"
    r"(?:\.([0-9]+))?(?:([Zz])|([+-])([0-9]{2}):([0-9]{2}))"
)


def _is_leap(year: int) -> bool:
    return year % 4 == 0 and (year % 100 != 0 or year % 400 == 0)


def _days_in_month(year: int, month: int) -> int:
    if month == 2:
        return 29 if _is_leap(year) else 28
    return 30 if month in (4, 6, 9, 11) else 31


def _days_from_civil(year: int, month: int, day: int) -> int:
    """Days since 1970-01-01 in the proleptic Gregorian calendar."""
    year -= 1 if month <= 2 else 0
    era = year // 400  # floor division
    year_of_era = year - era * 400
    day_of_year = (153 * (month + (-3 if month > 2 else 9)) + 2) // 5 + day - 1
    day_of_era = year_of_era * 365 + year_of_era // 4 - year_of_era // 100 + day_of_year
    return era * 146097 + day_of_era - 719468


def parse_timestamp(value: Any) -> Optional[int]:
    """Return milliseconds since the Unix epoch, or None if the value is not
    an RFC 3339 date-time with an offset. Digits beyond milliseconds are cut
    off, not rounded. Leap seconds are not accepted."""
    if not isinstance(value, str):
        return None
    match = _TIMESTAMP_RE.fullmatch(value)
    if match is None:
        return None
    year, month, day, hour, minute, second = (int(g) for g in match.groups()[:6])
    fraction, zulu, sign, off_hour, off_minute = match.groups()[6:]
    if not 1 <= month <= 12 or not 1 <= day <= _days_in_month(year, month):
        return None
    if hour > 23 or minute > 59 or second > 59:
        return None
    millis = int((fraction or "").ljust(3, "0")[:3])
    offset = 0
    if zulu is None:
        oh, om = int(off_hour), int(off_minute)
        if oh > 23 or om > 59:
            return None
        offset = (oh * 60 + om) * (1 if sign == "+" else -1)
    days = _days_from_civil(year, month, day)
    total_minutes = (days * 24 + hour) * 60 + minute - offset
    return (total_minutes * 60 + second) * 1000 + millis
