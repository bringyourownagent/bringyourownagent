using System.Globalization;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// The models a site asks for: what the wallet proposes in the consent window, what it stores for the site, and what the site is given.
/// A site that names no models gets the skill and the wallet's route, as before; a site that asks for models gets only the ones the
/// visitor ticked for it, by reference, and can reach no others.
/// </summary>
public sealed partial class WalletEngine
{
    /// <summary>The public model list (names, prices). Empty until the wallet has one; the wallet works without it.</summary>
    public IModelCatalog Catalog { get; set; } = EmptyCatalog.Instance;

    /// <summary>Whether the visitor gave an OpenRouter key: models behind it can be added from a site's request.</summary>
    public Func<bool> HasOpenRouterKey { get; set; } = () => false;

    /// <summary>Raised with the origin when the models or limits of a site's live grants changed (the visitor edited them): its connection tells the site.</summary>
    public event Action<string>? GrantChanged;

    // What a short debate answer is made of, for the price shown beside a model.
    static readonly TextRequest TypicalAnswer = new([new ChatMessage("user", new string('x', 6000))], 700, 0.7, null, null);

    // ------------------------------------------------------------------------------------------------
    // the visitor's models as the planner sees them

    string CostText(ITextSource source)
    {
        if (!source.Paid) return "free";
        try
        {
            var estimate = SafeEstimate(source.EstimateCost(TypicalAnswer), paid: true);
            return "about " + Money.Show(estimate) + " an answer";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "paid";
        }
    }

    /// <summary>A model is a concern of a picture section when it draws (it is then judged: it may be behind the wrong road), or when it is behind the key and the public list says nothing of it.</summary>
    static bool MayDraw(ModelFacts facts) => facts.Output is { } output ? Modalities.DrawsPictures(output) : facts.OpenRouter;

    /// <summary>What a picture costs, in words: the price of a picture is not known unless the public list says the model is free (PROPOSAL-v3 1.3).</summary>
    static string CostOfPicture(ITextSource source) =>
        source is IImageSource picture && picture.EstimateImageCost(new ImageRequest("x", [], 1, null)) == 0 ? "free" : "price unknown";

    string CostOfNewPicture(string model) => Catalog.Find(model) is { Free: true } ? "free" : "price unknown";

    string CostOfNew(string model)
    {
        var entry = Catalog.Find(model);
        if (entry is null) return "price unknown";
        if (entry.Free) return "free";
        return entry.TypicalAnswerCost is { } cost ? "about " + Money.Show(Math.Max(0.000001m, cost)) + " an answer" : "price unknown";
    }

    /// <summary>The canonical name of a model the visitor added: his own note, or what the wallet can tell from the kind and the id.</summary>
    string ModelIdOf(SourceSetting setting)
    {
        if (!string.IsNullOrWhiteSpace(setting.ModelId)) return ModelNames.Canon(setting.ModelId);
        var model = setting.Model ?? "";
        // His agent is named agent/<its name>: a site that wants one asks for "agent/*" or "agent/claude-code", and no model is ever mistaken for it.
        if (setting.Kind == SourceKinds.Agent) return "agent/" + ModelNames.Canon(model);
        if (setting.Kind == SourceKinds.OpenRouter) return ModelNames.Canon(model);
        if (setting.Kind == ApiServices.Kind)
        {
            var vendor = ApiServices.Find(setting.Service)?.Vendor;
            var tail = ModelNames.Canon(model);
            return vendor is null || tail.Contains('/') ? tail : vendor + "/" + tail;
        }

        return ModelNames.FromLocal(model);
    }

    static string WhereOf(SourceSetting setting, ITextSource? source) => setting.Kind switch
    {
        SourceKinds.OpenRouter => "OpenRouter key",
        SourceKinds.Agent => "your agent",
        ApiServices.Kind => ApiServices.NameOf(setting.Service) + " key",
        _ => source is null ? "this PC" : Where(source),
    };

