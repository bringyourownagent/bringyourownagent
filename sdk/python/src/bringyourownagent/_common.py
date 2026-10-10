"""Small helpers shared by the validators. Internal; not part of the public API."""

from __future__ import annotations

import json
import math
import re
from collections.abc import Mapping
from typing import Any, List

from .rules import SKILL_ID_MAX_LENGTH

# Unicode White_Space, spelled out so that every implementation agrees exactly.
_BLANK_RE = re.compile(
    "[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*"
)

_SEGMENT = "[a-z][a-z0-9]*(?:-[a-z0-9]+)*"
_SKILL_ID_RE = re.compile(_SEGMENT + r"\." + _SEGMENT)


def is_object(value: Any) -> bool:
    """A JSON object (a mapping)."""
    return isinstance(value, Mapping)


def is_array(value: Any) -> bool:
    """A JSON array."""
    return isinstance(value, (list, tuple))


def is_number(value: Any) -> bool:
    """A finite JSON number. Booleans are not numbers."""
    if isinstance(value, bool):
        return False
    if isinstance(value, int):
        return True
    if isinstance(value, float):
        return math.isfinite(value)
    return False


#: The largest number accepted anywhere: 2**53 - 1, the largest integer every
#: JSON implementation reads exactly. Larger values are invalid, so all
#: implementations compare the same numbers the same way.
MAX_NUMBER = 9007199254740991


def is_non_negative(value: Any) -> bool:
    """A number from 0 to MAX_NUMBER."""
    return is_number(value) and 0 <= value <= MAX_NUMBER


def is_count(value: Any) -> bool:
    """A whole number from 0 to MAX_NUMBER; 20.0 counts as whole."""
    if not is_non_negative(value):
        return False
    return isinstance(value, int) or float(value).is_integer()


def is_blank(text: str) -> bool:
    return _BLANK_RE.fullmatch(text) is not None


def is_skill_id(value: Any) -> bool:
    return (
        isinstance(value, str)
        and len(value) <= SKILL_ID_MAX_LENGTH
        and _SKILL_ID_RE.fullmatch(value) is not None
    )


def present_keys(obj: Mapping) -> List[Any]:
    """Keys whose value is not null, in Unicode code point order.

    A field whose value is null is treated as absent.
    """
    return sorted((key for key, value in obj.items() if value is not None), key=str)


def pointer(*parts: Any) -> str:
    """Build a JSON Pointer (RFC 6901) from path segments."""
    return "".join("/" + str(part).replace("~", "~0").replace("/", "~1") for part in parts)


def quote(value: Any) -> str:
    """Show a JSON value inside a message."""
    try:
        return json.dumps(value, ensure_ascii=False)
    except (TypeError, ValueError):
        return repr(value)


def format_number(value: Any) -> str:
    if isinstance(value, float) and value.is_integer() and abs(value) < 1e21:
        return str(int(value))
    return str(value)
