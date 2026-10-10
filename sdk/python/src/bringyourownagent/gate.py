"""The call gate: rules C1-C16 of spec/rules-0.1-draft.md (a draft for discussion).

``check_call`` checks the hard limits that code can check, reliably and for
free. Judging the declared purpose in words needs a model; that part plugs in
through the purpose judge hook (``judge=`` or ``apply_opinion``) and has no
opinion by default.

``check_call`` only evaluates. It does not run the call and does not update
the state; see "Execution contract" in the rules draft.
"""

from __future__ import annotations

import inspect
from typing import (
    Any,
    Callable,
    Dict,
    FrozenSet,
    List,
    Literal,
    Mapping,
    NamedTuple,
    Optional,
    Tuple,
    TypedDict,
)

from ._common import (
    format_number,
    is_array,
    is_count,
    is_non_negative,
    is_object,
    is_skill_id,
    pointer,
    present_keys,
    quote,
)
from ._money import Money, add_money, format_money, parse_money, zero
from ._time import parse_timestamp
from .manifest import limit_problem, validate_manifest, version_problem
from .rules import (
    DECISIONS,
    KNOWN_LIMITS,
    LOCAL_PROVIDERS,
    PROVIDER_KINDS,
    RATE_WINDOW_MS,
)

__all__ = [
    "CheckResult",
    "Decision",
    "Opinion",
    "PurposeJudge",
    "Reason",
    "apply_opinion",
    "check_call",
    "no_opinion",
]

#: One of the four decisions of the gate, mildest first.
Decision = Literal["allowed", "warn", "ask", "blocked"]

_GRANT_FIELDS = ("byoa", "skills", "budget", "expiresAt", "limits", "localOnly", "warnIf")
_STATE_FIELDS = ("spent", "callTimes")
_SEVERITY = {decision: rank for rank, decision in enumerate(DECISIONS)}
_REFUSED = "this version cannot enforce it, so the grant is refused"


class _ReasonBase(TypedDict):
    code: str
    decision: Decision
    message: str


class Reason(_ReasonBase, total=False):
    """Why the gate decided as it did. ``path`` is present only on the
    input-checking reasons (manifest-invalid, grant-invalid, call-invalid,
    state-invalid) and points into that document."""

    path: str


class CheckResult(TypedDict):
    decision: Decision
    reasons: List[Reason]


class _OpinionBase(TypedDict):
    decision: Decision


class Opinion(_OpinionBase, total=False):
    """What a purpose judge thinks of one call. ``decision`` is required;
    ``code`` defaults to "purpose-doubt"."""

    code: str
    message: str


#: A purpose judge: ``judge(manifest, grant, call)`` returns an Opinion or None.
PurposeJudge = Callable[[Any, Any, Any], Optional[Opinion]]


def no_opinion(manifest: Any, grant: Any, call: Any) -> Optional[Opinion]:
    """The default purpose judge: it never has an opinion."""
    return None


class _Grant(NamedTuple):
    skills: FrozenSet[str]
    budget: Money
    expires_at: int
    expires_text: str
    limits: Dict[str, Any]
    local_only: bool
    warn_sends_text: bool
    warn_call_cost: Optional[Money]
    warn_spend: Optional[Money]


class _Call(NamedTuple):
    skill: str
    at: int
    provider: str
    cost: Money
    clip_seconds: Any
    text_chars: Any


class _State(NamedTuple):
    spent: Optional[Money]
    call_times: List[int]


_Problems = List[Tuple[str, str]]