    /// <summary>
    /// The visitor's models for one skill in the order of his route, each with what stops it answering, if anything. A model is on the shelf of
    /// a text skill when it is a text model of his; on the shelf of a picture skill when nothing known says it cannot draw (a text model that
    /// cannot is no concern of a picture section and is not counted there).
    /// </summary>
    IReadOnlyList<ShelfModel> BuildShelf(WalletSettings settings, string skill)
    {
        var chain = ChainFor(settings, Vocabulary.Generate, null);
        var ordered = new List<SourceSetting>();
        foreach (var stop in chain.Stops)
        {
            if (settings.Sources.FirstOrDefault(s => s.Id == stop.SourceId && s.Kind is not null) is { } setting) ordered.Add(setting);
        }

        foreach (var setting in settings.Sources.Where(s => s.Kind is not null && (s.Skills.Count == 0 || s.Skills.Contains(Vocabulary.Generate))))
        {
            if (!ordered.Contains(setting)) ordered.Add(setting);
        }

        var shelf = new List<ShelfModel>();
        foreach (var setting in ordered)
        {
            if (setting.Skills.Count > 0 && !setting.Skills.Contains(Vocabulary.Generate)) continue;
            var source = _textSources.FirstOrDefault(s => s.Id == setting.Id);
            if (skill == Vocabulary.ImageGenerate && !MayDraw(FactsOf(setting, source))) continue;
            var stop = chain.Stops.FirstOrDefault(s => s.SourceId == setting.Id);
            string? notReady = null;
            if (source is null) notReady = "not available";
            else if (!setting.Enabled || stop is { Enabled: false }) notReady = "turned off";
            else
            {
                var plan = Router.PlanText(new SkillChain { Skill = Vocabulary.Generate, Stops = [new ChainStop { SourceId = setting.Id }] }, [source], _ => true, null,
                    pictures: skill == Vocabulary.ImageGenerate);
                notReady = plan.Skipped.FirstOrDefault()?.Why;
            }

            var modelId = ModelIdOf(setting);
            var catalogName = Catalog.Find(modelId)?.Name;
            var name = setting.Label is { Length: > 0 } label ? label : ModelNames.Display(setting.Model, catalogName);
            shelf.Add(new ShelfModel(setting.Id, name, modelId, WhereOf(setting, source), source?.Paid ?? setting.Kind is SourceKinds.OpenRouter or ApiServices.Kind,
                source is null ? "" : skill == Vocabulary.ImageGenerate ? CostOfPicture(source) : CostText(source), setting.Offer, notReady, FactsOf(setting, source)));
        }

        return shelf;
    }

    /// <summary>What the wallet knows of a source's properties: the visitor's word, then the catalog's, and where it runs from what it is.</summary>
    ModelFacts FactsOf(SourceSetting setting, ITextSource? source) =>
        ModelFacts.Of(setting, Catalog.Find(ModelIdOf(setting)),
            source is not null ? ProviderReceipt(source) : setting.Kind is SourceKinds.OpenRouter or SourceKinds.Agent or ApiServices.Kind ? "api-key" : "local");

    /// <summary>
    /// Whether one of the visitor's sources may answer text at all (the skill's own rule): where he picks a model for a text job himself (a site's models,
    /// the model that judges requests), one that does not write text is not offered.
    /// </summary>
    public bool ServesText(string sourceId) => Serves(sourceId, Vocabulary.Generate);

    /// <summary>Whether one of the visitor's sources may draw pictures at all: known to draw, and behind his OpenRouter key.</summary>
    public bool ServesPictures(string sourceId) => Serves(sourceId, Vocabulary.ImageGenerate);

    bool Serves(string sourceId, string skill) =>
        _store.Read(s => s.Sources.FirstOrDefault(x => x.Id == sourceId && x.Kind is not null) is { } setting && Offers(null, setting, skill));

    /// <summary>What the wallet knows of one of the visitor's sources, for the pages that show it (My models); null when there is no such source.</summary>
    public ModelFacts? FactsOfSource(string sourceId) =>
        _store.Read(s => s.Sources.FirstOrDefault(x => x.Id == sourceId && x.Kind is not null)) is { } setting
            ? FactsOf(setting, _textSources.FirstOrDefault(t => t.Id == sourceId))
            : null;

