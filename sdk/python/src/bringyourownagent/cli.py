"""The ``bringyourownagent`` command line tool.

    bringyourownagent validate manifest.json
    bringyourownagent check --manifest m.json --grant g.json --call c.json [--state s.json]

Also runnable as ``python -m bringyourownagent``.

Exit codes: 0 the manifest is valid / the call may proceed (allowed, warn);
1 the manifest is invalid / the call may not proceed (ask, blocked);
2 usage error or unreadable input.
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, List, Optional, Sequence, TextIO

from ._version import __version__
from .gate import check_call
from .manifest import validate_manifest

PROGRAM = "bringyourownagent"

HELP = """\
usage: bringyourownagent <command> [options]

Commands:
  validate <manifest.json>
      check a manifest against the BYOA 0.1-draft rules
  check --manifest <file> --grant <file> --call <file> [--state <file>]
      decide one call: allowed, warn, ask or blocked

Options:
  --json         print the result as JSON
  -h, --help     show this help
  -V, --version  show the version

Exit codes:
  0  the manifest is valid, or the call may proceed (allowed, warn)
  1  the manifest is invalid, or the call may not proceed (ask, blocked)
  2  usage error or unreadable input

Rules (draft): https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md
"""

_FILE_OPTIONS = ("--manifest", "--grant", "--call", "--state")


class _UsageError(Exception):
    pass


class _InputError(Exception):
    pass


def main(
    argv: Optional[Sequence[str]] = None,
    stdout: Optional[TextIO] = None,
    stderr: Optional[TextIO] = None,
) -> int:
    """Run the tool and return the exit code."""
    out = stdout if stdout is not None else _console(sys.stdout)
    err = stderr if stderr is not None else _console(sys.stderr)
    args = list(sys.argv[1:] if argv is None else argv)
    try:
        options = _parse(args)
        if options["help"]:
            out.write(HELP)
            return 0
        if options["version"]:
            out.write("%s %s\n" % (PROGRAM, __version__))
            return 0
        command = options["command"]
        if command == "validate":
            return _validate(options, out)
        if command == "check":
            return _check(options, out)
        raise _UsageError("unknown command %s" % json.dumps(command))
    except _UsageError as exc:
        err.write("%s: %s\nTry '%s --help'.\n" % (PROGRAM, exc, PROGRAM))
        return 2
    except _InputError as exc:
        err.write("%s: %s\n" % (PROGRAM, exc))
        return 2


def _parse(args: List[str]) -> Dict[str, Any]:
    options: Dict[str, Any] = {"help": False, "version": False, "json": False, "files": {}, "positional": []}
    index = 0
    while index < len(args):
        arg = args[index]
        index += 1
        if arg in ("-h", "--help"):
            options["help"] = True
        elif arg in ("-V", "--version"):
            options["version"] = True
        elif arg == "--json":
            options["json"] = True
        elif arg.split("=", 1)[0] in _FILE_OPTIONS:
            name, has_value, value = arg.partition("=")
            if not has_value:
                if index >= len(args):
                    raise _UsageError("option %s needs a file" % name)
                value = args[index]
                index += 1
            if not value:
                raise _UsageError("option %s needs a file" % name)
            options["files"][name[2:]] = value
        elif arg.startswith("-") and arg != "-":
            raise _UsageError("unknown option %s" % arg)
        else:
            options["positional"].append(arg)
    positional = options["positional"]
    options["command"] = positional[0] if positional else None
    if options["command"] is None and not (options["help"] or options["version"]):
        raise _UsageError("missing command")
    return options


def _validate(options: Dict[str, Any], out: TextIO) -> int:
    files = options["positional"][1:]
    if len(files) != 1 or options["files"]:
        raise _UsageError("validate takes exactly one file: %s validate manifest.json" % PROGRAM)
    path = files[0]
    result = validate_manifest(_load(path))
    if options["json"]:
        out.write(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
    else:
        errors, warnings = result["errors"], result["warnings"]
        counts = []
        if errors:
            counts.append(_plural(len(errors), "error"))
        if warnings:
            counts.append(_plural(len(warnings), "warning"))
        status = "valid" if result["ok"] else "invalid"
        out.write("%s: %s\n" % (path, ", ".join([status] + counts)))
        for level, issues in (("error", errors), ("warning", warnings)):
            for issue in issues:
                out.write("  %s %s at %s: %s\n" % (level, issue["code"], _show_path(issue["path"]), issue["message"]))
    return 0 if result["ok"] else 1


def _check(options: Dict[str, Any], out: TextIO) -> int:
    if len(options["positional"]) != 1:
        raise _UsageError(
            "check takes its files as options: %s check --manifest m.json --grant g.json --call c.json" % PROGRAM
        )
    files = options["files"]
    for name in ("manifest", "grant", "call"):
        if name not in files:
            raise _UsageError("check needs --%s <file>" % name)
    manifest = _load(files["manifest"])
    grant = _load(files["grant"])
    call = _load(files["call"])
    state = _load(files["state"]) if "state" in files else None
    result = check_call(manifest, grant, call, state)
    if options["json"]:
        out.write(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
    else:
        out.write("decision: %s\n" % result["decision"])
        for reason in result["reasons"]:
            where = " at %s" % _show_path(reason["path"]) if "path" in reason else ""
            out.write("  %s %s%s: %s\n" % (reason["decision"], reason["code"], where, reason["message"]))
    return 0 if result["decision"] in ("allowed", "warn") else 1


def _load(path: str) -> Any:
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except OSError as exc:
        raise _InputError("cannot read %s: %s" % (path, exc.strerror or exc)) from None
    except UnicodeDecodeError:
        raise _InputError("%s is not UTF-8 text" % path) from None
    try:
        return json.loads(text, parse_constant=_reject_constant)
    except ValueError as exc:
        raise _InputError("%s is not valid JSON: %s" % (path, exc)) from None


def _reject_constant(name: str) -> Any:
    raise ValueError("%s is not allowed in JSON" % name)


def _show_path(path: str) -> str:
    return path if path else "(root)"


def _plural(count: int, word: str) -> str:
    return "%d %s%s" % (count, word, "" if count == 1 else "s")


def _console(stream: TextIO) -> TextIO:
    # Never crash on a console that cannot show some character.
    reconfigure = getattr(stream, "reconfigure", None)
    if callable(reconfigure):
        try:
            reconfigure(errors="backslashreplace")
        except (ValueError, OSError):
            pass
    return stream
