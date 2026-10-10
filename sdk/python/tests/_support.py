"""Test helpers: make the source tree importable and load the shared vectors."""

from __future__ import annotations

import copy
import json
import sys
from pathlib import Path
from typing import Any, Dict

SDK_DIR = Path(__file__).resolve().parents[2]
SRC_DIR = SDK_DIR / "python" / "src"
VECTORS_DIR = SDK_DIR / "test-vectors"
EXAMPLES_DIR = SDK_DIR / "examples"

if str(SRC_DIR) not in sys.path:
    sys.path.insert(0, str(SRC_DIR))


def load_vectors(name: str) -> Dict[str, Any]:
    path = VECTORS_DIR / name
    if not path.is_file():
        raise FileNotFoundError(
            "shared test vectors not found at %s; run the tests from the repository" % path
        )
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def merge_patch(target: Any, patch: Any) -> Any:
    """JSON Merge Patch, RFC 7396."""
    if not isinstance(patch, dict):
        return copy.deepcopy(patch)
    result = copy.deepcopy(target) if isinstance(target, dict) else {}
    for key, value in patch.items():
        if value is None:
            result.pop(key, None)
        else:
            result[key] = merge_patch(result.get(key), value)
    return result


def case_document(case: Dict[str, Any], defaults: Dict[str, Any], name: str) -> Any:
    if name in case:
        return copy.deepcopy(case[name])
    if name + "Patch" in case:
        return merge_patch(defaults[name], case[name + "Patch"])
    return copy.deepcopy(defaults[name])
