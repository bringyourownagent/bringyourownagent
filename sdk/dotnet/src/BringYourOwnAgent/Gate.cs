using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BringYourOwnAgent.Internal;
using static BringYourOwnAgent.Internal.JsonValues;

namespace BringYourOwnAgent;

/// <summary>
/// The call gate: rules C1-C16 of spec/rules-0.1-draft.md (a draft for discussion).
/// </summary>
/// <remarks>
/// <see cref="CheckCall(JsonElement, JsonElement, JsonElement, JsonElement?, PurposeJudge?)"/> checks the hard
/// limits that code can check, reliably and for free. Judging the declared purpose in words needs a model;
/// that part plugs in through the purpose judge hook and has no opinion by default. The gate only evaluates:
/// it does not run the call and does not update the state (see "Execution contract" in the rules).
/// </remarks>
public static class Gate
{
    private static readonly string[] GrantFields = { "byoa", "skills", "budget", "expiresAt", "limits", "localOnly", "warnIf" };
    private static readonly string[] StateFields = { "spent", "callTimes" };
    private const string Refused = "this version cannot enforce it, so the grant is refused";

    /// <summary>
    /// Decides one call: <see cref="Decision.Allowed"/>, <see cref="Decision.Warn"/>, <see cref="Decision.Ask"/>
    /// or <see cref="Decision.Blocked"/>. Never throws for bad input: invalid documents block the call.
    /// </summary>
    /// <param name="manifest">The requester's manifest.</param>
    /// <param name="grant">What the visitor allowed for this requester.</param>
    /// <param name="call">One use of a skill, as the wallet sees it after choosing a provider.</param>
    /// <param name="state">What the wallet remembers within the session; null (or JSON null) means nothing spent and no calls yet.</param>
    /// <param name="judge">
    /// The purpose judge; null means no judge. It is consulted only when the hard limits did not block, and its
    /// opinion can only make the decision stricter. A judge that throws gives <see cref="Decision.Ask"/>.
    /// </param>
    public static CheckResult CheckCall(
        JsonElement manifest,
        JsonElement grant,
        JsonElement call,
        JsonElement? state = null,
        PurposeJudge? judge = null) =>
        Check(manifest, true, grant, true, call, true, state ?? default, true, judge);

    /// <summary>
    /// Decides one call given as JSON text. Text that is not JSON makes that document invalid
    /// (<c>manifest-invalid</c>, <c>grant-invalid</c>, <c>call-invalid</c> or <c>state-invalid</c> with path <c>""</c>),
    /// so the call is blocked; the other documents are still checked. A null <paramref name="state"/> means no state.
    /// Never throws for bad input. The elements passed to <paramref name="judge"/> stay valid after the call.
    /// </summary>
    /// <param name="manifest">The requester's manifest as JSON text.</param>
    /// <param name="grant">The visitor's grant as JSON text.</param>
    /// <param name="call">The call as JSON text.</param>
    /// <param name="state">The session state as JSON text, or null.</param>
    /// <param name="judge">The purpose judge; null means no judge.</param>
    public static CheckResult CheckCall(
        string? manifest,
        string? grant,
        string? call,
        string? state = null,
        PurposeJudge? judge = null)
    {
        var manifestOk = JsonInput.TryParse(manifest, out var manifestElement);
        var grantOk = JsonInput.TryParse(grant, out var grantElement);
        var callOk = JsonInput.TryParse(call, out var callElement);
        var stateOk = JsonInput.TryParse(state, out var stateElement);
        return Check(manifestElement, manifestOk, grantElement, grantOk, callElement, callOk, stateElement, stateOk, judge);
    }

