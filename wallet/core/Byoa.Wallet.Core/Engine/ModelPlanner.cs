using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// Works out which models to tick for a site that asks for several: first what it cannot work without, then what the visitor gave
/// it last time, then what it recommends and the visitor has, then the visitor's own default set; models behind a key he has but
/// has not added come in only when too few remain to reach the site's minimum. The visitor can change every tick; this is the
/// one-click answer.
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
        public bool IsNew => SourceId is null;
    }

    public static ModelSection Plan(string skill, RequestedModels spec, ModelPlanContext context)
    {
        var entries = new List<Entry>();
        var order = 0;
        foreach (var shelf in context.Shelf)
        {
            var entry = new Entry
            {
                Key = shelf.SourceId, SourceId = shelf.SourceId, Name = shelf.Name, ModelId = shelf.ModelId, Where = shelf.Where,
                Paid = shelf.Paid, Cost = shelf.Cost, Ready = shelf.Ready, Note = shelf.NotReady, Offer = shelf.Offer, Agent = shelf.Agent, Order = order++,
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
            entries.Add(new Entry
            {
                Key = ModelChoice.NewKey(road.Model), Name = ModelNames.Display(road.Model), ModelId = ModelNames.Canon(road.Model),
                Where = "OpenRouter key", Paid = true, Cost = context.CostOf(road.Model), Role = role, Rank = rank < 0 ? 0 : rank,
                Order = order++, Ready = true, Road = road,
            });
        }

        var pickedCount = 0;
        void Pick(Entry e)
        {
            if (e.Picked) return;
            e.Picked = true;
            e.PickedAt = pickedCount++;
        }

        // 1. What the site cannot work without.
        var missing = new List<string>();
        foreach (var wish in spec.Require)
        {
            var have = entries.Where(e => e.Ready && wish.AnyOf.Any(p => ModelNames.Matches(p, e.ModelId))).ToList();
            if (have.Any(e => e.Picked)) continue;                   // an earlier pick already satisfies it
            var best = have.OrderBy(e => e.IsNew ? 1 : 0).ThenBy(e => e.Order).FirstOrDefault();
            if (best is null) missing.Add(string.Join(" or ", wish.AnyOf));
            else Pick(best);
        }

        var max = Math.Max(spec.Max, pickedCount);
        bool Room() => pickedCount < max;
        bool Fresh(Entry e, bool byVendor) =>
            !byVendor || !entries.Any(x => x.Picked && ModelNames.Vendor(x.ModelId) == ModelNames.Vendor(e.ModelId));

        // 2. What the visitor gave this site last time (still there and able to answer).
        foreach (var id in context.Previous)
        {
            var e = entries.FirstOrDefault(x => x.SourceId == id && x.Ready);
            if (e is not null && Room()) Pick(e);
        }

        // 3. The site's recommendations the visitor has, in the site's order; 4. his default set, but only as far as the site's minimum:
        //    a window that ticks every model he owns makes the site slower and dearer than it asked to be, and he can tick more himself.
        foreach (var vendorPass in spec.ByVendor ? new[] { true, false } : new[] { false })
        {
            // (his agent is never ticked because a site likes it: only a site that cannot work without it, or he himself, ticks it)
            foreach (var e in entries.Where(x => x.Ready && !x.IsNew && !x.Agent && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank).ThenBy(x => x.Order))
                if (Room() && Fresh(e, vendorPass)) Pick(e);
            foreach (var e in entries.Where(x => x.Ready && !x.IsNew && x.Offer).OrderBy(x => x.Order))
                if (pickedCount < spec.Min && Room() && Fresh(e, vendorPass)) Pick(e);
            if (pickedCount >= spec.Min) break;                      // a second pass without the vendor rule only when there are too few
        }

        // 5. Too few: the site's recommendations behind a key (in its order), then any other model of the visitor's that can answer.
        if (pickedCount < spec.Min)
        {
            foreach (var e in entries.Where(x => x.Ready && x.IsNew && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank))
                if (pickedCount < spec.Min && Fresh(e, spec.ByVendor)) Pick(e);
            foreach (var e in entries.Where(x => x.Ready && !x.IsNew && !x.Agent).OrderBy(x => x.Order))
                if (pickedCount < spec.Min) Pick(e);
            foreach (var e in entries.Where(x => x.Ready && x.IsNew && x.Role == ModelRole.Recommended).OrderBy(x => x.Rank))
                if (pickedCount < spec.Min) Pick(e);
        }

        string? shortfall = pickedCount < spec.Min
            ? "needs at least " + spec.Min + " different models; " + (pickedCount == 0 ? "none" : pickedCount.ToString()) + " can answer"
            : null;

        // What the window shows: the ticked ones first (in the order they were picked), then the other recommendations, then the rest.
        var shown = entries
            .OrderBy(e => e.Picked ? 0 : 1)
            .ThenBy(e => e.Picked ? e.PickedAt : 0)
            .ThenBy(e => e.Role == ModelRole.Required ? 0 : e.Role == ModelRole.Recommended ? 1 : 2)
            .ThenBy(e => e.Rank)
            .ThenBy(e => e.Order)
            .Select(e => new ModelChoice(e.Key, e.SourceId, e.Name, e.ModelId, e.Where, e.Paid, e.Cost, e.Role, e.Picked, e.Ready, e.Note, e.Road))
            .ToList();
        return new ModelSection(skill, spec, shown, missing, shortfall);
    }

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