    /// <summary>
    /// For the Sites page: what is amiss with each model a site was given for a skill, by source id (a model with nothing amiss is absent). It is judged by what the
    /// site asked when it last connected, which a live grant remembers, and by the skill's own rule when there is none.
    /// </summary>
    public IReadOnlyDictionary<string, string> SiteModelNotes(string origin, string skill = Vocabulary.Generate)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = _store.Snapshot();
        var spec = _sessions.Values.Where(s => s.Origin == origin && s.ExpiresAt > Now).Select(s => s.Manifest.Skill(skill)?.Models).FirstOrDefault(m => m is not null);
        foreach (var id in settings.Site(origin)?.ModelsFor(skill)?.SourceIds ?? [])
        {
            if (settings.Sources.FirstOrDefault(s => s.Id == id && s.Kind is not null) is not { } setting) continue;
            var facts = FactsOf(setting, _textSources.FirstOrDefault(t => t.Id == id));
            var verdict = ModelFit.Evaluate(facts, ModelIdOf(setting), skill, spec);
            var marks = ModelFit.Marks(verdict) ?? (verdict.State == FitState.Excluded ? verdict.Reasons : null);
            if (marks is null) continue;
            notes[id] = (verdict.Offered(ModelFit.DropsOnMismatch(spec, facts)) ? "" : "not given to the site: ") + string.Join("; ", marks);
        }