    private static CheckResult Check(
        JsonElement manifest,
        bool manifestIsJson,
        JsonElement grant,
        bool grantIsJson,
        JsonElement call,
        bool callIsJson,
        JsonElement state,
        bool stateIsJson,
        PurposeJudge? judge)
    {
        var reasons = new List<Reason>();
        const string NotJson = "is not valid JSON";

        // Step 1: inputs (C1-C4).
        if (!manifestIsJson)
        {
            reasons.Add(new Reason("manifest-invalid", Decision.Blocked, "manifest-not-object: the manifest " + NotJson, ""));
        }
        else
        {
            foreach (var issue in Manifest.Validate(manifest).Errors)
            {
                reasons.Add(new Reason("manifest-invalid", Decision.Blocked, issue.Code + ": " + issue.Message, issue.Path));
            }
        }

        List<Problem> problems;
        var parsedGrant = grantIsJson ? ReadGrant(grant, out problems) : NotParsed<GrantData>("the grant " + NotJson, out problems);
        reasons.AddRange(problems.Select(p => new Reason("grant-invalid", Decision.Blocked, p.Message, p.Path)));
        var parsedCall = callIsJson ? ReadCall(call, out problems) : NotParsed<CallData>("the call " + NotJson, out problems);
        reasons.AddRange(problems.Select(p => new Reason("call-invalid", Decision.Blocked, p.Message, p.Path)));
        var parsedState = stateIsJson ? ReadState(state, out problems) : NotParsed<StateData>("the state " + NotJson, out problems);
        reasons.AddRange(problems.Select(p => new Reason("state-invalid", Decision.Blocked, p.Message, p.Path)));
        if (reasons.Count > 0 || parsedGrant is null || parsedCall is null || parsedState is null)
        {
            return MakeResult(reasons);
        }

        // Step 1b: measurements that a limit or a warning in force needs (C3).
        var limits = EffectiveLimits(new JsonObjectView(manifest), parsedGrant);
        var missing = MissingMeasurements(limits, parsedGrant, parsedCall);
        if (missing.Count > 0)
        {
            return MakeResult(missing.Select(p => new Reason("call-invalid", Decision.Blocked, p.Message, p.Path)));
        }

        // Step 2: hard limits (C5-C16).
        var result = MakeResult(HardLimits(new JsonObjectView(manifest), limits, parsedGrant, parsedCall, parsedState));

        // Step 3: the purpose judge, only if the call is not already blocked.
        if (judge is not null && result.Decision != Decision.Blocked)
        {
            Opinion? opinion;
            try
            {
                opinion = judge(manifest, grant, call);
            }
            catch (Exception)
            {
                // The judge is outside code; fail towards asking.
                return JudgeError(result, "the purpose judge failed; ask the visitor");
            }

            result = ApplyOpinion(result, opinion);
        }

        return result;
    }

    /// <summary>
    /// Merges a purpose judge's opinion into a gate result, for judges you await yourself.
    /// An opinion can only make the decision stricter; null and <see cref="Decision.Allowed"/> change nothing.
    /// </summary>
    /// <param name="result">A result of <c>CheckCall</c>. Ask the judge only if it is not blocked.</param>
    /// <param name="opinion">The judge's opinion, or null for no opinion.</param>
    /// <returns>A result with the opinion's reason added (code <c>purpose-doubt</c> when the opinion has none).</returns>
    public static CheckResult ApplyOpinion(CheckResult result, Opinion? opinion)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (opinion is null || opinion.Decision == Decision.Allowed)
        {
            return result;
        }

