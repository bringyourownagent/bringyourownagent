"""Python-specific unit tests: money, timestamps, the purpose judge hook, the API."""

import asyncio
import copy
import decimal
import re
import unittest
from decimal import Decimal

from _support import SDK_DIR, load_vectors

import bringyourownagent
from bringyourownagent import apply_opinion, check_call, no_opinion, validate_manifest
from bringyourownagent._money import Money, add_money, format_money, parse_money
from bringyourownagent._time import parse_timestamp

DEFAULTS = load_vectors("check-call.json")["defaults"]


def docs(**changes):
    result = copy.deepcopy(DEFAULTS)
    result.update(changes)
    return result["manifest"], result["grant"], result["call"], result["state"]


class MoneyTests(unittest.TestCase):
    def test_parse(self) -> None:
        self.assertEqual(parse_money("0.06 USD"), Money(Decimal("0.06"), "USD"))
        self.assertEqual(parse_money("0 EUR"), Money(Decimal("0"), "EUR"))
        self.assertIsNone(parse_money("0.06 USD\n"))
        self.assertIsNone(parse_money(0.06))
        self.assertIsNone(parse_money("\u0661 USD"))  # Arabic-Indic digit one is not [0-9]

    def test_sums_are_exact(self) -> None:
        total = add_money(parse_money("0.1 USD"), parse_money("0.2 USD"))
        self.assertEqual(total.amount, Decimal("0.3"))
        biggest = parse_money("999999999999.999999 USD")
        self.assertEqual(add_money(biggest, biggest).amount, Decimal("1999999999999.999998"))

    def test_independent_of_the_callers_decimal_context(self) -> None:
        with decimal.localcontext() as ctx:
            ctx.prec = 3
            total = add_money(parse_money("123.456 USD"), parse_money("0.001 USD"))
        self.assertEqual(total.amount, Decimal("123.457"))

    def test_currencies_never_mix(self) -> None:
        with self.assertRaises(ValueError):
            add_money(parse_money("1 USD"), parse_money("1 EUR"))

    def test_format(self) -> None:
        self.assertEqual(format_money(parse_money("0.060 USD")), "0.06 USD")
        self.assertEqual(format_money(parse_money("12.000 EUR")), "12 EUR")
        self.assertEqual(format_money(parse_money("0 USD")), "0 USD")


class TimestampTests(unittest.TestCase):
    def test_epoch_and_offsets(self) -> None:
        self.assertEqual(parse_timestamp("1970-01-01T00:00:00Z"), 0)
        self.assertEqual(parse_timestamp("1970-01-01T01:00:00+01:00"), 0)
        self.assertEqual(parse_timestamp("1969-12-31T23:59:59.999Z"), -1)
        self.assertEqual(parse_timestamp("2026-10-10t12:00:00z"), parse_timestamp("2026-10-10T12:00:00Z"))

    def test_matches_known_instant(self) -> None:
        # 2026-10-10T12:00:00Z, computed independently
        self.assertEqual(parse_timestamp("2026-10-10T12:00:00Z"), 1791633600000)

    def test_rejects(self) -> None:
        for bad in (
            "2026-10-10T12:00:00",
            "2026-10-10 12:00:00Z",
            "2026-13-01T00:00:00Z",
            "2026-02-29T00:00:00Z",
            "2026-10-10T24:00:00Z",
            "2026-10-10T23:59:60Z",
            "2026-10-10T12:00:00+24:00",
            "2026-10-10T12:00:00.Z",
            None,
            1791633600000,
        ):
            with self.subTest(bad):
                self.assertIsNone(parse_timestamp(bad))

    def test_cuts_extra_fraction_digits(self) -> None:
        self.assertEqual(parse_timestamp("1970-01-01T00:00:00.123999999Z"), 123)


