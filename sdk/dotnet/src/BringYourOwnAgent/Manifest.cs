using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BringYourOwnAgent.Internal;
using static BringYourOwnAgent.Internal.JsonValues;

namespace BringYourOwnAgent;

/// <summary>Manifest rules M1-M28 of spec/rules-0.1-draft.md (a draft for discussion).</summary>
public static class Manifest
{
    private static readonly string[] TopLevelFields = { "byoa", "purpose", "skills", "privacy", "expectedCost" };
    private static readonly string[] PurposeFields = { "summary", "limits" };

    /// <summary>
    /// Checks a requester's manifest against the 0.1-draft rules.
    /// Never throws for bad input: problems are returned as data.
    /// </summary>
    /// <param name="manifest">The manifest. JSON null or an undefined element counts as no manifest.</param>
    /// <returns><see cref="ValidationResult.Ok"/> is true when there are no errors; warnings never make a manifest invalid.</returns>
    public static ValidationResult Validate(JsonElement manifest)
    {
        var errors = new List<Issue>();
        var warnings = new List<Issue>();
        void Error(string code, string path, string message) => errors.Add(new Issue(code, path, message));
        void Warn(string code, string path, string message) => warnings.Add(new Issue(code, path, message));

        // M1
        if (!IsObject(manifest))
        {
            Error("manifest-not-object", "", "the manifest must be a JSON object");
            return new ValidationResult(errors, warnings);
        }

        var document = new JsonObjectView(manifest);

        // M2, M3
        var version = document.Get("byoa");
        var problem = VersionProblem(version);
        if (problem is not null)
        {
            Error(IsAbsent(version) ? "version-missing" : "version-unsupported", "/byoa", problem);
        }

        // M4-M9
        var purpose = document.Get("purpose");
        if (!IsObject(purpose))
        {
            Error("purpose-missing", "/purpose", "field \"purpose\" is missing or is not an object");
        }
        else
        {
            CheckPurpose(new JsonObjectView(purpose), Error, Warn);
        }

        // M10-M15
        var skills = document.Get("skills");
        var skillsView = IsObject(skills) ? new JsonObjectView(skills) : null;
        if (skillsView is null || skillsView.PresentKeys().Count == 0)
        {
            Error("skills-missing", "/skills", "field \"skills\" must be an object with at least one skill");
        }
        else
        {
            CheckSkills(skillsView, Error, Warn);
        }

        // M16, M17
        var privacy = document.Get("privacy");
        var classes = string.Join(", ", Rules.PrivacyClasses);
        if (IsAbsent(privacy))
        {
            Error("privacy-missing", "/privacy", "field \"privacy\" is missing; use one of: " + classes);
        }
        else if (!Rules.Has(Rules.PrivacyClasses, AsString(privacy)))
        {
            Error("privacy-unknown", "/privacy", "privacy class " + JsText.Quote(privacy) + " is unknown; use one of: " + classes);
        }

        // M18-M20
        var cost = document.Get("expectedCost");
        if (IsAbsent(cost))
        {
            Warn(
                "cost-missing",
                "/expectedCost",
                "field \"expectedCost\" is missing; visitors decide faster when they see an expected cost");
        }
        else if (!IsObject(cost))
        {
            Error("cost-invalid", "/expectedCost", "\"expectedCost\" must be an object such as {\"perSession\": \"0.06 USD\"}");
        }
        else
        {
            var costView = new JsonObjectView(cost);
            var keys = costView.PresentKeys();
            if (!keys.Any(key => Rules.Has(Rules.CostPeriods, key)))
            {
                Warn("cost-missing", "/expectedCost", "\"expectedCost\" has none of: " + string.Join(", ", Rules.CostPeriods));
            }

            foreach (var key in keys)
            {
                var path = Pointer("expectedCost", key);
                if (Rules.Has(Rules.CostPeriods, key))
                {
                    if (Money.Parse(costView.Get(key)) is null)
                    {
                        Error("cost-invalid", path, "\"expectedCost." + key + "\" must be a money string such as \"0.06 USD\"");
                    }
                }
                else
                {
                    Warn("field-unknown", path, "unknown field " + JsText.Quote(key) + " in \"expectedCost\"; this version ignores it");
                }
            }
        }

        // M21
        foreach (var key in document.PresentKeys())
        {
            if (!TopLevelFields.Contains(key))
            {
                Warn("field-unknown", Pointer(key), "unknown field " + JsText.Quote(key) + "; this version ignores it");
            }
        }

        return new ValidationResult(errors, warnings);
    }

