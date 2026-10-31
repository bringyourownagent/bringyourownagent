"""Manifest rules M1-M28 of spec/rules-0.1-draft.md (a draft for discussion)."""

from __future__ import annotations

from typing import Any, List, Mapping, Optional, Sequence, TypedDict

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
    ABILITIES,
    COST_PERIODS,
    COUNT_LIMITS,
    KNOWN_LIMITS,
    KNOWN_SKILLS,
    MODALITIES,
    NEEDS_PROPERTIES,
    ON_MISMATCH,
    PRIVACY_CLASSES,
    RUNS,
    SOURCE_KINDS,
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
        models = entry.get("models")
        if models is not None:
            _check_models(skill_id, models, error, warn)
    if not any_required:
        warn(
            "no-required-skill",
            "/skills",
            'no skill is marked "required": true; the site claims to work without any of them',
        )


def _check_models(skill_id: str, models: Any, error: Any, warn: Any) -> None:
    """M22-M28: the ``models`` block of a skill. Only ``kind``, ``onMismatch``, ``needs`` and ``abilities``
    are checked here; ``count``, ``distinct``, ``require``, ``recommend``, ``why``, ``whyLong`` and ``names``
    stay the wallet's to read, and a block without ``count`` is valid (CONTRACT.md section 3.3)."""
    path = pointer("skills", skill_id, "models")
    if not is_object(models):
        error("models-invalid", path, '"models" of skill %s must be an object' % quote(skill_id))
        return

    kind = models.get("kind")
    if kind is not None and kind not in SOURCE_KINDS:
        error(
            "models-kind-invalid",
            path + "/kind",
            '"models.kind" must be one of: %s; got %s' % (", ".join(quote(k) for k in SOURCE_KINDS), quote(kind)),
        )

    on_mismatch = models.get("onMismatch")
    if on_mismatch is not None and on_mismatch not in ON_MISMATCH:
        error(
            "models-onmismatch-invalid",
            path + "/onMismatch",
            '"models.onMismatch" must be "drop" or "show"; got %s' % quote(on_mismatch),
        )

    needs = models.get("needs")
    if needs is not None:
        _check_needs(skill_id, needs, error, warn)

    abilities = models.get("abilities")
    if abilities is not None:
        _check_abilities(skill_id, abilities, error, warn)


def _check_needs(skill_id: str, needs: Any, error: Any, warn: Any) -> None:
    path = pointer("skills", skill_id, "models", "needs")
    if not is_object(needs):
        error("needs-invalid", path, '"models.needs" of skill %s must be an object of conditions by property' % quote(skill_id))
        return
    for key in present_keys(needs):
        key_path = path + pointer(key)
        value = needs[key]
        if key not in NEEDS_PROPERTIES:
            if not key.startswith("x-"):
                warn("needs-unknown", key_path, "unknown property %s in \"models.needs\"; wallets that do not know it will ignore it" % quote(key))
            continue
        problem = _needs_problem(key, value)
        if problem is not None:
            error("needs-invalid", key_path, problem)


def _needs_problem(key: str, value: Any) -> Optional[str]:
    """Why a condition of ``needs`` is invalid, or None if it is fine. An array is the short form of the
    one long form each property takes: ``{"includes": [...]}`` for a modality, ``{"oneOf": [...]}`` for
    ``runs``, ``{"min": n}`` for ``context`` (CONTRACT.md sections 3.3 and 3.5)."""
    if key in ("input", "output"):
        items = _list_of(value, "includes")
        if items is not None and _is_list(MODALITIES, items):
            return None
        return '"models.needs.%s" must be an array of %s with at least one value, or {"includes": [...]} of those; got %s' % (
            key,
            ", ".join(quote(m) for m in MODALITIES),
            quote(value),
        )
    if key == "runs":
        items = _list_of(value, "oneOf")
        if items is not None and _is_list(RUNS, items):
            return None
        return '"models.needs.%s" must be an array of %s with at least one value, or {"oneOf": [...]} of those; got %s' % (
            key,
            ", ".join(quote(r) for r in RUNS),
            quote(value),
        )
    # context: a minimum, in thousands of tokens
    if is_non_negative(value):
        return None
    if is_object(value) and len(present_keys(value)) == 1 and is_non_negative(value.get("min")):
        return None
    return '"models.needs.%s" must be a number of thousands of tokens (the minimum) or {"min": n}; got %s' % (key, quote(value))


def _list_of(value: Any, key: str) -> Optional[Any]:
    """The array of a short form, or of the one long form ``key``, or None when the value is neither."""
    if isinstance(value, (list, tuple)):
        return value
    if is_object(value) and len(present_keys(value)) == 1 and value.get(key) is not None:
        return value[key]
    return None


def _is_list(allowed: Sequence[str], items: Any) -> bool:
    """A non-empty array of strings, all of them in ``allowed``."""
    return bool(items) and all(isinstance(item, str) and item in allowed for item in items)


def _check_abilities(skill_id: str, abilities: Any, error: Any, warn: Any) -> None:
    path = pointer("skills", skill_id, "models", "abilities")
    if not isinstance(abilities, (list, tuple)) or not abilities or not all(isinstance(item, str) for item in abilities):
        error(
            "abilities-invalid",
            path,
            '"models.abilities" of skill %s must be an array of ability names, e.g. ["web.search"]' % quote(skill_id),
        )
        return
    for at, item in enumerate(abilities):
        if item not in ABILITIES and not item.startswith("x-"):
            warn(
                "abilities-unknown",
                path + pointer(str(at)),
                "ability %s is not in the 0.1 vocabulary; wallets that do not know it will ignore it" % quote(item),
            )
