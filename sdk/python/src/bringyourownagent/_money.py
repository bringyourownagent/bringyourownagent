"""Money strings such as "0.06 USD". Internal; not part of the public API.

Amounts are Decimal, never float. The format allows at most 12 digits before
the point and 6 after it, so every sum the gate computes is exact.
"""

from __future__ import annotations

import re
from decimal import Context, Decimal, Inexact
from typing import Any, NamedTuple, Optional

_MONEY_RE = re.compile(r"(0|[1-9][0-9]{0,11})(?:\.([0-9]{1,6}))? ([A-Z]{3})")

# A private context, independent of the caller's decimal settings. Inexact is
# trapped: with the bounded format above no sum can ever need rounding.
_EXACT = Context(prec=40, traps=[Inexact])


class Money(NamedTuple):
    amount: Decimal
    currency: str


def parse_money(value: Any) -> Optional[Money]:
    """Parse "<amount> <CUR>"; return None when the value is not valid money."""
    if not isinstance(value, str):
        return None
    match = _MONEY_RE.fullmatch(value)
    if match is None:
        return None
    whole, fraction, currency = match.groups()
    text = whole if fraction is None else whole + "." + fraction
    return Money(Decimal(text), currency)


def zero(currency: str) -> Money:
    return Money(Decimal(0), currency)


def add_money(a: Money, b: Money) -> Money:
    if a.currency != b.currency:
        raise ValueError("cannot add %s to %s" % (b.currency, a.currency))
    return Money(_EXACT.add(a.amount, b.amount), a.currency)


def format_money(money: Money) -> str:
    """Shortest form: "0.06 USD", "0 USD", "12 EUR"."""
    text = format(money.amount, "f")
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    return "%s %s" % (text, money.currency)