    /// <summary>
    /// Checks a manifest given as JSON text. Text that is not JSON is reported as
    /// <c>manifest-not-object</c>; a null string counts as no manifest. Never throws for bad input.
    /// </summary>
    /// <param name="json">The manifest as JSON text.</param>
    public static ValidationResult Validate(string? json)
    {
        if (!JsonInput.TryParse(json, out var manifest))
        {
            return new ValidationResult(
                new[] { new Issue("manifest-not-object", "", "the manifest is not valid JSON") },
                new Issue[0]);
        }

        return Validate(manifest);
    }

    /// <summary>Why a known limit value is invalid, or null if it is fine.</summary>
    internal static string? LimitProblem(string name, JsonElement value)
    {
        if (Rules.CountLimits.Contains(name))
        {
            if (!IsCount(value))
            {
                return "limit " + JsText.Quote(name) + " must be a whole number from 0 to 9007199254740991";
            }
        }
        else if (!IsNonNegative(value))
        {
            return "limit " + JsText.Quote(name) + " must be a number from 0 to 9007199254740991";
        }

        return null;
    }

    internal static string? VersionProblem(JsonElement value)
    {
        if (IsAbsent(value))
        {
            return "field \"byoa\" is missing; it names the protocol version, e.g. \"0.1-draft\"";
        }

        if (!Rules.Has(Rules.SupportedVersions, AsString(value)))
        {
            return "protocol version " + JsText.Quote(value) + " is not supported; supported: "
                + string.Join(", ", Rules.SupportedVersions.Select(JsText.Quote));
        }

        return null;
    }

    private delegate void Report(string code, string path, string message);

    private static void CheckPurpose(JsonObjectView purpose, Report error, Report warn)
    {
        var summary = AsString(purpose.Get("summary"));
        if (summary is null || IsBlank(summary))
        {
            error(
                "summary-missing",
                "/purpose/summary",
                "\"purpose.summary\" must be a non-empty string: one short sentence for the visitor");
        }
        else if (CodePointLength(summary) > Rules.SummaryMaxLength)
        {
            error(
                "summary-too-long",
                "/purpose/summary",
                "\"purpose.summary\" has " + CodePointLength(summary).ToString(CultureInfo.InvariantCulture)
                + " characters; the limit is " + Rules.SummaryMaxLength.ToString(CultureInfo.InvariantCulture));
        }

        var limits = purpose.Get("limits");
        if (!IsAbsent(limits))
        {
            if (!IsObject(limits))
            {
                error("limit-invalid", "/purpose/limits", "\"purpose.limits\" must be an object");
            }
            else
            {
                var limitsView = new JsonObjectView(limits);
                foreach (var name in limitsView.PresentKeys())
                {
                    var path = Pointer("purpose", "limits", name);
                    if (Rules.Has(Rules.KnownLimits, name))
                    {
                        var problem = LimitProblem(name, limitsView.Get(name));
                        if (problem is not null)
                        {
                            error("limit-invalid", path, problem);
                        }
                    }
                    else
                    {
                        warn("limit-unknown", path, "unknown limit " + JsText.Quote(name) + "; wallets that do not know it will ignore it");
                    }
                }
            }
        }

        foreach (var key in purpose.PresentKeys())
        {
            if (!PurposeFields.Contains(key))
            {
                warn("field-unknown", Pointer("purpose", key), "unknown field " + JsText.Quote(key) + " in \"purpose\"; this version ignores it");
            }
        }
    }

