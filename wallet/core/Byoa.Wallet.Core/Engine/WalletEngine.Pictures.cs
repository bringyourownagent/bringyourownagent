using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BringYourOwnAgent;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// Serving <c>image.generate</c> (PROPOSAL-v3 3.1, 3.2 and 4.4): a site that was given a picture model by name asks it for pictures, through the
/// visitor's OpenRouter key. The call goes through the same gate, daily limits, "ask before paid", time limit and cancel as a text call;
/// what comes back is checked, made smaller when the site asked (or the default says) and handed to whoever carries the answer, as bytes
/// beside a result that only describes them.
/// </summary>
public sealed partial class WalletEngine
{
    /// <summary>
    /// Makes a picture smaller. The desktop app supplies one (it has a picture library; the core has none). With none, a picture goes through only
    /// when nothing about it needs changing that can be seen without one: it is within the byte limit and the site named no side.
    /// </summary>
    public IPictureShrinker? PictureShrinker { get; set; }

    readonly object _pictureGate = new();
    readonly Dictionary<string, List<DateTimeOffset>> _pictureTimes = new(StringComparer.Ordinal);

    /// <summary>Takes <paramref name="count"/> places in the site's pictures of this minute (20 a minute); false, and none taken, when they are not there.</summary>
    bool TakePictureSlots(string origin, int count)
    {
        lock (_pictureGate)
        {
            var now = Now;
            if (!_pictureTimes.TryGetValue(origin, out var times)) _pictureTimes[origin] = times = [];
            times.RemoveAll(t => now - t >= TimeSpan.FromMinutes(1));
            if (times.Count + count > Pictures.PicturesPerMinute) return false;
            for (var i = 0; i < count; i++) times.Add(now);
            return true;
        }
    }

    /// <summary>Gives back places for pictures that never came (the call asked for four and got two).</summary>
    void GivePictureSlots(string origin, int count)
    {
        if (count <= 0) return;
        lock (_pictureGate)
        {
            if (_pictureTimes.TryGetValue(origin, out var times)) times.RemoveRange(Math.Max(0, times.Count - count), Math.Min(count, times.Count));
        }
    }

    /// <summary>What a picture call asked, after the wallet has checked it.</summary>
    sealed record PictureAsk(ImageRequest Request, int? MaxSide, int MaxBytes);