def check_call(
    manifest: Any,
    grant: Any,
    call: Any,
    state: Any = None,
    *,
    judge: Any = None,
) -> CheckResult:
    """Decide one call: ``allowed``, ``warn``, ``ask`` or ``blocked``.

    ``state`` may be None (nothing spent, no calls yet). ``judge`` is the
    purpose judge hook: None means no judge; anything else must be a
    synchronous callable, otherwise the result is ``ask``. For an asynchronous
    judge, call this without one, await your judge, then use ``apply_opinion``.
    Never raises for bad input: invalid documents block the call.
    """
    reasons: List[Reason] = []

    # Step 1: inputs (C1-C4).
    for issue in validate_manifest(manifest)["errors"]:
        reasons.append(
            _reason("manifest-invalid", "blocked", "%s: %s" % (issue["code"], issue["message"]), issue["path"])
        )
    parsed_grant, problems = _read_grant(grant)
    reasons.extend(_reason("grant-invalid", "blocked", message, path) for path, message in problems)
    parsed_call, problems = _read_call(call)
    reasons.extend(_reason("call-invalid", "blocked", message, path) for path, message in problems)
    parsed_state, problems = _read_state(state)
    reasons.extend(_reason("state-invalid", "blocked", message, path) for path, message in problems)
    if reasons or parsed_grant is None or parsed_call is None or parsed_state is None:
        return _result(reasons)

    # Step 1b: measurements that a limit or a warning in force needs (C3).
    limits = _effective_limits(manifest, parsed_grant)
    missing = _missing_measurements(limits, parsed_grant, parsed_call)
    if missing:
        return _result([_reason("call-invalid", "blocked", message, path) for path, message in missing])

    # Step 2: hard limits (C5-C16).
    result = _result(_hard_limits(manifest, limits, parsed_grant, parsed_call, parsed_state))

    # Step 3: the purpose judge, only if the call is not already blocked.
    if judge is not None and result["decision"] != "blocked":
        if not callable(judge):
            return _judge_error(result, "the purpose judge is not callable; ask the visitor")
        try:
            opinion = judge(manifest, grant, call)
        except Exception:  # the judge is outside code; fail towards asking
            return _judge_error(result, "the purpose judge failed; ask the visitor")
        if inspect.isawaitable(opinion):
            close = getattr(opinion, "close", None)
            if callable(close):
                close()
            return _judge_error(
                result,
                "the purpose judge returned an awaitable; check_call is synchronous: "
                "await the judge yourself and use apply_opinion",
            )
        result = apply_opinion(result, opinion)
    return result


def apply_opinion(result: CheckResult, opinion: Optional[Opinion]) -> CheckResult:
    """Merge a purpose judge's opinion into a gate result.

    An opinion can only make the decision stricter. None and ``allowed``
    change nothing. Anything that is not a valid opinion adds
    ``purpose-judge-error`` with decision ``ask``. Returns a new result.
    """
    if opinion is None:
        return {"decision": result["decision"], "reasons": list(result["reasons"])}
    if not _is_opinion(opinion):
        return _judge_error(result, "the purpose judge returned something that is not an opinion; ask the visitor")
    decision = opinion["decision"]
    if decision == "allowed":
        return {"decision": result["decision"], "reasons": list(result["reasons"])}
    reason = _reason(
        opinion.get("code") or "purpose-doubt",
        decision,
        opinion.get("message") or "the purpose judge doubts that this call fits the declared purpose",
    )
    return _escalate(result, reason)


# ---------------------------------------------------------------------------
# Effective limits and required measurements


def _effective_limits(manifest: Mapping, grant: _Grant) -> Dict[str, Any]:
    """The stricter of the manifest limit and the grant limit, per known limit."""
    manifest_limits = manifest["purpose"].get("limits")
    if not is_object(manifest_limits):
        manifest_limits = {}
    limits: Dict[str, Any] = {}
    for name in KNOWN_LIMITS:
        values = [v for v in (manifest_limits.get(name), grant.limits.get(name)) if v is not None]
        if values:
            limits[name] = min(values)
    return limits


def _missing_measurements(limits: Dict[str, Any], grant: _Grant, call: _Call) -> _Problems:
    problems: _Problems = []
    if "maxClipSeconds" in limits and call.clip_seconds is None:
        problems.append(
            (
                "/input/clipSeconds",
                '"input.clipSeconds" is required while a clip length limit applies; '
                "use 0 when the call sends no audio",
            )
        )
    if ("maxTextChars" in limits or grant.warn_sends_text) and call.text_chars is None:
        problems.append(
            (
                "/input/textChars",
                '"input.textChars" is required while a text limit or a text warning applies; '
                "use 0 when the call sends no text",
            )
        )
    return problems


# ---------------------------------------------------------------------------
# Step 2