    private static void CheckSkills(JsonObjectView skills, Report error, Report warn)
    {
        var anyRequired = false;
        foreach (var skillId in skills.PresentKeys())
        {
            var path = Pointer("skills", skillId);
            if (!IsSkillId(skillId))
            {
                error(
                    "skill-id-invalid",
                    path,
                    "skill id " + JsText.Quote(skillId) + " must have the form domain.action in lower case, e.g. \"audio.transcribe\"");
            }
            else if (!Rules.Has(Rules.KnownSkills, skillId))
            {
                warn("skill-unknown", path, "skill " + JsText.Quote(skillId) + " is not in the 0.1-draft vocabulary; check the spelling");
            }

            var entry = skills.Get(skillId);
            if (!IsObject(entry))
            {
                error("skill-invalid", path, "skill " + JsText.Quote(skillId) + " must be an object of parameters");
                continue;
            }

            var required = new JsonObjectView(entry).Get("required");
            if (!IsBoolean(required))
            {
                error("skill-required-invalid", path + "/required", "skill " + JsText.Quote(skillId) + " must state \"required\": true or false");
            }
            else if (required.ValueKind == JsonValueKind.True)
            {
                anyRequired = true;
            }

            var models = new JsonObjectView(entry).Get("models");
            if (!IsAbsent(models))
            {
                CheckModels(skillId, models, error, warn);
            }
        }

        if (!anyRequired)
        {
            warn(
                "no-required-skill",
                "/skills",
                "no skill is marked \"required\": true; the site claims to work without any of them");
        }
    }

    /// <summary>
    /// M22-M28: the <c>models</c> block of a skill. Only <c>kind</c>, <c>onMismatch</c>, <c>needs</c> and
    /// <c>abilities</c> are checked here; <c>count</c>, <c>distinct</c>, <c>require</c>, <c>recommend</c>,
    /// <c>why</c>, <c>whyLong</c> and <c>names</c> stay the wallet's to read, and a block without
    /// <c>count</c> is valid (CONTRACT.md section 3.3).
    /// </summary>
    private static void CheckModels(string skillId, JsonElement models, Report error, Report warn)
    {
        var path = Pointer("skills", skillId, "models");
        if (!IsObject(models))
        {
            error("models-invalid", path, "\"models\" of skill " + JsText.Quote(skillId) + " must be an object");
            return;
        }

        var view = new JsonObjectView(models);
        if (!IsAbsent(view.Get("kind")) && !Rules.Has(Rules.SourceKinds, AsString(view.Get("kind"))))
        {
            error(
                "models-kind-invalid",
                path + "/kind",
                "\"models.kind\" must be one of: " + string.Join(", ", Rules.SourceKinds.Select(JsText.Quote))
                + "; got " + JsText.Quote(view.Get("kind")));
        }

        if (!IsAbsent(view.Get("onMismatch")) && !Rules.Has(Rules.OnMismatch, AsString(view.Get("onMismatch"))))
        {
            error(
                "models-onmismatch-invalid",
                path + "/onMismatch",
                "\"models.onMismatch\" must be \"drop\" or \"show\"; got " + JsText.Quote(view.Get("onMismatch")));
        }

        var needs = view.Get("needs");
        if (!IsAbsent(needs))
        {
            CheckNeeds(skillId, needs, error, warn);
        }

        var abilities = view.Get("abilities");
        if (!IsAbsent(abilities))
        {
            CheckAbilities(skillId, abilities, error, warn);
        }
    }

    private static void CheckNeeds(string skillId, JsonElement needs, Report error, Report warn)
    {
        var path = Pointer("skills", skillId, "models", "needs");
        if (!IsObject(needs))
        {
            error("needs-invalid", path, "\"models.needs\" of skill " + JsText.Quote(skillId) + " must be an object of conditions by property");
            return;
        }

        var view = new JsonObjectView(needs);
        foreach (var key in view.PresentKeys())
        {
            var keyPath = path + Pointer(key);
            var value = view.Get(key);
            if (!Rules.Has(Rules.NeedsProperties, key))
            {
                if (!key.StartsWith("x-", StringComparison.Ordinal))
                {
                    warn("needs-unknown", keyPath, "unknown property " + JsText.Quote(key) + " in \"models.needs\"; wallets that do not know it will ignore it");
                }

                continue;
            }

            var problem = NeedsProblem(key, value);
            if (problem is not null)
            {
                error("needs-invalid", keyPath, problem);
            }
        }
    }

