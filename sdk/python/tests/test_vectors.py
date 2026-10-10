"""The shared test vectors in sdk/test-vectors: the contract with other implementations.

One test method is generated per vector case.
"""

import copy
import re
import unittest
from typing import Any, Callable, Dict

from _support import case_document, load_vectors

from bringyourownagent import DECISIONS, check_call, validate_manifest


def _slug(index: int, name: str) -> str:
    return "test_%03d_%s" % (index, re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")[:60])


def _manifest_test(case: Dict[str, Any], defaults: Dict[str, Any]) -> Callable[[unittest.TestCase], None]:
    def test(self: unittest.TestCase) -> None:
        result = validate_manifest(case_document(case, defaults, "manifest"))
        actual = {
            "ok": result["ok"],
            "errors": [{"code": i["code"], "path": i["path"]} for i in result["errors"]],
            "warnings": [{"code": i["code"], "path": i["path"]} for i in result["warnings"]],
        }
        self.assertEqual(actual, case["expect"])
        for issue in result["errors"] + result["warnings"]:
            self.assertIsInstance(issue["message"], str)
            self.assertTrue(issue["message"])

    return test


def _check_call_test(case: Dict[str, Any], defaults: Dict[str, Any]) -> Callable[[unittest.TestCase], None]:
    def test(self: unittest.TestCase) -> None:
        docs = [case_document(case, defaults, n) for n in ("manifest", "grant", "call", "state")]
        result = check_call(*docs)
        actual = {
            "decision": result["decision"],
            "reasons": [
                {"code": r["code"], "path": r["path"]} if "path" in r else {"code": r["code"]}
                for r in result["reasons"]
            ],
        }
        self.assertEqual(actual, case["expect"])
        for reason in result["reasons"]:
            self.assertIn(reason["decision"], DECISIONS)
            self.assertTrue(reason["message"])

    return test


def make_judge(spec: Any) -> Any:
    """Build the judge a purpose-judge.json case asks for."""
    if spec is None:
        return None
    if "returns" in spec:
        value = spec["returns"]
        return lambda manifest, grant, call: copy.deepcopy(value)
    if "throws" in spec:
        message = spec["throws"]

        def failing(manifest: Any, grant: Any, call: Any) -> Any:
            raise RuntimeError(message)

        return failing
    if "notCallable" in spec:
        return spec["notCallable"]
    raise ValueError("unknown judge spec %r" % (spec,))


def _judge_test(case: Dict[str, Any], defaults: Dict[str, Any]) -> Callable[[unittest.TestCase], None]:
    def test(self: unittest.TestCase) -> None:
        docs = [case_document(case, defaults, n) for n in ("manifest", "grant", "call", "state")]
        result = check_call(*docs, judge=make_judge(case.get("judge")))
        actual = {
            "decision": result["decision"],
            "reasons": [
                {"code": r["code"], "path": r["path"]} if "path" in r else {"code": r["code"]}
                for r in result["reasons"]
            ],
        }
        self.assertEqual(actual, case["expect"])

    return test


class ManifestVectors(unittest.TestCase):
    """validate-manifest.json"""


class PurposeJudgeVectors(unittest.TestCase):
    """purpose-judge.json"""


class CheckCallVectors(unittest.TestCase):
    """check-call.json"""


def _install(cls: type, file_name: str, factory: Callable[..., Any]) -> None:
    data = load_vectors(file_name)
    if not data["cases"]:
        raise AssertionError("%s has no cases" % file_name)
    for index, case in enumerate(data["cases"], start=1):
        test = factory(case, data["defaults"])
        test.__doc__ = case["name"]
        setattr(cls, _slug(index, case["name"]), test)


_install(ManifestVectors, "validate-manifest.json", _manifest_test)
_install(CheckCallVectors, "check-call.json", _check_call_test)
_install(PurposeJudgeVectors, "purpose-judge.json", _judge_test)


if __name__ == "__main__":
    unittest.main()