def _hard_limits(
    manifest: Mapping, limits: Dict[str, Any], grant: _Grant, call: _Call, state: _State
) -> List[Reason]:
    reasons: List[Reason] = []

    def add(code: str, decision: Decision, message: str) -> None:
        reasons.append(_reason(code, decision, message))

    # C5
    if call.at >= grant.expires_at:
        add("grant-expired", "blocked", "the grant expired at %s" % grant.expires_text)
    # C6
    if manifest["skills"].get(call.skill) is None:
        add("skill-not-declared", "blocked", "skill %s is not declared in the manifest" % quote(call.skill))
    # C7
    if call.skill not in grant.skills:
        add("skill-not-granted", "blocked", "skill %s is not in the grant" % quote(call.skill))
    # C8
    by_grant = grant.local_only
    by_manifest = manifest["privacy"] == "local-only"
    if (by_grant or by_manifest) and call.provider not in LOCAL_PROVIDERS:
        who = (
            "the grant and the manifest allow"
            if by_grant and by_manifest
            else "the grant allows" if by_grant else "the manifest allows"
        )
        add(
            "not-local",
            "blocked",
            "%s local providers only (local-model, home-server); this call would use %s"
            % (who, quote(call.provider)),
        )
    # C9
    max_clip = limits.get("maxClipSeconds")
    if max_clip is not None and call.clip_seconds is not None and call.clip_seconds > max_clip:
        add(
            "clip-too-long",
            "blocked",
            "the clip is %s seconds; the limit is %s" % (format_number(call.clip_seconds), format_number(max_clip)),
        )
    # C10
    max_text = limits.get("maxTextChars")
    if max_text is not None and call.text_chars is not None and call.text_chars > max_text:
        add(
            "text-too-long",
            "blocked",
            "the text has %s characters; the limit is %s" % (format_number(call.text_chars), format_number(max_text)),
        )
    # C11
    max_rate = limits.get("maxCallsPerMinute")
    if max_rate is not None:
        recent = sum(1 for t in state.call_times if call.at - RATE_WINDOW_MS < t <= call.at)
        if recent >= max_rate:
            add(
                "rate-exceeded",
                "blocked",
                "%d calls in the last minute; the limit is %s per minute" % (recent, format_number(max_rate)),
            )
    # C12
    budget = grant.budget
    spent = state.spent if state.spent is not None else zero(budget.currency)
    mismatches = []
    if call.cost.currency != budget.currency:
        mismatches.append("the estimated cost is in %s" % call.cost.currency)
    if spent.currency != budget.currency:
        mismatches.append("the session spend is in %s" % spent.currency)
    after: Optional[Money] = None
    if mismatches:
        add(
            "currency-mismatch",
            "blocked",
            "%s, but the budget is in %s; the gate does not convert currencies"
            % (" and ".join(mismatches), budget.currency),
        )
    else:
        # C13
        after = add_money(spent, call.cost)
        if after.amount > budget.amount:
            add(
                "budget-exceeded",
                "ask",
                "this call (%s) would bring the session spend to %s, above the budget of %s"
                % (format_money(call.cost), format_money(after), format_money(budget)),
            )
    # C14
    if grant.warn_sends_text and call.text_chars is not None and call.text_chars > 0:
        add(
            "text-sent",
            "warn",
            "this call sends text (%s characters) and the visitor asked to be warned"
            % format_number(call.text_chars),
        )
    if after is not None:
        # C15
        if grant.warn_call_cost is not None and call.cost.amount > grant.warn_call_cost.amount:
            add(
                "call-cost-high",
                "warn",
                "this call costs %s, above the visitor's warning level of %s"
                % (format_money(call.cost), format_money(grant.warn_call_cost)),
            )
        # C16
        if grant.warn_spend is not None and after.amount > grant.warn_spend.amount:
            add(
                "spend-high",
                "warn",
                "the session spend would reach %s, above the visitor's warning level of %s"
                % (format_money(after), format_money(grant.warn_spend)),
            )
    return reasons


# ---------------------------------------------------------------------------
# Step 1: reading the grant, the call and the state