class JudgeTests(unittest.TestCase):
    def test_default_judge_has_no_opinion(self) -> None:
        self.assertIsNone(no_opinion({}, {}, {}))
        self.assertEqual(check_call(*docs(), judge=no_opinion), check_call(*docs()))

    def test_judge_can_escalate(self) -> None:
        def doubtful(manifest, grant, call):
            return {"decision": "ask", "message": "this does not look like a game"}

        result = check_call(*docs(), judge=doubtful)
        self.assertEqual(result["decision"], "ask")
        self.assertEqual(result["reasons"][-1]["code"], "purpose-doubt")

    def test_judge_cannot_relax(self) -> None:
        manifest, grant, call, state = docs()
        state["spent"] = "0.199 USD"
        result = check_call(manifest, grant, call, state, judge=lambda *a: {"decision": "allowed"})
        self.assertEqual(result["decision"], "ask")
        result = check_call(manifest, grant, call, state, judge=lambda *a: {"decision": "warn", "code": "odd"})
        self.assertEqual(result["decision"], "ask")
        self.assertEqual([r["code"] for r in result["reasons"]], ["budget-exceeded", "odd"])

    def test_judge_is_not_consulted_when_blocked(self) -> None:
        calls = []
        manifest, grant, call, state = docs()
        call["input"]["clipSeconds"] = 42
        check_call(manifest, grant, call, state, judge=lambda *a: calls.append(a))
        self.assertEqual(calls, [])

    def test_failing_judge_means_ask(self) -> None:
        def broken(manifest, grant, call):
            raise RuntimeError("model offline")

        result = check_call(*docs(), judge=broken)
        self.assertEqual(result["decision"], "ask")
        self.assertEqual(result["reasons"][-1]["code"], "purpose-judge-error")

    def test_invalid_opinion_means_ask(self) -> None:
        for bad in ({"decision": "maybe"}, "blocked", {"decision": "warn", "code": ""}, {"decision": "warn", "message": 3}):
            with self.subTest(bad):
                result = check_call(*docs(), judge=lambda *a, bad=bad: bad)
                self.assertEqual(result["decision"], "ask")
                self.assertEqual(result["reasons"][-1]["code"], "purpose-judge-error")

    def test_async_judge_is_refused_but_apply_opinion_works(self) -> None:
        async def async_judge(manifest, grant, call):
            return {"decision": "warn", "code": "purpose-unclear"}

        result = check_call(*docs(), judge=async_judge)
        self.assertEqual(result["reasons"][-1]["code"], "purpose-judge-error")

        base = check_call(*docs())
        merged = apply_opinion(base, asyncio.run(async_judge(*docs()[:3])))
        self.assertEqual(merged["decision"], "warn")
        self.assertEqual(merged["reasons"][-1]["code"], "purpose-unclear")
        self.assertEqual(base["reasons"], [])  # apply_opinion does not mutate


class ApiTests(unittest.TestCase):
    def test_never_raises_on_odd_input(self) -> None:
        for odd in (None, 1, "x", [], {}, {"skills": {1: 2}}, {"purpose": {"summary": None}}):
            with self.subTest(odd):
                self.assertFalse(validate_manifest(odd)["ok"])
                self.assertEqual(check_call(odd, odd, odd, odd)["decision"], "blocked")

    def test_inputs_are_not_mutated(self) -> None:
        manifest, grant, call, state = docs()
        before = copy.deepcopy((manifest, grant, call, state))
        check_call(manifest, grant, call, state)
        validate_manifest(manifest)
        self.assertEqual((manifest, grant, call, state), before)

    def test_version_matches_pyproject(self) -> None:
        text = (SDK_DIR / "python" / "pyproject.toml").read_text(encoding="utf-8")
        self.assertIn('path = "src/bringyourownagent/_version.py"', text)
        self.assertRegex(bringyourownagent.__version__, r"^\d+\.\d+\.\d+$")
        package_json = (SDK_DIR / "js" / "package.json").read_text(encoding="utf-8")
        js_version = re.search(r'"version":\s*"([^"]+)"', package_json).group(1)
        self.assertEqual(bringyourownagent.__version__, js_version)

    def test_types(self) -> None:
        from typing import get_args

        from bringyourownagent import Decision, Opinion, Reason

        self.assertEqual(Opinion.__required_keys__, frozenset({"decision"}))
        self.assertEqual(Opinion.__optional_keys__, frozenset({"code", "message"}))
        self.assertEqual(Reason.__required_keys__, frozenset({"code", "decision", "message"}))
        self.assertEqual(get_args(Decision), ("allowed", "warn", "ask", "blocked"))

    def test_public_names(self) -> None:
        for name in bringyourownagent.__all__:
            self.assertTrue(hasattr(bringyourownagent, name), name)


if __name__ == "__main__":
    unittest.main()
