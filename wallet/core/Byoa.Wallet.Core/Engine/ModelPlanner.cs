using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// Works out which models to tick for a site that asks for several: first what it cannot work without, then what the visitor gave
/// it last time, then what it recommends and the visitor has, then the visitor's own default set; models behind a key he has but
/// has not added come in only when too few remain to reach the site's minimum. The visitor can change every tick; this is the
/// one-click answer. Only a model that fits (<see cref="ModelFit"/>) is ever ticked: one whose properties the wallet does not know, or that
/// falls short of something the site asked in a way the site wants shown, is in the list and not ticked; the rest are left out and counted.
/// </summary>
public static class ModelPlanner
{
    sealed class Entry
    {
        public string Key = "";
        public string? SourceId;
        public string Name = "";
        public string ModelId = "";
        public string Where = "";
        public bool Paid;
        public string Cost = "";
        public ModelRole Role;
        public int Rank = int.MaxValue;         // the place in the site's recommendations
        public int Order;                       // the place on the visitor's shelf
        public bool Ready;
        public string? Note;
        public bool Offer;
        public bool Agent;
        public ModelRoad? Road;
        public bool Picked;
        public int PickedAt;
        public FitVerdict Fit = null!;
        public bool IsNew => SourceId is null;

        /// <summary>The wallet may tick it: it can answer and it fits.</summary>
        public bool Pickable => Ready && Fit.Fits;

        /// <summary>The visitor ticked it last time and nothing known is wrong with it now.</summary>
        public bool Restorable => Ready && Fit.State is FitState.Fits or FitState.Unknown;
    }