def _read_grant(grant: Any) -> Tuple[Optional[_Grant], _Problems]:
    if not is_object(grant):
        return None, [("", "the grant must be a JSON object")]
    problems: _Problems = []

    def bad(path: str, message: str) -> None:
        problems.append((path, message))

    problem = version_problem(grant.get("byoa"))
    if problem is not None:
        bad("/byoa", problem)

    skills = grant.get("skills")
    granted = set()
    if not is_array(skills):
        bad("/skills", '"skills" must be an array of skill ids')
    else:
        for index, skill_id in enumerate(skills):
            if is_skill_id(skill_id):
                granted.add(skill_id)
            else:
                bad("/skills/%d" % index, "%s is not a skill id of the form domain.action" % quote(skill_id))

    budget: Optional[Money] = None
    budget_obj = grant.get("budget")
    if not is_object(budget_obj):
        bad("/budget", '"budget" must be an object such as {"perSession": "0.20 USD"}')
    else:
        budget = parse_money(budget_obj.get("perSession"))
        if budget is None:
            bad("/budget/perSession", '"budget.perSession" must be a money string such as "0.20 USD"')
        for key in present_keys(budget_obj):
            if key != "perSession":
                bad(pointer("budget", key), "unknown budget field %s; %s" % (quote(key), _REFUSED))

    expires_text = grant.get("expiresAt")
    expires_at = parse_timestamp(expires_text)
    if expires_at is None:
        bad("/expiresAt", '"expiresAt" must be an RFC 3339 timestamp such as "2026-10-10T13:00:00Z"')

    limits: Dict[str, Any] = {}
    limits_obj = grant.get("limits")
    if limits_obj is not None:
        if not is_object(limits_obj):
            bad("/limits", '"limits" must be an object')
        else:
            for name in present_keys(limits_obj):
                path = pointer("limits", name)
                if name not in KNOWN_LIMITS:
                    bad(path, "unknown limit %s; %s" % (quote(name), _REFUSED))
                    continue
                problem = limit_problem(name, limits_obj[name])
                if problem is not None:
                    bad(path, problem)
                else:
                    limits[name] = limits_obj[name]

    local_only = grant.get("localOnly")
    if local_only is None:
        local_only = False
    elif not isinstance(local_only, bool):
        bad("/localOnly", '"localOnly" must be true or false')
        local_only = False

    sends_text = False
    thresholds: Dict[str, Optional[Money]] = {"callCostAbove": None, "spendAbove": None}
    warn_if = grant.get("warnIf")
    if warn_if is not None:
        if not is_object(warn_if):
            bad("/warnIf", '"warnIf" must be an object')
        else:
            for key in present_keys(warn_if):
                path = pointer("warnIf", key)
                value = warn_if[key]
                if key == "sendsText":
                    if isinstance(value, bool):
                        sends_text = value
                    else:
                        bad(path, '"warnIf.sendsText" must be true or false')
                elif key in thresholds:
                    money = parse_money(value)
                    if money is None:
                        bad(path, '"warnIf.%s" must be a money string such as "0.05 USD"' % key)
                    elif budget is not None and money.currency != budget.currency:
                        bad(path, '"warnIf.%s" must be in the budget currency (%s)' % (key, budget.currency))
                    else:
                        thresholds[key] = money
                else:
                    bad(path, "unknown condition %s; %s" % (quote(key), _REFUSED))

    for key in present_keys(grant):
        if key not in _GRANT_FIELDS:
            bad(pointer(key), "unknown grant field %s; %s" % (quote(key), _REFUSED))

    if problems or budget is None or expires_at is None:
        return None, problems
    return (
        _Grant(
            skills=frozenset(granted),
            budget=budget,
            expires_at=expires_at,
            expires_text=expires_text,
            limits=limits,
            local_only=local_only,
            warn_sends_text=sends_text,
            warn_call_cost=thresholds["callCostAbove"],
            warn_spend=thresholds["spendAbove"],
        ),
        [],
    )