        var reason = new Reason(
            string.IsNullOrEmpty(opinion.Code) ? "purpose-doubt" : opinion.Code!,
            opinion.Decision,
            string.IsNullOrEmpty(opinion.Message) ? "the purpose judge doubts that this call fits the declared purpose" : opinion.Message!);
        return Escalate(result, reason);
    }

    /// <summary>
    /// Merges an opinion given as JSON, such as a model's answer: <c>{"decision": "warn", "code": "...", "message": "..."}</c>.
    /// JSON null (or an undefined element) means no opinion. Anything that is not an opinion adds
    /// <c>purpose-judge-error</c> with decision <see cref="Decision.Ask"/>. Never throws for bad input.
    /// </summary>
    /// <param name="result">A result of <c>CheckCall</c>. Ask the judge only if it is not blocked.</param>
    /// <param name="opinion">The opinion as JSON.</param>
    public static CheckResult ApplyOpinion(CheckResult result, JsonElement opinion)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (IsAbsent(opinion))
        {
            return result;
        }

        var parsed = ReadOpinion(opinion);
        if (parsed is null)
        {
            return JudgeError(result, "the purpose judge returned something that is not an opinion; ask the visitor");
        }

        return ApplyOpinion(result, parsed);
    }

    // -------------------------------------------------------------------------------------------
    // Effective limits and required measurements

    /// <summary>The stricter of the manifest limit and the grant limit, per known limit.</summary>
    private static Dictionary<string, double> EffectiveLimits(JsonObjectView manifest, GrantData grant)
    {
        var manifestLimits = new JsonObjectView(new JsonObjectView(manifest.Get("purpose")).Get("limits"));
        var limits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in Rules.KnownLimits)
        {
            var values = new List<double>();
            if (TryGetNumber(manifestLimits.Get(name), out var fromManifest))
            {
                values.Add(fromManifest);
            }

            if (grant.Limits.TryGetValue(name, out var fromGrant))
            {
                values.Add(fromGrant);
            }

            if (values.Count > 0)
            {
                limits[name] = values.Min();
            }
        }

        return limits;
    }

    private static List<Problem> MissingMeasurements(Dictionary<string, double> limits, GrantData grant, CallData call)
    {
        var problems = new List<Problem>();
        if (limits.ContainsKey("maxClipSeconds") && call.ClipSeconds is null)
        {
            problems.Add(new Problem(
                "/input/clipSeconds",
                "\"input.clipSeconds\" is required while a clip length limit applies; use 0 when the call sends no audio"));
        }

        if ((limits.ContainsKey("maxTextChars") || grant.WarnSendsText) && call.TextChars is null)
        {
            problems.Add(new Problem(
                "/input/textChars",
                "\"input.textChars\" is required while a text limit or a text warning applies; use 0 when the call sends no text"));
        }

        return problems;
    }

    // -------------------------------------------------------------------------------------------
    // Step 2

    private static List<Reason> HardLimits(
        JsonObjectView manifest,
        Dictionary<string, double> limits,
        GrantData grant,
        CallData call,
        StateData state)
    {
        var reasons = new List<Reason>();
        void Add(string code, Decision decision, string message) => reasons.Add(new Reason(code, decision, message));

        // C5
        if (call.At >= grant.ExpiresAt)
        {
            Add("grant-expired", Decision.Blocked, "the grant expired at " + grant.ExpiresText);
        }

        // C6
        if (IsAbsent(new JsonObjectView(manifest.Get("skills")).Get(call.Skill)))
        {
            Add("skill-not-declared", Decision.Blocked, "skill " + JsText.Quote(call.Skill) + " is not declared in the manifest");
        }

        // C7
        if (!grant.Skills.Contains(call.Skill))
        {
            Add("skill-not-granted", Decision.Blocked, "skill " + JsText.Quote(call.Skill) + " is not in the grant");
        }

        // C8
        var byGrant = grant.LocalOnly;
        var byManifest = AsString(manifest.Get("privacy")) == "local-only";
        if ((byGrant || byManifest) && !Rules.Has(Rules.LocalProviders, call.Provider))
        {
            var who = byGrant && byManifest
                ? "the grant and the manifest allow"
                : byGrant ? "the grant allows" : "the manifest allows";
            Add(
                "not-local",
                Decision.Blocked,
                who + " local providers only (local-model, home-server); this call would use " + JsText.Quote(call.Provider));
        }

        // C9
        if (limits.TryGetValue("maxClipSeconds", out var maxClip) && call.ClipSeconds is double clip && clip > maxClip)
        {
            Add(
                "clip-too-long",
                Decision.Blocked,
                "the clip is " + JsText.FormatNumber(clip) + " seconds; the limit is " + JsText.FormatNumber(maxClip));
        }

        // C10
        if (limits.TryGetValue("maxTextChars", out var maxText) && call.TextChars is double text && text > maxText)
        {
            Add(
                "text-too-long",
                Decision.Blocked,
                "the text has " + JsText.FormatNumber(text) + " characters; the limit is " + JsText.FormatNumber(maxText));
        }

        // C11
        if (limits.TryGetValue("maxCallsPerMinute", out var maxRate))
        {
            var recent = state.CallTimes.Count(t => call.At - Rules.RateWindowMs < t && t <= call.At);
            if (recent >= maxRate)
            {
                Add(
                    "rate-exceeded",
                    Decision.Blocked,
                    recent.ToString(CultureInfo.InvariantCulture) + " calls in the last minute; the limit is "
                    + JsText.FormatNumber(maxRate) + " per minute");
            }
        }

        // C12
        var budget = grant.Budget;
        var spent = state.Spent ?? Money.Zero(budget.Currency);
        var mismatches = new List<string>();
        if (call.Cost.Currency != budget.Currency)
        {
            mismatches.Add("the estimated cost is in " + call.Cost.Currency);
        }

        if (spent.Currency != budget.Currency)
        {
            mismatches.Add("the session spend is in " + spent.Currency);
        }

        Money? after = null;
        if (mismatches.Count > 0)
        {
            Add(
                "currency-mismatch",
                Decision.Blocked,
                string.Join(" and ", mismatches) + ", but the budget is in " + budget.Currency + "; the gate does not convert currencies");
        }
        else
        {
            // C13
            after = spent.Add(call.Cost);
            if (after.Micros > budget.Micros)
            {
                Add(
                    "budget-exceeded",
                    Decision.Ask,
                    "this call (" + call.Cost + ") would bring the session spend to " + after + ", above the budget of " + budget);
            }
        }

        // C14
        if (grant.WarnSendsText && call.TextChars is double sent && sent > 0)
        {
            Add(
                "text-sent",
                Decision.Warn,
                "this call sends text (" + JsText.FormatNumber(sent) + " characters) and the visitor asked to be warned");
        }

        if (after is not null)
        {
            // C15
            if (grant.WarnCallCost is not null && call.Cost.Micros > grant.WarnCallCost.Micros)
            {
                Add(
                    "call-cost-high",
                    Decision.Warn,
                    "this call costs " + call.Cost + ", above the visitor's warning level of " + grant.WarnCallCost);
            }

            // C16
            if (grant.WarnSpend is not null && after.Micros > grant.WarnSpend.Micros)
            {
                Add(
                    "spend-high",
                    Decision.Warn,
                    "the session spend would reach " + after + ", above the visitor's warning level of " + grant.WarnSpend);
            }
        }

        return reasons;
    }

    // -------------------------------------------------------------------------------------------
    // Step 1: reading the grant, the call and the state

    private static GrantData? ReadGrant(JsonElement grant, out List<Problem> problems)
    {
        problems = new List<Problem>();
        if (!IsObject(grant))
        {
            problems.Add(new Problem("", "the grant must be a JSON object"));
            return null;
        }

        var found = problems;
        void Bad(string path, string message) => found.Add(new Problem(path, message));
        var document = new JsonObjectView(grant);

        var problem = Manifest.VersionProblem(document.Get("byoa"));
        if (problem is not null)
        {
            Bad("/byoa", problem);
        }

        var skills = document.Get("skills");
        var granted = new HashSet<string>(StringComparer.Ordinal);
        if (!IsArray(skills))
        {
            Bad("/skills", "\"skills\" must be an array of skill ids");
        }
        else
        {
            var index = 0;
            foreach (var skillId in skills.EnumerateArray())
            {
                if (IsSkillId(skillId))
                {
                    granted.Add(AsString(skillId)!);
                }
                else
                {
                    Bad("/skills/" + index.ToString(CultureInfo.InvariantCulture), JsText.Quote(skillId) + " is not a skill id of the form domain.action");
                }

                index++;
            }
        }

        Money? budget = null;
        var budgetObject = document.Get("budget");
        if (!IsObject(budgetObject))
        {
            Bad("/budget", "\"budget\" must be an object such as {\"perSession\": \"0.20 USD\"}");
        }
        else
        {
            var budgetView = new JsonObjectView(budgetObject);
            budget = Money.Parse(budgetView.Get("perSession"));
            if (budget is null)
            {
                Bad("/budget/perSession", "\"budget.perSession\" must be a money string such as \"0.20 USD\"");
            }

            foreach (var key in budgetView.PresentKeys())
            {
                if (key != "perSession")
                {
                    Bad(Pointer("budget", key), "unknown budget field " + JsText.Quote(key) + "; " + Refused);
                }
            }
        }

        var expiresElement = document.Get("expiresAt");
        var expiresAt = Timestamp.Parse(expiresElement);
        if (expiresAt is null)
        {
            Bad("/expiresAt", "\"expiresAt\" must be an RFC 3339 timestamp such as \"2026-10-10T13:00:00Z\"");
        }

        var limits = new Dictionary<string, double>(StringComparer.Ordinal);
        var limitsObject = document.Get("limits");
        if (!IsAbsent(limitsObject))
        {
            if (!IsObject(limitsObject))
            {
                Bad("/limits", "\"limits\" must be an object");
            }
            else
            {
                var limitsView = new JsonObjectView(limitsObject);
                foreach (var name in limitsView.PresentKeys())
                {
                    var path = Pointer("limits", name);
                    if (!Rules.Has(Rules.KnownLimits, name))
                    {
                        Bad(path, "unknown limit " + JsText.Quote(name) + "; " + Refused);
                        continue;
                    }

                    var limitProblem = Manifest.LimitProblem(name, limitsView.Get(name));
                    if (limitProblem is not null)
                    {
                        Bad(path, limitProblem);
                    }
                    else
                    {
                        TryGetNumber(limitsView.Get(name), out var value);
                        limits[name] = value;
                    }
                }
            }
        }

        var localOnly = false;
        var localOnlyElement = document.Get("localOnly");
        if (!IsAbsent(localOnlyElement))
        {
            if (!IsBoolean(localOnlyElement))
            {
                Bad("/localOnly", "\"localOnly\" must be true or false");
            }
            else
            {
                localOnly = localOnlyElement.ValueKind == JsonValueKind.True;
            }
        }

        var sendsText = false;
        Money? callCostAbove = null;
        Money? spendAbove = null;
        var warnIf = document.Get("warnIf");
        if (!IsAbsent(warnIf))
        {
            if (!IsObject(warnIf))
            {
                Bad("/warnIf", "\"warnIf\" must be an object");
            }
            else
            {
                var warnView = new JsonObjectView(warnIf);
                foreach (var key in warnView.PresentKeys())
                {
                    var path = Pointer("warnIf", key);
                    var value = warnView.Get(key);
                    if (key == "sendsText")
                    {
                        if (IsBoolean(value))
                        {
                            sendsText = value.ValueKind == JsonValueKind.True;
                        }
                        else
                        {
                            Bad(path, "\"warnIf.sendsText\" must be true or false");
                        }
                    }
                    else if (key == "callCostAbove" || key == "spendAbove")
                    {
                        var money = Money.Parse(value);
                        if (money is null)
                        {
                            Bad(path, "\"warnIf." + key + "\" must be a money string such as \"0.05 USD\"");
                        }
                        else if (budget is not null && money.Currency != budget.Currency)
                        {
                            Bad(path, "\"warnIf." + key + "\" must be in the budget currency (" + budget.Currency + ")");
                        }
                        else if (key == "callCostAbove")
                        {
                            callCostAbove = money;
                        }
                        else
                        {
                            spendAbove = money;
                        }
                    }
                    else
                    {
                        Bad(path, "unknown condition " + JsText.Quote(key) + "; " + Refused);
                    }
                }
            }
        }

        foreach (var key in document.PresentKeys())
        {
            if (!GrantFields.Contains(key))
            {
                Bad(Pointer(key), "unknown grant field " + JsText.Quote(key) + "; " + Refused);
            }
        }

        if (problems.Count > 0 || budget is null || expiresAt is null)
        {
            return null;
        }

        return new GrantData(granted, budget, expiresAt.Value, AsString(expiresElement)!, limits, localOnly, sendsText, callCostAbove, spendAbove);
    }

    private static CallData? ReadCall(JsonElement call, out List<Problem> problems)
    {
        problems = new List<Problem>();
        if (!IsObject(call))
        {
            problems.Add(new Problem("", "the call must be a JSON object"));
            return null;
        }

        var found = problems;
        void Bad(string path, string message) => found.Add(new Problem(path, message));
        var document = new JsonObjectView(call);

        var skill = AsString(document.Get("skill"));
        if (!IsSkillId(skill))
        {
            Bad("/skill", "\"skill\" must be a skill id such as \"audio.transcribe\"");
        }

        var at = Timestamp.Parse(document.Get("at"));
        if (at is null)
        {
            Bad("/at", "\"at\" must be an RFC 3339 timestamp such as \"2026-10-10T12:00:00Z\"");
        }

        var provider = AsString(document.Get("provider"));
        if (!Rules.Has(Rules.ProviderKinds, provider))
        {
            Bad("/provider", "\"provider\" must be one of: " + string.Join(", ", Rules.ProviderKinds));
        }

        var cost = Money.Parse(document.Get("estimatedCost"));
        if (cost is null)
        {
            Bad("/estimatedCost", "\"estimatedCost\" must be a money string such as \"0.003 USD\" (\"0 USD\" for a local model)");
        }

        double? clipSeconds = null;
        double? textChars = null;
        var measured = document.Get("input");
        if (!IsAbsent(measured))
        {
            if (!IsObject(measured))
            {
                Bad("/input", "\"input\" must be an object");
            }
            else
            {
                var input = new JsonObjectView(measured);
                var clip = input.Get("clipSeconds");
                if (!IsAbsent(clip))
                {
                    if (!IsNonNegative(clip))
                    {
                        Bad("/input/clipSeconds", "\"input.clipSeconds\" must be a number from 0 to 9007199254740991");
                    }
                    else if (TryGetNumber(clip, out var value))
                    {
                        clipSeconds = value;
                    }
                }

                var text = input.Get("textChars");
                if (!IsAbsent(text))
                {
                    if (!IsCount(text))
                    {
                        Bad("/input/textChars", "\"input.textChars\" must be a whole number from 0 to 9007199254740991");
                    }
                    else if (TryGetNumber(text, out var value))
                    {
                        textChars = value;
                    }
                }
            }
        }

        var parameters = document.Get("params");
        if (!IsAbsent(parameters) && !IsObject(parameters))
        {
            Bad("/params", "\"params\" must be an object");
        }

        if (problems.Count > 0 || at is null || cost is null)
        {
            return null;
        }

        return new CallData(skill!, at.Value, provider!, cost, clipSeconds, textChars);
    }

    private static StateData? ReadState(JsonElement state, out List<Problem> problems)
    {
        problems = new List<Problem>();
        if (IsAbsent(state))
        {
            return new StateData(null, new List<long>());
        }

        if (!IsObject(state))
        {
            problems.Add(new Problem("", "the state must be a JSON object"));
            return null;
        }

        var document = new JsonObjectView(state);
        Money? spent = null;
        var rawSpent = document.Get("spent");
        if (!IsAbsent(rawSpent))
        {
            spent = Money.Parse(rawSpent);
            if (spent is null)
            {
                problems.Add(new Problem("/spent", "\"spent\" must be a money string such as \"0.04 USD\""));
            }
        }

        var callTimes = new List<long>();
        var rawTimes = document.Get("callTimes");
        if (!IsAbsent(rawTimes))
        {
            if (!IsArray(rawTimes))
            {
                problems.Add(new Problem("/callTimes", "\"callTimes\" must be an array of RFC 3339 timestamps"));
            }
            else
            {
                var index = 0;
                foreach (var value in rawTimes.EnumerateArray())
                {
                    var instant = Timestamp.Parse(value);
                    if (instant is null)
                    {
                        problems.Add(new Problem(
                            "/callTimes/" + index.ToString(CultureInfo.InvariantCulture),
                            JsText.Quote(value) + " is not an RFC 3339 timestamp"));
                    }
                    else
                    {
                        callTimes.Add(instant.Value);
                    }

                    index++;
                }
            }
        }

        foreach (var key in document.PresentKeys())
        {
            if (!StateFields.Contains(key))
            {
                problems.Add(new Problem(
                    Pointer(key),
                    "unknown state field " + JsText.Quote(key) + "; refused so that a misspelt field cannot reset the spend"));
            }
        }

        return problems.Count > 0 ? null : new StateData(spent, callTimes);
    }

    private static T? NotParsed<T>(string message, out List<Problem> problems)
        where T : class
    {
        problems = new List<Problem> { new Problem("", message) };
        return null;
    }

    // -------------------------------------------------------------------------------------------
    // Opinions and results

    private static Opinion? ReadOpinion(JsonElement value)
    {
        if (!IsObject(value))
        {
            return null;
        }

        var opinion = new JsonObjectView(value);
        if (!DecisionExtensions.TryParse(AsString(opinion.Get("decision")), out var decision))
        {
            return null;
        }

        var code = opinion.Get("code");
        if (!IsAbsent(code) && string.IsNullOrEmpty(AsString(code)))
        {
            return null;
        }

        var message = opinion.Get("message");
        if (!IsAbsent(message) && AsString(message) is null)
        {
            return null;
        }

        return new Opinion(decision, AsString(code), AsString(message));
    }

    private static CheckResult MakeResult(IEnumerable<Reason> reasons)
    {
        var list = reasons.ToList();
        var decision = Decision.Allowed;
        foreach (var reason in list)
        {
            if (reason.Decision > decision)
            {
                decision = reason.Decision;
            }
        }

        return new CheckResult(decision, list);
    }

    private static CheckResult Escalate(CheckResult result, Reason reason)
    {
        var decision = reason.Decision > result.Decision ? reason.Decision : result.Decision;
        return new CheckResult(decision, result.Reasons.Concat(new[] { reason }));
    }

    private static CheckResult JudgeError(CheckResult result, string message) =>
        Escalate(result, new Reason("purpose-judge-error", Decision.Ask, message));

    private sealed class Problem
    {
        public Problem(string path, string message)
        {
            Path = path;
            Message = message;
        }

        public string Path { get; }

        public string Message { get; }
    }

    private sealed class GrantData
    {
        public GrantData(
            HashSet<string> skills,
            Money budget,
            long expiresAt,
            string expiresText,
            Dictionary<string, double> limits,
            bool localOnly,
            bool warnSendsText,
            Money? warnCallCost,
            Money? warnSpend)
        {
            Skills = skills;
            Budget = budget;
            ExpiresAt = expiresAt;
            ExpiresText = expiresText;
            Limits = limits;
            LocalOnly = localOnly;
            WarnSendsText = warnSendsText;
            WarnCallCost = warnCallCost;
            WarnSpend = warnSpend;
        }

        public HashSet<string> Skills { get; }

        public Money Budget { get; }

        public long ExpiresAt { get; }

        public string ExpiresText { get; }

        public Dictionary<string, double> Limits { get; }

        public bool LocalOnly { get; }

        public bool WarnSendsText { get; }

        public Money? WarnCallCost { get; }

        public Money? WarnSpend { get; }
    }

    private sealed class CallData
    {
        public CallData(string skill, long at, string provider, Money cost, double? clipSeconds, double? textChars)
        {
            Skill = skill;
            At = at;
            Provider = provider;
            Cost = cost;
            ClipSeconds = clipSeconds;
            TextChars = textChars;
        }

        public string Skill { get; }

        public long At { get; }

        public string Provider { get; }

        public Money Cost { get; }

        public double? ClipSeconds { get; }

        public double? TextChars { get; }
    }

    private sealed class StateData
    {
        public StateData(Money? spent, List<long> callTimes)
        {
            Spent = spent;
            CallTimes = callTimes;
        }

        public Money? Spent { get; }

        public List<long> CallTimes { get; }
    }
}