    /// <summary>
    /// Why a condition of <c>needs</c> is invalid, or null if it is fine. An array is the short form of the
    /// one long form each property takes: <c>{"includes": [...]}</c> for a modality, <c>{"oneOf": [...]}</c> for
    /// <c>runs</c>, <c>{"min": n}</c> for <c>context</c> (CONTRACT.md sections 3.3 and 3.5).
    /// </summary>
    private static string? NeedsProblem(string key, JsonElement value)
    {
        if (key is "input" or "output")
        {
            if (ListOf(value, "includes") is { } items && IsListOf(Rules.Modalities, items))
            {
                return null;
            }

            return "\"models.needs." + key + "\" must be an array of " + string.Join(", ", Rules.Modalities.Select(JsText.Quote))
                + " with at least one value, or {\"includes\": [...]} of those; got " + JsText.Quote(value);
        }

        if (key == "runs")
        {
            if (ListOf(value, "oneOf") is { } items && IsListOf(Rules.Runs, items))
            {
                return null;
            }

            return "\"models.needs." + key + "\" must be an array of " + string.Join(", ", Rules.Runs.Select(JsText.Quote))
                + " with at least one value, or {\"oneOf\": [...]} of those; got " + JsText.Quote(value);
        }

        // context: a minimum, in thousands of tokens
        if (IsNonNegative(value))
        {
            return null;
        }

        if (IsObject(value))
        {
            var view = new JsonObjectView(value);
            if (view.PresentKeys().Count == 1 && IsNonNegative(view.Get("min")))
            {
                return null;
            }
        }

        return "\"models.needs." + key + "\" must be a number of thousands of tokens (the minimum) or {\"min\": n}; got " + JsText.Quote(value);
    }

    /// <summary>The array of a short form, or of the one long form <paramref name="key"/>, or null when the value is neither.</summary>
    private static JsonElement? ListOf(JsonElement value, string key)
    {
        if (IsArray(value))
        {
            return value;
        }

        if (IsObject(value))
        {
            var view = new JsonObjectView(value);
            if (view.PresentKeys().Count == 1 && !IsAbsent(view.Get(key)))
            {
                return view.Get(key);
            }
        }

        return null;
    }

    /// <summary>A non-empty array of strings, all of them in <paramref name="allowed"/>.</summary>
    private static bool IsListOf(IReadOnlyList<string> allowed, JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!Rules.Has(allowed, AsString(item)))
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckAbilities(string skillId, JsonElement abilities, Report error, Report warn)
    {
        var path = Pointer("skills", skillId, "models", "abilities");
        var wellFormed = IsArray(abilities) && abilities.GetArrayLength() > 0;
        if (wellFormed)
        {
            foreach (var item in abilities.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    wellFormed = false;
                    break;
                }
            }
        }

        if (!wellFormed)
        {
            error(
                "abilities-invalid",
                path,
                "\"models.abilities\" of skill " + JsText.Quote(skillId) + " must be an array of ability names, e.g. [\"web.search\"]");
            return;
        }

        var at = 0;
        foreach (var item in abilities.EnumerateArray())
        {
            var name = AsString(item)!;
            if (!Rules.Has(Rules.Abilities, name) && !name.StartsWith("x-", StringComparison.Ordinal))
            {
                warn("abilities-unknown", path + Pointer(at.ToString(CultureInfo.InvariantCulture)), "ability " + JsText.Quote(item) + " is not in the 0.1 vocabulary; wallets that do not know it will ignore it");
            }

            at++;
        }
    }
}