    async Task<JsonNode?> ImageCallAsync(ActivityEntry entry, GrantSession session, SiteRecord site, JsonObject payload, long started, CancellationToken ct,
        ICollection<byte[]>? pictures)
    {
        // The answer is bytes beside a result: a caller that cannot carry them (no collector) is refused before anything is spent.
        if (pictures is null) return Failed(entry, "failed", "this transport carries no pictures");
        if (!session.Skills.Contains(Vocabulary.ImageGenerate) || !session.Models.TryGetValue(Vocabulary.ImageGenerate, out var granted))
            return Failed(entry, "failed", "no picture models were granted");

        if (payload["input"] is not JsonObject input) return Failed(entry, "invalid", "no input");
        var textLimits = session.Limits ?? TextLimits.Defaults;
        if (Str(input["prompt"]) is not { } prompt || string.IsNullOrWhiteSpace(prompt)) return Failed(entry, "invalid", "no prompt in the input");
        if (prompt.Length > textLimits.InputChars)
            return Failed(entry, "invalid", "text too long for the wallet: at most " + textLimits.InputChars + " characters", reason: CallReason.Limit);

        var count = input["count"] is null ? 1 : Int(input["count"]) ?? 0;
        if (count is < 1 or > Pictures.MaxCount) return Failed(entry, "invalid", "count must be 1 to " + Pictures.MaxCount);
        string? aspect = null;
        if (input["aspect"] is { } aspectNode)
        {
            aspect = Str(aspectNode);
            if (aspect is null || !Pictures.Aspects.Contains(aspect)) return Failed(entry, "invalid", "aspect is not one of " + string.Join(", ", Pictures.Aspects.Order()));
        }

        int? maxSide = Int(input["maxSide"]) is { } side ? Math.Clamp(side, Pictures.MinSide, Pictures.MaxSide) : null;
        var maxBytes = Int(input["maxBytes"]) is { } bytes ? Math.Clamp(bytes, Pictures.MinBytes, Pictures.MaxBytes) : Pictures.DefaultBytes;

        var references = new List<Picture>();
        if (input["references"] is { } referencesNode)
        {
            if (referencesNode is not JsonArray list) return Failed(entry, "invalid", "references is not a list");
            if (list.Count > Pictures.MaxReferences) return Failed(entry, "limit", "more than " + Pictures.MaxReferences + " references", reason: CallReason.Limit);
            var total = 0;
            foreach (var item in list)
            {
                if (Str(item?["data"]) is not { } data) return Failed(entry, "invalid", "a reference has no data");
                if (data.Length > Pictures.MaxReferenceBytes / 3 * 4 + 4) return Failed(entry, "limit", "a reference is over " + Pictures.MaxReferenceBytes + " bytes", reason: CallReason.Limit);
                var decoded = new byte[data.Length / 4 * 3 + 3];
                if (!Convert.TryFromBase64String(data, decoded, out var written)) return Failed(entry, "invalid", "a reference is not base64");
                if (written > Pictures.MaxReferenceBytes) return Failed(entry, "limit", "a reference is over " + Pictures.MaxReferenceBytes + " bytes", reason: CallReason.Limit);
                total += written;
                if (total > Pictures.MaxReferencesBytes) return Failed(entry, "limit", "the references are over " + Pictures.MaxReferencesBytes + " bytes together", reason: CallReason.Limit);
                // what it is comes from its bytes, never from the mime the site wrote
                if (Pictures.Detect(decoded.AsSpan(0, written)) is not { } mime) return Failed(entry, "invalid", "a reference is not a PNG, JPEG, WebP or GIF picture");
                references.Add(new Picture(mime, decoded.AsSpan(0, written).ToArray()));
            }
        }

        // A site reaches only the models it was given, by the ref it was told; a call that names none has no route of the wallet's own to fall back on.
        var parameters = payload["params"] as JsonObject ?? new JsonObject();
        if (Str(parameters["model"]) is not { } wanted) return Failed(entry, "invalid", "no model named for the pictures");
        var pick = granted.Where(g => g.Ref == wanted).ToList();
        if (pick.Count == 0) return Failed(entry, "invalid", "no such picture model for this site: " + wanted);
        // The fit is asked again at every call, as for text; and a model that does not read pictures is not sent any to look at.
        var spec = session.Manifest.Skill(Vocabulary.ImageGenerate)?.Models;
        var fitting = _store.Read(s => pick.SelectMany(g => g.SourceIds).Distinct(StringComparer.Ordinal)
            .Where(id => s.Sources.FirstOrDefault(x => x.Id == id) is { } setting && Offers(spec, setting, Vocabulary.ImageGenerate)
                && (references.Count == 0 || FactsOf(setting, _textSources.FirstOrDefault(t => t.Id == id)).Input?.Contains(Modalities.Image) == true))
            .ToHashSet(StringComparer.Ordinal));
        if (fitting.Count == 0) return Failed(entry, "failed", references.Count > 0 ? "the model does not read pictures" : "the model no longer fits this site");
        var chain = new SkillChain
        {
            Skill = Vocabulary.ImageGenerate,
            Stops = pick.SelectMany(g => g.SourceIds).Distinct(StringComparer.Ordinal).Where(fitting.Contains)
                .Select(id => new ChainStop { SourceId = id, AskBeforePaid = true }).ToList(),
        };
        var sources = _textSources.Where(s => s is IImageSource && fitting.Contains(s.Id)).ToList();

        if (!TakePictureSlots(session.Origin, count)) return Failed(entry, "limit", "more than " + Pictures.PicturesPerMinute + " pictures a minute", reason: CallReason.Limit);
        var delivered = 0;
        try
        {
            // The per-day limits of the wallet: the same for a picture call as for a text call.
            var limits = _store.Read(s => (
                SitePerDay: Money.Parse(site.PerDay ?? s.Limits.SitePerDay, 0.20m),
                CallsPerDay: site.CallsPerDay ?? s.Limits.SiteCallsPerDay,
                WalletPerDay: Money.Parse(s.Limits.WalletPerDay, 1.00m)));
            var hold = new DailyHold(session.Origin);
            if (!HoldCall(hold, limits.CallsPerDay))
            {
                TellLimit(session, site, "calls per day", $"{limits.CallsPerDay} calls a day for this site");
                return Failed(entry, "limit", "calls per day for this site");
            }

            try
            {
                var ask = new PictureAsk(new ImageRequest(prompt, references, count, aspect), maxSide, maxBytes);
                var answer = await RoutePicturesAsync(entry, session, site, ask, chain, sources, wanted, limits.CallsPerDay, limits.SitePerDay, limits.WalletPerDay,
                    hold, started, ct, pictures).ConfigureAwait(false);
                if (answer["ok"]?.GetValue<bool>() == true) delivered = answer["output"]!["images"]!.AsArray().Count;
                // A cancel that arrived as the route ended is a cancel, not a failure of the route (the site is told so).
                if (ct.IsCancellationRequested && delivered == 0) ct.ThrowIfCancellationRequested();
                return answer;
            }
            finally
            {
                ReleaseDaily(hold);
            }
        }
        finally
        {
            GivePictureSlots(session.Origin, count - delivered);
        }
    }

