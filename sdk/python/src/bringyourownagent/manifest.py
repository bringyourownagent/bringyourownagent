"""Manifest rules M1-M21 of spec/rules-0.1-draft.md (a draft for discussion)."""

from __future__ import annotations

from typing import Any, List, Mapping, Optional, TypedDict

from ._common import (
    is_blank,
    is_count,
    is_non_negative,
    is_object,
    is_skill_id,
    pointer,
    present_keys,
    quote,
)
from ._money import parse_money
from .rules import (
    COST_PERIODS,
    COUNT_LIMITS,
    KNOWN_LIMITS,
    KNOWN_SKILLS,
    PRIVACY_CLASSES,
    SUMMARY_MAX_LENGTH,
    SUPPORTED_VERSIONS,
)

__all__ = ["Issue", "ValidationResult", "validate_manifest"]

_TOP_LEVEL_FIELDS = ("byoa", "purpose", "skills", "privacy", "expectedCost")
_PURPOSE_FIELDS = ("summary", "limits")


class Issue(TypedDict):
    """One problem found in a document."""

    code: str
    path: str
    message: str


class ValidationResult(TypedDict):
    ok: bool
    errors: List[Issue]
    warnings: List[Issue]


def limit_problem(name: str, value: Any) -> Optional[str]:
    """Why a known limit value is invalid, or None if it is fine."""
    if name in COUNT_LIMITS:
        if not is_count(value):
            return "limit %s must be a whole number from 0 to 9007199254740991" % quote(name)
    elif not is_non_negative(value):
        return "limit %s must be a number from 0 to 9007199254740991" % quote(name)
    return None


def version_problem(value: Any) -> Optional[str]:
    if value is None:
        return 'field "byoa" is missing; it names the protocol version, e.g. "0.1-draft"'
    if not (isinstance(value, str) and value in SUPPORTED_VERSIONS):
        return "protocol version %s is not supported; supported: %s" % (
            quote(value),
            ", ".join(quote(v) for v in SUPPORTED_VERSIONS),
        )
    return None


def validate_manifest(manifest: Any) -> ValidationResult:
    """Check a manifest against the 0.1-draft rules.

    Never raises for bad input: problems are returned as data. ``ok`` is True
    when there are no errors; warnings never make a manifest invalid.
    """
    errors: List[Issue] = []
    warnings: List[Issue] = []

    def error(code: str, path: str, message: str) -> None:
        errors.append({"code": code, "path": path, "message": message})

    def warn(code: str, path: str, message: str) -> None:
        warnings.append({"code": code, "path": path, "message": message})

    # M1
    if not is_object(manifest):
        error("manifest-not-object", "", "the manifest must be a JSON object")
        return {"ok": False, "errors": errors, "warnings": warnings}

    # M2, M3
    version = manifest.get("byoa")
    problem = version_problem(version)
    if problem is not None:
        error("version-missing" if version is None else "version-unsupported", "/byoa", problem)

    # M4-M9
    purpose = manifest.get("purpose")
    if not is_object(purpose):
        error("purpose-missing", "/purpose", 'field "purpose" is missing or is not an object')
    else:
        _check_purpose(purpose, error, warn)

    # M10-M15
    skills = manifest.get("skills")
    if not is_object(skills) or not present_keys(skills):
        error("skills-missing", "/skills", 'field "skills" must be an object with at least one skill')
    else:
        _check_skills(skills, error, warn)

    # M16, M17
    privacy = manifest.get("privacy")
    classes = ", ".join(PRIVACY_CLASSES)
    if privacy is None:
        error("privacy-missing", "/privacy", 'field "privacy" is missing; use one of: %s' % classes)
    elif not (isinstance(privacy, str) and privacy in PRIVACY_CLASSES):
        error(
            "privacy-unknown",
            "/privacy",
            "privacy class %s is unknown; use one of: %s" % (quote(privacy), classes),
        )

    # M18-M20
    cost = manifest.get("expectedCost")
    if cost is None:
        warn(
            "cost-missing",
            "/expectedCost",
            'field "expectedCost" is missing; visitors decide faster when they see an expected cost',
        )
    elif not is_object(cost):
        error("cost-invalid", "/expectedCost", '"expectedCost" must be an object such as {"perSession": "0.06 USD"}')
    else:
        keys = present_keys(cost)
        if not any(key in COST_PERIODS for key in keys):
            warn(
                "cost-missing",
                "/expectedCost",
                '"expectedCost" has none of: %s' % ", ".join(COST_PERIODS),
            )
        for key in keys:
            path = pointer("expectedCost", key)
            if key in COST_PERIODS:
                if parse_money(cost[key]) is None:
                    error(
                        "cost-invalid",
                        path,
                        '"expectedCost.%s" must be a money string such as "0.06 USD"' % key,
                    )
            else:
                warn(
                    "field-unknown",
                    path,
                    'unknown field %s in "expectedCost"; this version ignores it' % quote(key),
                )

    # M21
    for key in present_keys(manifest):
        if key not in _TOP_LEVEL_FIELDS:
            warn("field-unknown", pointer(key), "unknown field %s; this version ignores it" % quote(key))

    return {"ok": not errors, "errors": errors, "warnings": warnings}