        return notes;
    }

    /// <summary>What the wallet knows of a model it would add behind the visitor's OpenRouter key: only what the catalog says.</summary>
    ModelFacts FactsOfNew(string model) =>
        ModelFacts.Of(new SourceSetting { Kind = SourceKinds.OpenRouter, Model = model }, Catalog.Find(model), "api-key");

    /// <summary>
    /// Whether a source of the visitor's is in the list for a skill and what a site asks of it (null: nothing asked, so only the skill's own
    /// rule counts: a model that does not write text does not answer text). The one question
    /// that a stored choice, a grant and every call ask again each time.
    /// </summary>
    bool Offers(RequestedModels? spec, SourceSetting setting, string skill)
    {
        var facts = FactsOf(setting, _textSources.FirstOrDefault(s => s.Id == setting.Id));
        return ModelFit.Evaluate(facts, ModelIdOf(setting), skill, spec).Offered(ModelFit.DropsOnMismatch(spec, facts));
    }

    ModelRoad? RoadFor(string name)
    {
        if (!HasOpenRouterKey() || name.Contains('*') || !name.Contains('/')) return null;
        if (Catalog.Loaded)
        {
            // With the list in hand a name that is not in it is not a model the wallet can reach; the catalog's own spelling is the one that works.
            return Catalog.Find(name) is { } known && ModelChoice.IsPlainModelName(known.Id) ? new ModelRoad(SourceKinds.OpenRouter, known.Id) : null;
        }

        return ModelChoice.IsPlainModelName(name) ? new ModelRoad(SourceKinds.OpenRouter, name) : null;
    }

    /// <summary>The skills whose models the visitor chooses for a site, in the order their refs are numbered in a grant: text first.</summary>
    static readonly string[] ModelSkills = [Vocabulary.Generate, Vocabulary.ImageGenerate];

    ModelSection? PlanModels(RequestedSkill skill, WalletSettings settings, SiteRecord site)
    {
        if (skill.Models is not { } spec || !ModelSkills.Contains(skill.Id)) return null;
        var previous = site.ModelsFor(skill.Id)?.SourceIds ?? [];
        var picture = skill.Id == Vocabulary.ImageGenerate;
        return ModelPlanner.Plan(skill.Id, spec, new ModelPlanContext(BuildShelf(settings, skill.Id), previous, RoadFor, picture ? CostOfNewPicture : CostOfNew, FactsOfNew));
    }

    /// <summary>
    /// Models a visitor can add behind his OpenRouter key from a site's window: the catalog's, narrowed by what he typed, and judged by what the site
    /// asked (<paramref name="spec"/>) like every other list: one that falls short is left out or shown unticked with the reason, never offered as fit.
    /// </summary>
    public IReadOnlyList<ModelChoice> SearchNewModels(string? query, RequestedModels? spec = null, CatalogFilter filter = CatalogFilter.All, int limit = 30,
        string skill = Vocabulary.Generate)
    {
        if (!HasOpenRouterKey() || !Catalog.Loaded) return [];
        var have = _store.Read(s => s.Sources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal));
        var found = new List<ModelChoice>();
        foreach (var model in Catalog.Search(query, filter, limit + 20, skill))
        {
            if (!ModelChoice.IsPlainModelName(model.Id)) continue;
            if (have.Contains(SourceKinds.OpenRouter + ":" + model.Id)) continue;
            var facts = FactsOfNew(model.Id);
            var verdict = ModelFit.Evaluate(facts, model.Canonical, skill, spec);
            if (!verdict.Offered(ModelFit.DropsOnMismatch(spec, facts))) continue;
            found.Add(new ModelChoice(ModelChoice.NewKey(model.Id), null, ModelNames.Display(model.Id, model.Name), model.Canonical, "OpenRouter key",
                Paid: !model.Free, Cost: skill == Vocabulary.ImageGenerate ? CostOfNewPicture(model.Id) : CostOfNew(model.Id), Role: ModelRole.Yours, Checked: false, Ready: true, Note: null,
                Road: new ModelRoad(SourceKinds.OpenRouter, model.Id), Fit: verdict.State, Marks: ModelFit.Marks(verdict)));
            if (found.Count >= limit) break;
        }

        return found;
    }

    // ------------------------------------------------------------------------------------------------
    // asking, and what the answer makes of the site

    /// <summary>
    /// True when an allowed site asks for models its stored choice no longer covers: it asks something else of them than when the visitor
    /// answered (a changed <c>needs</c> is the site asking for more, never decided by the wallet), or the models he gave it, those that still fit,
    /// are fewer than it needs or lack one it requires.
    /// </summary>
    bool ModelsNeedAsking(SiteRecord site, RequestedManifest manifest, WalletSettings settings) =>
        manifest.Skills.Any(skill => ModelsNeedAsking(site, skill, settings));

    /// <summary>The same for one skill: the window then shows that skill's models and not the others', which the visitor has no reason to look at again.</summary>
    bool ModelsNeedAsking(SiteRecord site, RequestedSkill skill, WalletSettings settings)
    {
        if (site.Status != SiteStatus.Allowed) return false;
        if (skill.Models is not { } spec || !ModelSkills.Contains(skill.Id)) return false;
        var stored = site.ModelsFor(skill.Id);
        if (!string.Equals(stored?.Ask ?? "", spec.Ask, StringComparison.Ordinal)) return true;
        var ids = (stored?.SourceIds ?? []).Select(id => settings.Sources.FirstOrDefault(x => x.Id == id)).OfType<SourceSetting>()
            .Where(setting => Offers(spec, setting, skill.Id)).Select(ModelIdOf).ToList();
        return !ModelPlanner.Satisfies(spec, ids);
    }

    /// <summary>
    /// What a site asks for above the visitor's limits for it, to put to him: shown the first time, and again only when the site asks for
    /// more than it did when he last looked.
    /// </summary>
    LimitsAsk? BuildLimitsAsk(SiteRecord site, RequestedManifest manifest, LimitSettings limits)
    {
        var current = TextLimits.ForSite(limits, site);
        var beyond = TextLimits.Beyond(current, manifest.Limits);
        if (beyond is null) return null;
        if (site.Status == SiteStatus.Allowed && site.Asked is { } seen && !Exceeds(beyond, seen)) return null;
        return new LimitsAsk(beyond, current);
    }

    static bool Exceeds(RequestedLimits now, SiteAsk seen) =>
        now.InputChars > (seen.InputChars ?? 0) || now.AnswerTokens > (seen.AnswerTokens ?? 0) || now.CallSeconds > (seen.CallSeconds ?? 0)
        || now.ParallelCalls > (seen.ParallelCalls ?? 0) || now.CallsPerMinute > (seen.CallsPerMinute ?? 0);

    bool LimitsRaised(SiteRecord site, RequestedManifest manifest, LimitSettings limits) =>
        site.Status == SiteStatus.Allowed && BuildLimitsAsk(site, manifest, limits) is not null;

    static SiteAsk AskOf(RequestedLimits? declared) => new()
    {
        InputChars = declared?.InputChars, AnswerTokens = declared?.AnswerTokens, CallSeconds = declared?.CallSeconds,
        ParallelCalls = declared?.ParallelCalls, CallsPerMinute = declared?.CallsPerMinute,
    };

    /// <summary>
    /// The keys the visitor ticked, kept to what the site can use: known keys only, no repeats, at most as many as the site uses, and null
    /// when what is left does not give the site what it needs (so the skill is left off, not half given).
    /// </summary>
    IReadOnlyList<string>? ChooseModels(ModelSection section, IReadOnlyList<string>? keys)
    {
        var known = section.Choices.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var picked = new List<string>();
        foreach (var key in keys ?? section.Choices.Where(c => c.Checked).Select(c => c.Key).ToList())
        {
            if (picked.Contains(key, StringComparer.Ordinal)) continue;
            if (!known.ContainsKey(key))
            {
                if (!ModelChoice.TryParseNew(key, out var offered)) continue;
                // a model behind his key that the window did not offer (an old search, a made-up key) is judged now, like every other
                var facts = FactsOfNew(offered);
                if (!ModelFit.Evaluate(facts, ModelNames.Canon(offered), section.Skill, section.Spec).Offered(ModelFit.DropsOnMismatch(section.Spec, facts))) continue;
            }

            picked.Add(key);
        }

        if (picked.Count > section.Spec.Max) picked = picked.Take(section.Spec.Max).ToList();
        var ids = picked.Select(k => known.TryGetValue(k, out var c) ? c.ModelId : ModelNames.Canon(ModelChoice.TryParseNew(k, out var m) ? m : k)).ToList();
        return picked.Count >= section.Spec.Min && ModelPlanner.Satisfies(section.Spec, ids) ? picked : null;
    }

    /// <summary>
    /// Makes sure every chosen key is a source: a model behind the visitor's OpenRouter key that he ticked in the window is added to his
    /// models now (not in his default set: a model a site asked for is not a model he wants everywhere). Returns the source id of each key.
    /// </summary>
    Dictionary<string, string> EnsureSources(IEnumerable<string> keys)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var toAdd = new List<string>();
        foreach (var key in keys)
        {
            if (!ModelChoice.TryParseNew(key, out var model))
            {
                map[key] = key;
                continue;
            }

            var id = SourceKinds.OpenRouter + ":" + model;
            map[key] = id;
            toAdd.Add(model);
        }

        if (toAdd.Count > 0 && HasOpenRouterKey())
        {
            _store.Update(s =>
            {
                foreach (var model in toAdd)
                {
                    var id = SourceKinds.OpenRouter + ":" + model;
                    if (s.Sources.Any(x => x.Id == id)) continue;
                    s.Sources.Add(new SourceSetting
                    {
                        Id = id, Kind = SourceKinds.OpenRouter, Model = model, Endpoint = "https://openrouter.ai/api/v1",
                        Skills = [Vocabulary.Generate], Offer = false,
                    });
                }

                Router.EnsureTextChainFromSettings(s);
            });
        }

        return map;
    }

    // ------------------------------------------------------------------------------------------------
    // what the site is given

    bool ShowNamesFor(SiteRecord? site, RequestedManifest manifest) =>
        (site?.ShowModelNames ?? true) && manifest.Skills.Any(k => k.Models?.Names == true);

    /// <summary>
    /// The models a site was given, per skill: the visitor's choice for it, one entry per model (two roads to the same model are one
    /// entry, the visitor's choice first), referred to as m1, m2... Refs are unique within the grant (PROPOSAL-v3 3.3): the text entries are
    /// m1 to mk and the picture entries go on from m(k+1), so a model ticked for both has one ref in each list. Empty for a site that asked for
    /// no models. A model that no longer fits what the site asks (the fit engine is asked again each time) is not given, and each one that is
    /// given carries the verdicts the site may be told.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<GrantedModel>> BuildGranted(SiteRecord? site, RequestedManifest manifest, WalletSettings settings)
    {
        var result = new Dictionary<string, IReadOnlyList<GrantedModel>>(StringComparer.Ordinal);
        if (site is null) return result;
        var next = 1;
        foreach (var skillId in ModelSkills)
        {
            if (manifest.Skill(skillId) is not { Models: { } spec } skill) continue;
            var list = new List<GrantedModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in site.ModelsFor(skill.Id)?.SourceIds ?? [])
            {
                var setting = settings.Sources.FirstOrDefault(s => s.Id == id && s.Kind is not null);
                if (setting is null) continue;
                var modelId = ModelIdOf(setting);
                var source = _textSources.FirstOrDefault(s => s.Id == id);
                var facts = FactsOf(setting, source);
                var verdict = ModelFit.Evaluate(facts, modelId, skill.Id, spec);
                if (!verdict.Offered(ModelFit.DropsOnMismatch(spec, facts)) || !seen.Add(modelId)) continue;
                var roads = new List<string> { id };
                foreach (var other in settings.Sources.Where(s => s.Kind is not null && s.Enabled && s.Id != id
                             && (s.Skills.Count == 0 || s.Skills.Contains(Vocabulary.Generate)) && ModelIdOf(s) == modelId && Offers(spec, s, skill.Id)))
                    roads.Add(other.Id);
                var name = setting.Label is { Length: > 0 } label ? label : ModelNames.Display(setting.Model, Catalog.Find(modelId)?.Name);
                var abilities = facts.Agent && spec.Abilities is { Count: > 0 } asked ? AgentProfile.Verdicts(asked) : null;
                list.Add(new GrantedModel("m" + (next++).ToString(CultureInfo.InvariantCulture), roads, name, modelId,
                    source is null ? "api-key" : ProviderReceipt(source), source?.Paid ?? true, verdict.Can, abilities));
            }

            result[skill.Id] = list;
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // the visitor changes what a site has

    /// <summary>Sites → the models of one site: replaces the list (ids of the visitor's models, in his order). The site's live grants follow at once.</summary>
    public void SetSiteModels(string origin, string skill, IReadOnlyList<string> sourceIds)
    {
        _store.Update(s =>
        {
            var record = Site(s, origin);
            var entry = record.ModelsFor(skill);
            if (entry is null)
            {
                entry = new SiteModels { Skill = skill };
                record.Models.Add(entry);
            }

            entry.SourceIds = sourceIds.Distinct(StringComparer.Ordinal).ToList();
        });
        RefreshLiveGrants(origin);
        SitesChanged?.Invoke();
    }

    /// <summary>Whether a site sees the names of its models. Its live grants follow at once.</summary>
    public void SetShowModelNames(string origin, bool show)
    {
        _store.Update(s => Site(s, origin).ShowModelNames = show);
        RefreshLiveGrants(origin);
        SitesChanged?.Invoke();
    }

    /// <summary>The visitor changed a limit for one site (<paramref name="change"/> edits its record), or the defaults for all (null origin): live grants follow at once.</summary>
    public void LimitsChanged(string? origin = null)
    {
        if (origin is not null)
        {
            RefreshLiveGrants(origin);
            return;
        }

        foreach (var site in _sessions.Values.Select(s => s.Origin).Distinct(StringComparer.Ordinal).ToList()) RefreshLiveGrants(site);
    }

    /// <summary>The visitor changed what one of his models can do: every live grant is built again at once (a model that no longer fits goes out of it, the verdicts the site is told follow).</summary>
    public void SourcePropertiesChanged() => LimitsChanged();

    void RefreshLiveGrants(string origin)
    {
        var settings = _store.Snapshot();
        var site = settings.Site(origin);
        foreach (var session in _sessions.Values.Where(s => s.Origin == origin).ToList())
        {
            session.Models = BuildGranted(site, session.Manifest, settings);
            session.ShowNames = ShowNamesFor(site, session.Manifest);
            session.Limits = TextLimits.Effective(settings.Limits, site, session.Manifest.Limits);
        }

        GrantChanged?.Invoke(origin);
    }
}