    async Task<JsonObject> RoutePicturesAsync(
        ActivityEntry entry, GrantSession session, SiteRecord site, PictureAsk ask, SkillChain chain, IReadOnlyList<ITextSource> sources, string modelRef,
        int callsPerDay, decimal sitePerDay, decimal walletPerDay, DailyHold hold, long started, CancellationToken ct, ICollection<byte[]> pictures)
    {
        using var routeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var routeBudget = TextRouteTimeout ?? TimeSpan.FromSeconds((session.Limits ?? TextLimits.Defaults).CallSeconds);
        var sourceTimeout = TextSourceTimeout ?? routeBudget;
        if (routeBudget > TimeSpan.Zero) routeTimeout.CancelAfter(routeBudget);
        var routeDeadline = routeBudget > TimeSpan.Zero
            ? Stopwatch.GetTimestamp() + (long)(routeBudget.TotalSeconds * Stopwatch.Frequency)
            : long.MaxValue;
        var routeCt = routeTimeout.Token;
        var settings = _store.Snapshot();
        var plan = Router.PlanText(chain, sources, id => IsEnabled(settings, id));
        int Index(ITextSource s) => chain.Stops.FindIndex(x => x.SourceId == s.Id);

        var route = new Route();
        foreach (var k in plan.Skipped.Where(k => k.Why == "turned off")) route.Add(Index(k.Source), k.Source.Name + ": turned off");
        var day = new DayLimits(sitePerDay, walletPerDay);
        var request = ask.Request;
        string? stopReason = null;
        var droppedForSize = false;
        decimal totalCost = 0;

        try
        {
            foreach (var step in await OnlyLoadedAsync(plan.Steps, s => s.Source, (s, why) => route.Add(Index((ITextSource)s), ((ITextSource)s).Name + ": " + why), routeCt).ConfigureAwait(false))
            {
                if (routeCt.IsCancellationRequested || (routeBudget > TimeSpan.Zero && Remaining(routeDeadline) <= TimeSpan.Zero)) break;
                var source = (IImageSource)step.Source;
                if (entry.ProviderCalled && !HoldCall(hold, callsPerDay))
                {
                    stopReason ??= "calls per day";
                    route.Add(Index(source), source.Name + ": calls per day limit");
                    break;
                }

                // A paid source answers a few at once, a free one in turn, as for text; a call waits a moment for a busy source and goes to the next road.
                var busy = _busy.GetOrAdd(source.Id, _ => source.Paid ? new SemaphoreSlim(4, 4) : new SemaphoreSlim(1, 1));
                var busyWait = routeBudget > TimeSpan.Zero ? Min(BusyWait, Remaining(routeDeadline)) : BusyWait;
                if (busyWait <= TimeSpan.Zero || !await busy.WaitAsync(busyWait, routeCt).ConfigureAwait(false))
                {
                    if (entry.ProviderCalled) ReleaseHeldCall(hold);
                    route.Add(Index(source), source.Name + ": busy (one call at a time)");
                    continue;
                }

                try
                {
                    decimal each;
                    try { each = SafeEstimate(source.EstimateImageCost(request), source.Paid); }
                    catch (Exception e) when (e is not OutOfMemoryException) { each = source.Paid ? FlatTextEstimate : 0; }
                    var estimate = Math.Min(MaxTextCost, each * request.Count);

                    var reserved = await ReserveAsync(entry, session, site, source, step, Vocabulary.ImageGenerate, request.Prompt.Length, estimate, hold, day, route, Index(source), routeCt, ct).ConfigureAwait(false);
                    if (reserved.Failure is not null) return reserved.Failure;
                    if (reserved.Skipped)
                    {
                        stopReason ??= reserved.StopReason;
                        continue;
                    }

                    var ran = Stopwatch.GetTimestamp();
                    var made = await DrawAsync(source, request, each, routeCt, routeDeadline, ct, sourceTimeout, entry, session, reserved, hold).ConfigureAwait(false);
                    totalCost = SafeAdd(totalCost, made.Cost);
                    if (made.Pictures.Count == 0)
                    {
                        route.Add(Index(source), source.Name + ": " + (made.Failure ?? "did not draw"));
                        continue;
                    }

                    var kept = FinishPictures(made.Pictures, ask, out var tooBig);
                    droppedForSize |= tooBig;
                    entry.Source = source.Name;
                    entry.Cost = Money.ToSpec(totalCost);
                    entry.Milliseconds = (int)Stopwatch.GetElapsedTime(ran).TotalMilliseconds;
                    var before = route.Before(Index(source));
                    entry.Fallback = before.Count > 0 ? string.Join(" → ", before) + " → " + source.Name : null;
                    if (kept.Count == 0)
                    {
                        // What the model drew was dropped (too big to be made to fit, or not a picture): the money is spent, the site gets no picture.
                        entry.Note = droppedForSize ? "no picture fit the limits" : "no picture was a PNG, JPEG, WebP or GIF";
                        return FailedObject(entry, droppedForSize ? "limit" : "failed", entry.Note, route.All());
                    }

                    entry.Outcome = "ok";
                    entry.Note = JoinNotes(kept.Count + (kept.Count == 1 ? " picture" : " pictures"), kept.Any(p => p.Reduced) ? "made smaller" : null);
                    Log(entry);
                    var images = new JsonArray();
                    foreach (var picture in kept)
                    {
                        pictures.Add(picture.Bytes);
                        var described = new JsonObject
                        {
                            ["mime"] = picture.Mime,
                            ["size"] = picture.Bytes.Length,
                            ["sha256"] = Convert.ToHexString(SHA256.HashData(picture.Bytes)).ToLowerInvariant(),
                        };
                        if (picture.Reduced) described["reduced"] = true;
                        images.Add(described);
                    }

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["output"] = new JsonObject { ["images"] = images },
                        ["receipt"] = new JsonObject
                        {
                            ["cost"] = made.Reported is { } reported ? Money.ToReceipt(reported) : "unknown",
                            ["provider"] = ProviderReceipt(source),
                            ["model"] = modelRef,
                        },
                    };
                }
                finally
                {
                    busy.Release();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // (the route's own time ran out; the caller did not cancel)
        }

        // The call's own time (its maxCallSeconds) was used up: the site is told "timeout", and every slot the call held is free again.
        var timedOut = routeBudget > TimeSpan.Zero && ((routeCt.IsCancellationRequested && !ct.IsCancellationRequested) || Remaining(routeDeadline) <= RouteDeadlineSlack);
        if (timedOut) stopReason = "picture route timed out";
        entry.Milliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        entry.Cost = Money.ToSpec(totalCost);
        return stopReason is not null
            ? FailedObject(entry, "limit", stopReason, route.All(), timedOut ? CallReason.Timeout : null)
            : FailedObject(entry, "failed", plan.Steps.Count == 0 ? "no source can do image.generate" : "no source drew a picture", route.All());
    }

    /// <summary>The day limits of the site and of the wallet as a call raises them (the visitor may be asked to).</summary>
    sealed class DayLimits(decimal site, decimal wallet)
    {
        public decimal Site { get; set; } = site;
        public decimal Wallet { get; set; } = wallet;
    }

    /// <summary>What <see cref="ReserveAsync"/> decided about one source: go on (with what is reserved), leave it for the next road, or end the call.</summary>
    readonly record struct Reserved(JsonObject? Failure, bool Skipped, string? StopReason, DateTimeOffset At, decimal Estimate, Decision Decision, string Codes);

    /// <summary>
    /// Everything that comes before a paid or counted call: the money per day (the visitor may be asked to raise it), the budget per session, the gate
    /// of the rules library (rate, text size, budget), and "ask before paid" for a source that asks. The slot and the estimate are reserved when it
    /// goes on; every way out of it gives back what it took.
    /// </summary>
    async Task<Reserved> ReserveAsync(ActivityEntry entry, GrantSession session, SiteRecord site, ITextSource source, TextRouteStep step, string skill, int textChars,
        decimal estimate, DailyHold hold, DayLimits day, Route route, int index, CancellationToken routeCt, CancellationToken callerCt)
    {
        if (estimate > 0)
        {
            var held = await HoldMoneyOrRaiseAsync(session, site, hold, estimate, day.Site, day.Wallet, routeCt).ConfigureAwait(false);
            (day.Site, day.Wallet) = (held.SitePerDay, held.WalletPerDay);
            if (!held.Held)
            {
                route.Add(index, source.Name + ": over the daily limit");
                return new Reserved(null, true, "money per day", default, estimate, Decision.Blocked, "");
            }
        }

        // A call that would pass the budget per session asks the visitor to raise it first (the gate below holds it to the budget either way)
        if (estimate > 0 && SafeAdd(session.Spent, estimate) > session.Budget)
            await RaiseAsync(session, site, "budget", session.Budget, SafeAdd(session.Spent, estimate), routeCt).ConfigureAwait(false);

        // The gate (spec/rules-0.1-draft.md) decides before the source runs; the slot and the estimate are reserved.
        var at = Now;
        var call = new JsonObject
        {
            ["skill"] = skill,
            ["at"] = GrantSession.Time(at),
            ["provider"] = source.ProviderKind,
            ["estimatedCost"] = Money.ToSpec(estimate),
            ["input"] = new JsonObject { ["clipSeconds"] = 0, ["textChars"] = textChars },
            ["params"] = new JsonObject(),
        }.ToJsonString();
        CheckResult check;
        await session.Gate.WaitAsync(routeCt).ConfigureAwait(false);
        try
        {
            check = Gate.CheckCall(session.Manifest.Json, session.GrantJson(), call, session.StateJson(at));
            if (check.Decision is Decision.Allowed or Decision.Warn)
            {
                session.CallTimes.Add(at);
                session.Spent = SafeAdd(session.Spent, estimate);
            }
        }
        finally
        {
            session.Gate.Release();
        }

        var codes = string.Join(", ", check.Reasons.Select(r => r.Code).Distinct());
        if (check.Decision == Decision.Blocked)
        {
            if (check.Reasons.Any(r => r.Code is "rate-exceeded" or "text-too-long" or "grant-expired"))
            {
                if (check.Reasons.Any(r => r.Code == "rate-exceeded")) TellLimit(session, site, "rate", "the site's calls per minute");
                return new Reserved(FailedObject(entry, "limit", codes, route.All()), false, null, at, estimate, check.Decision, codes);
            }

            return new Reserved(FailedObject(entry, "failed", codes, route.All()), false, null, at, estimate, check.Decision, codes);
        }

        if (check.Decision == Decision.Ask)
        {
            // budget-exceeded: the wallet never spends past the budget without a new yes (the visitor was asked just above)
            route.Add(index, source.Name + ": " + codes);
            return new Reserved(null, true, "budget per session", at, estimate, check.Decision, codes);
        }

        // A paid source asks first, unless the visitor said "always" for the site; a source the public list says is free has nothing to ask about.
        if (step.AskFirst && estimate > 0 && !site.PaidAllowed)
        {
            var answer = await AskPaidAsync(session, site, source.Name, estimate, route.All(), callerCt).ConfigureAwait(false);
            if (answer == PaidAnswer.No)
            {
                await Unreserve(session, at, estimate).ConfigureAwait(false);
                route.Add(index, source.Name + ": you said no");
                return new Reserved(FailedObject(entry, "refused", "paid fallback not allowed", route.All()), false, null, at, estimate, check.Decision, codes);
            }

            if (answer == PaidAnswer.Always)
            {
                RememberPaid(session.Origin);
                site.PaidAllowed = true;
            }
        }

        return new Reserved(null, false, null, at, estimate, check.Decision, codes);
    }

    /// <summary><see cref="Failed"/> as the object it always is.</summary>
    JsonObject FailedObject(ActivityEntry entry, string outcome, string note, IReadOnlyList<string>? route = null, string? reason = null) =>
        (JsonObject)Failed(entry, outcome, note, route, reason);

    /// <summary>What the requests to one source gave: the pictures, what they cost (settled with the day's money and the session's), what the provider reported.</summary>
    sealed record Drawn(IReadOnlyList<byte[]> Pictures, decimal Cost, decimal? Reported, string? Failure);

    /// <summary>
    /// Up to <c>count</c> requests to one source, one after another, stopping at the first that fails (a few pictures are an answer, none is a failure
    /// of this source). The money settles on what the provider reported for each request, or the estimate for one that said nothing; the receipt's
    /// cost is known only when every request reported.
    /// </summary>
    async Task<Drawn> DrawAsync(IImageSource source, ImageRequest request, decimal each, CancellationToken routeCt, long routeDeadline, CancellationToken callerCt,
        TimeSpan sourceTimeout, ActivityEntry entry, GrantSession session, Reserved reserved, DailyHold hold)
    {
        var got = new List<byte[]>();
        decimal spent = 0;
        decimal reportedSum = 0;
        var allReported = true;
        var responded = false;
        string? failure = null;
        try
        {
            while (got.Count < request.Count)
            {
                if (routeCt.IsCancellationRequested) break;
                entry.ProviderCalled = true;
                entry.ProviderCalls++;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(routeCt);
                if (sourceTimeout > TimeSpan.Zero) limit.CancelAfter(Min(sourceTimeout, Remaining(routeDeadline)));
                try
                {
                    var answer = await source.GenerateImageAsync(request with { Count = request.Count - got.Count }, limit.Token).ConfigureAwait(false);
                    responded = true;
                    got.AddRange(answer.Pictures.Take(request.Count - got.Count));
                    spent += answer.Cost is > 0 ? Math.Min(MaxTextCost, answer.Cost.Value) : each;
                    if (answer.Cost is > 0 || (answer.Cost is not null && each == 0)) reportedSum += answer.Cost.Value; else allReported = false;
                }
                catch (TextSourceException e)
                {
                    responded |= e.ResponseReceived;
                    if (e.ResponseReceived) spent += e.Cost is > 0 ? Math.Min(MaxTextCost, e.Cost.Value) : each;
                    if (e.Cost is > 0 || (e.Cost is not null && each == 0)) reportedSum += e.Cost.Value; else if (e.ResponseReceived) allReported = false;
                    failure = e.Message;
                    break;
                }
                catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
                {
                    failure = "took too long";
                    break;
                }
                catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
                {
                    failure = Short(e);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the site cancelled: what was reserved goes back, and the cancel goes on
            await ReleaseTextReservationAsync(session, reserved.At, reserved.Estimate, hold, removeCallTime: false).ConfigureAwait(false);
            throw;
        }

        var attempt = new TextAttempt(null, responded ? spent : null, true, responded, failure);
        var cost = await SettleTextAttemptAsync(session, (ITextSource)source, reserved.At, reserved.Estimate, attempt, hold).ConfigureAwait(false);
        return new Drawn(got, cost, responded && allReported ? reportedSum : null, failure);
    }

    /// <summary>One picture as it is sent: its bytes, the type the wallet found in them, and whether the wallet made it smaller.</summary>
    sealed record Finished(byte[] Bytes, string Mime, bool Reduced);

    /// <summary>
    /// What the model drew, made fit (ADDENDUM-0370-1): the type is found in the bytes; a picture over the site's side or byte limit is made smaller by
    /// the shrinker, or dropped when it cannot be; the pictures of one answer together stay within 8,000,000 bytes. <paramref name="tooBig"/> is true when
    /// a picture was dropped for its size.
    /// </summary>
    List<Finished> FinishPictures(IReadOnlyList<byte[]> drawn, PictureAsk ask, out bool tooBig)
    {
        tooBig = false;
        var kept = new List<Finished>();
        var total = 0;
        foreach (var raw in drawn)
        {
            if (Pictures.Detect(raw) is not { } mime) continue;                 // a picture of another type is dropped
            ShrunkPicture? shrunk;
            try
            {
                shrunk = PictureShrinker is { } shrinker
                    ? shrinker.Shrink(raw, mime, ask.MaxSide, ask.MaxBytes)
                    : ask.MaxSide is null && raw.Length <= ask.MaxBytes ? new ShrunkPicture(raw, false) : null;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;                                                       // a picture the library cannot read is no picture
            }

            if (shrunk is null || shrunk.Bytes.Length > ask.MaxBytes || Pictures.Detect(shrunk.Bytes) is not { } sentType)
            {
                tooBig = true;
                continue;
            }

            if (total + shrunk.Bytes.Length > Pictures.MaxAnswerBytes)
            {
                tooBig = true;
                continue;
            }

            total += shrunk.Bytes.Length;
            kept.Add(new Finished(shrunk.Bytes, sentType, shrunk.Reduced));
        }

        return kept;
    }
}