def _check_purpose(purpose: Mapping, error: Any, warn: Any) -> None:
    summary = purpose.get("summary")
    if not isinstance(summary, str) or is_blank(summary):
        error(
            "summary-missing",
            "/purpose/summary",
            '"purpose.summary" must be a non-empty string: one short sentence for the visitor',
        )
    elif len(summary) > SUMMARY_MAX_LENGTH:
        error(
            "summary-too-long",
            "/purpose/summary",
            '"purpose.summary" has %d characters; the limit is %d' % (len(summary), SUMMARY_MAX_LENGTH),
        )

    limits = purpose.get("limits")
    if limits is not None:
        if not is_object(limits):
            error("limit-invalid", "/purpose/limits", '"purpose.limits" must be an object')
        else:
            for name in present_keys(limits):
                path = pointer("purpose", "limits", name)
                if name in KNOWN_LIMITS:
                    problem = limit_problem(name, limits[name])
                    if problem is not None:
                        error("limit-invalid", path, problem)
                else:
                    warn(
                        "limit-unknown",
                        path,
                        "unknown limit %s; wallets that do not know it will ignore it" % quote(name),
                    )

    for key in present_keys(purpose):
        if key not in _PURPOSE_FIELDS:
            warn(
                "field-unknown",
                pointer("purpose", key),
                'unknown field %s in "purpose"; this version ignores it' % quote(key),
            )


def _check_skills(skills: Mapping, error: Any, warn: Any) -> None:
    any_required = False
    for skill_id in present_keys(skills):
        path = pointer("skills", skill_id)
        if not is_skill_id(skill_id):
            error(
                "skill-id-invalid",
                path,
                'skill id %s must have the form domain.action in lower case, e.g. "audio.transcribe"'
                % quote(skill_id),
            )
        elif skill_id not in KNOWN_SKILLS:
            warn(
                "skill-unknown",
                path,
                "skill %s is not in the 0.1-draft vocabulary; check the spelling" % quote(skill_id),
            )
        entry = skills[skill_id]
        if not is_object(entry):
            error("skill-invalid", path, "skill %s must be an object of parameters" % quote(skill_id))
            continue
        required = entry.get("required")
        if not isinstance(required, bool):
            error(
                "skill-required-invalid",
                path + "/required",
                'skill %s must state "required": true or false' % quote(skill_id),
            )
        elif required:
            any_required = True
    if not any_required:
        warn(
            "no-required-skill",
            "/skills",
            'no skill is marked "required": true; the site claims to work without any of them',
        )
