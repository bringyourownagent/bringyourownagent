"""BYOA (Bring Your Own Agent): an early draft reference implementation.

Two things for now, both following spec/rules-0.1-draft.md (a draft for
discussion):

* ``validate_manifest(manifest)`` checks a requester's manifest and returns
  ``{"ok", "errors", "warnings"}``. Problems are data, never exceptions.
* ``check_call(manifest, grant, call, state)`` is the gate for one call:
  it returns ``{"decision", "reasons"}`` where the decision is one of
  ``allowed``, ``warn``, ``ask``, ``blocked``.

https://bringyourownagent.org
"""

from ._version import __version__
from .gate import (
    CheckResult,
    Decision,
    Opinion,
    PurposeJudge,
    Reason,
    apply_opinion,
    check_call,
    no_opinion,
)
from .manifest import Issue, ValidationResult, validate_manifest
from .rules import (
    ABILITIES,
    COST_PERIODS,
    COUNT_LIMITS,
    DECISIONS,
    KNOWN_LIMITS,
    KNOWN_SKILLS,
    LOCAL_PROVIDERS,
    MODALITIES,
    NEEDS_PROPERTIES,
    ON_MISMATCH,
    PRIVACY_CLASSES,
    PROVIDER_KINDS,
    RUNS,
    SOURCE_KINDS,
    SUPPORTED_VERSIONS,
)

__all__ = [
    "ABILITIES",
    "COST_PERIODS",
    "COUNT_LIMITS",
    "CheckResult",
    "DECISIONS",
    "Decision",
    "Issue",
    "KNOWN_LIMITS",
    "KNOWN_SKILLS",
    "LOCAL_PROVIDERS",
    "MODALITIES",
    "NEEDS_PROPERTIES",
    "ON_MISMATCH",
    "Opinion",
    "PRIVACY_CLASSES",
    "PROVIDER_KINDS",
    "PurposeJudge",
    "Reason",
    "RUNS",
    "SUPPORTED_VERSIONS",
    "SOURCE_KINDS",
    "ValidationResult",
    "__version__",
    "apply_opinion",
    "check_call",
    "no_opinion",
    "validate_manifest",
]