    public static ModelSection Plan(string skill, RequestedModels spec, ModelPlanContext context)
    {
        var entries = new List<Entry>();
        var notFitting = new List<ModelMiss>();
        var order = 0;
        foreach (var shelf in context.Shelf)
        {
            var facts = shelf.Facts ?? ModelFacts.Unknown;
            var verdict = ModelFit.Evaluate(facts, shelf.ModelId, skill, spec);
            if (!verdict.Offered(ModelFit.DropsOnMismatch(spec, facts)))
            {
                notFitting.Add(new ModelMiss(shelf.Name, string.Join("; ", verdict.Reasons), shelf.ModelId));
                continue;
            }

            var entry = new Entry
            {
                Key = shelf.SourceId, SourceId = shelf.SourceId, Name = shelf.Name, ModelId = shelf.ModelId, Where = shelf.Where,
                Paid = shelf.Paid, Cost = shelf.Cost, Ready = shelf.Ready, Note = shelf.NotReady, Offer = shelf.Offer, Agent = shelf.Agent, Order = order++,
                Fit = verdict,
            };
            if (WishOf(spec, shelf.ModelId) is not null) entry.Role = ModelRole.Required;
            else if (RankOf(spec, shelf.ModelId) is var rank and >= 0)
            {
                entry.Role = ModelRole.Recommended;
                entry.Rank = rank;
            }
            else entry.Role = ModelRole.Yours;
            entries.Add(entry);
        }

        // Models the site named that the visitor does not have, but could have behind a key he gave: only exact names, never a mask.
        var named = spec.Require.SelectMany(w => w.AnyOf.Select(name => (name, ModelRole.Required, -1)))
            .Concat(spec.Recommend.Select((name, i) => (name, ModelRole.Recommended, i)));
        foreach (var (name, role, rank) in named)
        {
            if (name.Contains('*')) continue;
            if (entries.Any(e => ModelNames.Matches(name, e.ModelId))) continue;
            if (context.RoadFor(name) is not { } road) continue;
            if (entries.Any(e => e.Key == ModelChoice.NewKey(road.Model))) continue;
            // (a model behind a key is judged by what the catalog says of it, and has no "also use as a text model" tick: it is not his yet)
            var facts = context.FactsOf?.Invoke(road.Model) ?? ModelFacts.Unknown;
            var verdict = ModelFit.Evaluate(facts, ModelNames.Canon(road.Model), skill, spec);
            if (!verdict.Offered(ModelFit.DropsOnMismatch(spec, facts)))
            {
                notFitting.Add(new ModelMiss(ModelNames.Display(road.Model), string.Join("; ", verdict.Reasons), ModelNames.Canon(road.Model)));
                continue;
            }

            entries.Add(new Entry
            {
                Key = ModelChoice.NewKey(road.Model), Name = ModelNames.Display(road.Model), ModelId = ModelNames.Canon(road.Model),
                Where = "OpenRouter key", Paid = true, Cost = context.CostOf(road.Model), Role = role, Rank = rank < 0 ? 0 : rank,
                Order = order++, Ready = true, Road = road, Fit = verdict,
            });
        }

        var pickedCount = 0;
        void Pick(Entry e)
        {
            if (e.Picked) return;
            e.Picked = true;
            e.PickedAt = pickedCount++;
        }

        // 1. What the site cannot work without. (A model of the visitor's that matches but does not surely fit is there for him to tick:
        //    the wish is not missing, and the wallet does not tick it for him.)
        var missing = new List<string>();
        foreach (var wish in spec.Require)
        {
            var have = entries.Where(e => e.Ready && wish.AnyOf.Any(p => ModelNames.Matches(p, e.ModelId))).ToList();
            if (have.Any(e => e.Picked)) continue;                   // an earlier pick already satisfies it
            if (have.Count == 0) missing.Add(string.Join(" or ", wish.AnyOf));
            else if (have.Where(e => e.Pickable).OrderBy(e => e.IsNew ? 1 : 0).ThenBy(e => e.Order).FirstOrDefault() is { } best) Pick(best);
        }

        var max = Math.Max(spec.Max, pickedCount);
        bool Room() => pickedCount < max;
        bool Fresh(Entry e, bool byVendor) =>
            !byVendor || !entries.Any(x => x.Picked && ModelNames.Vendor(x.ModelId) == ModelNames.Vendor(e.ModelId));

        // 2. What the visitor gave this site last time (still there and able to answer).
        foreach (var id in context.Previous)
        {
            var e = entries.FirstOrDefault(x => x.SourceId == id && x.Restorable);
            if (e is not null && Room()) Pick(e);
        }

        // 3. The site's recommendations the visitor has, in the site's order; 4. his default set, but only as far as the site's minimum:
        //    a window that ticks every model he owns makes the site slower and dearer than it asked to be, and he can tick more himself.
        foreach (var vendorPass in spec.ByVendor ? new[] { true, false } : new[] { false })
        {
            // (his agent is never ticked because a site likes it: only a site that cannot work without it, or he himself, ticks it)
            foreach (var e in entries.Where(x => x.Pickable && !x.IsNew && !x.Agent && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank).ThenBy(x => x.Order))
                if (Room() && Fresh(e, vendorPass)) Pick(e);
            foreach (var e in entries.Where(x => x.Pickable && !x.IsNew && x.Offer).OrderBy(x => x.Order))
                if (pickedCount < spec.Min && Room() && Fresh(e, vendorPass)) Pick(e);
            if (pickedCount >= spec.Min) break;                      // a second pass without the vendor rule only when there are too few
        }

        // 5. Too few: the site's recommendations behind a key (in its order), then any other model of the visitor's that can answer.
        if (pickedCount < spec.Min)
        {
            foreach (var e in entries.Where(x => x.Pickable && x.IsNew && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank))
                if (pickedCount < spec.Min && Fresh(e, spec.ByVendor)) Pick(e);
            foreach (var e in entries.Where(x => x.Pickable && !x.IsNew && !x.Agent).OrderBy(x => x.Order))
                if (pickedCount < spec.Min) Pick(e);
            foreach (var e in entries.Where(x => x.Pickable && x.IsNew && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank))
                if (pickedCount < spec.Min) Pick(e);
        }

        // Too few is judged on the models the visitor could tick, not only the ones the wallet ticked: a model whose properties are not known
        // is his to tick, and the window keeps Allow off until he has. His agent counts when the site asks for an agent (its kind, or its name).
        var tickable = pickedCount + entries.Count(x => !x.Picked && x.Ready && (!x.Agent || spec.Kind != RequestedModels.ModelKind || x.Role != ModelRole.Yours));
        string? shortfall = tickable < spec.Min
            ? "needs at least " + spec.Min + " different models; " + (tickable == 0 ? "none" : tickable.ToString()) + " can answer"
            : null;

        // What the window shows: the ticked ones first (in the order they were picked), then the other recommendations, then the rest.
        var shown = entries
            .OrderBy(e => e.Picked ? 0 : 1)
            .ThenBy(e => e.Picked ? e.PickedAt : 0)
            .ThenBy(e => e.Role == ModelRole.Required ? 0 : e.Role == ModelRole.Recommended ? 1 : 2)
            .ThenBy(e => e.Rank)
            .ThenBy(e => e.Order)
            .Select(e => new ModelChoice(e.Key, e.SourceId, e.Name, e.ModelId, e.Where, e.Paid, e.Cost, e.Role, e.Picked, e.Ready, e.Note, e.Road,
                e.Fit.State, ModelFit.Marks(e.Fit)))
            .ToList();
        // The names the site likes that nothing of the visitor's answers to (shown or left out): examples for him, with a way to add one.
        var examples = spec.Require.SelectMany(w => w.AnyOf).Concat(spec.Recommend).Distinct(StringComparer.Ordinal)
            .Where(name => !entries.Any(e => ModelNames.Matches(name, e.ModelId)) && !notFitting.Any(m => m.ModelId.Length > 0 && ModelNames.Matches(name, m.ModelId)))
            .Take(MaxExamples).ToList();
        return new ModelSection(skill, spec, shown, missing, shortfall, notFitting, examples);
    }

    /// <summary>How many names the window shows as examples.</summary>
    public const int MaxExamples = 4;

    static int RankOf(RequestedModels spec, string modelId)
    {
        for (var i = 0; i < spec.Recommend.Count; i++)
        {
            if (ModelNames.Matches(spec.Recommend[i], modelId)) return i;
        }

        return -1;
    }

    static ModelWish? WishOf(RequestedModels spec, string modelId) =>
        spec.Require.FirstOrDefault(w => w.AnyOf.Any(p => ModelNames.Matches(p, modelId)));

    /// <summary>Whether a stored choice still satisfies what the site asks now: enough models, and every required one among them.</summary>
    public static bool Satisfies(RequestedModels spec, IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count < spec.Min) return false;
        return spec.Require.All(w => modelIds.Any(id => w.AnyOf.Any(p => ModelNames.Matches(p, id))));
    }
}