def _read_call(call: Any) -> Tuple[Optional[_Call], _Problems]:
    if not is_object(call):
        return None, [("", "the call must be a JSON object")]
    problems: _Problems = []

    def bad(path: str, message: str) -> None:
        problems.append((path, message))

    skill = call.get("skill")
    if not is_skill_id(skill):
        bad("/skill", '"skill" must be a skill id such as "audio.transcribe"')
    at = parse_timestamp(call.get("at"))
    if at is None:
        bad("/at", '"at" must be an RFC 3339 timestamp such as "2026-10-10T12:00:00Z"')
    provider = call.get("provider")
    if not (isinstance(provider, str) and provider in PROVIDER_KINDS):
        bad("/provider", '"provider" must be one of: %s' % ", ".join(PROVIDER_KINDS))
    cost = parse_money(call.get("estimatedCost"))
    if cost is None:
        bad(
            "/estimatedCost",
            '"estimatedCost" must be a money string such as "0.003 USD" ("0 USD" for a local model)',
        )
    clip_seconds = text_chars = None
    measured = call.get("input")
    if measured is not None:
        if not is_object(measured):
            bad("/input", '"input" must be an object')
        else:
            clip_seconds = measured.get("clipSeconds")
            if clip_seconds is not None and not is_non_negative(clip_seconds):
                bad("/input/clipSeconds", '"input.clipSeconds" must be a number from 0 to 9007199254740991')
            text_chars = measured.get("textChars")
            if text_chars is not None and not is_count(text_chars):
                bad("/input/textChars", '"input.textChars" must be a whole number from 0 to 9007199254740991')
    params = call.get("params")
    if params is not None and not is_object(params):
        bad("/params", '"params" must be an object')

    if problems or at is None or cost is None:
        return None, problems
    return _Call(skill, at, provider, cost, clip_seconds, text_chars), []


def _read_state(state: Any) -> Tuple[Optional[_State], _Problems]:
    if state is None:
        return _State(None, []), []
    if not is_object(state):
        return None, [("", "the state must be a JSON object")]
    problems: _Problems = []

    spent: Optional[Money] = None
    if state.get("spent") is not None:
        spent = parse_money(state["spent"])
        if spent is None:
            problems.append(("/spent", '"spent" must be a money string such as "0.04 USD"'))

    call_times: List[int] = []
    raw_times = state.get("callTimes")
    if raw_times is not None:
        if not is_array(raw_times):
            problems.append(("/callTimes", '"callTimes" must be an array of RFC 3339 timestamps'))
        else:
            for index, value in enumerate(raw_times):
                instant = parse_timestamp(value)
                if instant is None:
                    problems.append(("/callTimes/%d" % index, "%s is not an RFC 3339 timestamp" % quote(value)))
                else:
                    call_times.append(instant)

    for key in present_keys(state):
        if key not in _STATE_FIELDS:
            problems.append(
                (
                    pointer(key),
                    "unknown state field %s; refused so that a misspelt field cannot reset the spend" % quote(key),
                )
            )

    if problems:
        return None, problems
    return _State(spent, call_times), []


# ---------------------------------------------------------------------------
# Results


def _reason(code: str, decision: Decision, message: str, path: Optional[str] = None) -> Reason:
    reason: Reason = {"code": code, "decision": decision, "message": message}
    if path is not None:
        reason["path"] = path
    return reason


def _result(reasons: List[Reason]) -> CheckResult:
    decision: Decision = "allowed"
    for reason in reasons:
        if _SEVERITY[reason["decision"]] > _SEVERITY[decision]:
            decision = reason["decision"]
    return {"decision": decision, "reasons": reasons}


def _escalate(result: CheckResult, reason: Reason) -> CheckResult:
    decision = result["decision"]
    if _SEVERITY.get(reason["decision"], 0) > _SEVERITY.get(decision, 0):
        decision = reason["decision"]
    return {"decision": decision, "reasons": list(result["reasons"]) + [reason]}


def _judge_error(result: CheckResult, message: str) -> CheckResult:
    return _escalate(result, _reason("purpose-judge-error", "ask", message))


def _is_opinion(value: Any) -> bool:
    if not is_object(value):
        return False
    decision = value.get("decision")
    if not (isinstance(decision, str) and decision in DECISIONS):
        return False
    code = value.get("code")
    if code is not None and not (isinstance(code, str) and code):
        return False
    message = value.get("message")
    return message is None or isinstance(message, str)
