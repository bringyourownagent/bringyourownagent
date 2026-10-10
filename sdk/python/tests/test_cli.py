"""The bringyourownagent command line tool, run in-process on the files in sdk/examples."""

import io
import json
import os
import subprocess
import sys
import unittest

from _support import EXAMPLES_DIR, SRC_DIR, VECTORS_DIR

from bringyourownagent.cli import main


def run(*args):
    out, err = io.StringIO(), io.StringIO()
    code = main(list(args), stdout=out, stderr=err)
    return code, out.getvalue(), err.getvalue()


def example(name: str) -> str:
    return str(EXAMPLES_DIR / name)


class ValidateCommand(unittest.TestCase):
    def test_valid_manifest(self) -> None:
        code, out, err = run("validate", example("manifest.json"))
        self.assertEqual(code, 0)
        self.assertTrue(out.endswith("manifest.json: valid\n"), out)
        self.assertEqual(err, "")

    def test_invalid_manifest_lists_problems(self) -> None:
        code, out, _ = run("validate", example("manifest-invalid.json"))
        self.assertEqual(code, 1)
        self.assertIn("invalid, 4 errors, 2 warnings", out)
        self.assertIn("  error privacy-unknown at /privacy: ", out)
        self.assertIn("  warning limit-unknown at /purpose/limits/maxTokens: ", out)

    def test_json_output(self) -> None:
        code, out, _ = run("validate", "--json", example("manifest-invalid.json"))
        self.assertEqual(code, 1)
        result = json.loads(out)
        self.assertFalse(result["ok"])
        self.assertEqual(
            [e["code"] for e in result["errors"]],
            ["limit-invalid", "skill-required-invalid", "privacy-unknown", "cost-invalid"],
        )

    def test_missing_file(self) -> None:
        code, out, err = run("validate", example("no-such-file.json"))
        self.assertEqual(code, 2)
        self.assertEqual(out, "")
        self.assertIn("cannot read", err)

    def test_not_json(self) -> None:
        code, _, err = run("validate", str(VECTORS_DIR / "README.md"))
        self.assertEqual(code, 2)
        self.assertIn("is not valid JSON", err)


class CheckCommand(unittest.TestCase):
    def check(self, call: str, *extra: str):
        return run(
            "check",
            "--manifest", example("manifest.json"),
            "--grant=" + example("grant.json"),
            "--call", example(call),
            "--state", example("state.json"),
            *extra,
        )

    def test_allowed(self) -> None:
        code, out, _ = self.check("call.json")
        self.assertEqual((code, out), (0, "decision: allowed\n"))

    def test_blocked(self) -> None:
        code, out, _ = self.check("call-long-clip.json")
        self.assertEqual(code, 1)
        self.assertEqual(
            out,
            "decision: blocked\n  blocked clip-too-long: the clip is 42 seconds; the limit is 15\n",
        )

    def test_warn_still_proceeds(self) -> None:
        code, out, _ = self.check("call-text.json", "--json")
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["decision"], "warn")

    def test_state_is_optional(self) -> None:
        code, out, _ = run(
            "check", "--manifest", example("manifest.json"), "--grant", example("grant.json"),
            "--call", example("call.json"),
        )
        self.assertEqual((code, out), (0, "decision: allowed\n"))


class Usage(unittest.TestCase):
    def test_help_and_version(self) -> None:
        code, out, _ = run("--help")
        self.assertEqual(code, 0)
        self.assertIn("usage: bringyourownagent <command>", out)
        code, out, _ = run("-V")
        self.assertEqual((code, out.startswith("bringyourownagent 0.")), (0, True))

    def test_errors(self) -> None:
        for args in (
            (),
            ("frobnicate",),
            ("validate",),
            ("validate", "a.json", "b.json"),
            ("validate", "--bogus", "a.json"),
            ("check", "--manifest", example("manifest.json")),
            ("check", "--call"),
        ):
            with self.subTest(args):
                code, out, err = run(*args)
                self.assertEqual(code, 2)
                self.assertEqual(out, "")
                self.assertTrue(err.startswith("bringyourownagent: "), err)

    def test_python_dash_m(self) -> None:
        env = dict(os.environ, PYTHONPATH=str(SRC_DIR), PYTHONDONTWRITEBYTECODE="1")
        completed = subprocess.run(
            [sys.executable, "-m", "bringyourownagent", "validate", example("manifest.json")],
            capture_output=True,
            text=True,
            env=env,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn(": valid", completed.stdout)


if __name__ == "__main__":
    unittest.main()
