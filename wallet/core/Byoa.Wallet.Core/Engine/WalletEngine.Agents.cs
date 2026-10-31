using System.Globalization;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// The visitor's own agent as a source of answers for a site (docs/agent-source.md). An agent is not a model: it can search the web and read
/// pages before it answers, so a site that reaches it can try to talk it into more than an answer. Three rules keep that in check:
/// a site gets an agent only when the visitor gave it by name (never as part of "his route"), and only with a goal the site stated, which the
/// guard looks at and, when it looks risky, the visitor decides; every request to the agent passes the guard first; and what the guard holds
/// waits for the visitor, with what the guard saw in front of him.
/// </summary>
public sealed partial class WalletEngine
{
    /// <summary>The guard. Without one no agent runs: nothing can vouch for a request.</summary>
    public IGuard? Guard { get; set; }

    /// <summary>How long the visitor has to answer when the guard holds something. Silence is a refusal.</summary>
    public static TimeSpan GuardQuestionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>A goal shorter than this tells nobody what the site will ask; the site does not get an agent for it.</summary>
    public const int MinPurposeChars = 20;

    /// <summary>What the agent can do, as the guard is told: "harm" is judged against it.</summary>
    public const string AgentAbilities =
        "It can search the web and read web pages, and nothing else: it has no files, it cannot run commands, and it cannot reach the owner's " +
        "accounts, passwords, money or this wallet.";

    readonly SemaphoreSlim _guardPrompt = new(1, 1);

    /// <summary>The text sources a site that was not given a model by name may use: every source that is a model. An agent is never one of them.</summary>
    IReadOnlyList<ITextSource> PlainTextSources => _textSources.Any(s => s is IAgentTextSource)
        ? _textSources.Where(s => s is not IAgentTextSource).ToList()
        : _textSources;

    static bool IsAgentId(WalletSettings settings, string id) =>
        settings.Sources.Any(s => s.Id == id && string.Equals(s.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase));

    /// <summary>The site has an agent among its models, and the goal it states now is not the one that was accepted.</summary>
    static bool AgentGoalChanged(SiteRecord site, RequestedManifest manifest, WalletSettings settings) =>
        site.Status == SiteStatus.Allowed
        && site.Models.Any(m => m.SourceIds.Any(id => IsAgentId(settings, id)))
        && !string.Equals(site.AgentPurposeOk, manifest.Summary, StringComparison.Ordinal);

    // ------------------------------------------------------------------------------------------------
    // the goal, when the visitor gives an agent to a site

    /// <summary>
    /// The site is about to be given his agent. Its goal must say something; the guard looks at it; a goal that looks risky is put to the visitor.
    /// True when the agent may go to the site, with the goal that was accepted (null when it was not checked, so it is asked again next time).
    /// <c>Refused</c> is the visitor's own no, to a goal that looked risky: that is a decision about the site, which a technical failure is not.
    /// </summary>
    async Task<(bool Given, string? Accepted, bool Refused)> AcceptAgentGoalAsync(string origin, string host, SiteRecord site, string purpose, string agent, CancellationToken ct)
    {
        purpose = purpose.Trim();
        if (purpose.Length < MinPurposeChars)
        {
            _ui.ShowInfo(new InfoModel("Your agent was not given",
                host + " did not say what it would ask your agent, so it did not get it. A site states its goal in its request."));
            return (false, null, false);
        }

        if (string.Equals(site.AgentPurposeOk, purpose, StringComparison.Ordinal)) return (true, purpose, false);
        if (site.TrustAgentRequests) return (true, purpose, false);              // the visitor trusts this site fully
        var verdict = Guard is { } guard
            ? await guard.CheckPurposeAsync(origin, purpose, AgentAbilities, ct).ConfigureAwait(false)
            : GuardVerdict.Unavailable("the guard is not set up");
        switch (verdict.Outcome)
        {
            case GuardOutcome.Allow:
                return (true, purpose, false);
            case GuardOutcome.Hold:
                var answer = await AskGuardAsync(new GuardPrompt(origin, host, ForPurpose: true, purpose, null, verdict.Tripped, agent), ct).ConfigureAwait(false);
                if (answer == GuardAnswer.AllowAlways) SetTrustAgentRequests(origin, true);
                return answer is GuardAnswer.Allow or GuardAnswer.AllowAlways ? (true, purpose, false) : (false, null, true);
            default:
                if (StopWhenGuardUnavailable)
                {
                    _ui.ShowInfo(new InfoModel("Your agent was not given",
                        "The guard could not be asked (" + verdict.Why + "), so " + host + " did not get your agent. You can change this in Guard."));
                    return (false, null, false);
                }

                return (true, null, false);
        }
    }

    bool StopWhenGuardUnavailable => _store.Read(s => s.Guard.StopWhenUnavailable);

    /// <summary>One question at a time to the visitor: a held request, or a goal. No answer in time is a refusal.</summary>
    async Task<GuardAnswer> AskGuardAsync(GuardPrompt prompt, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(GuardQuestionTimeout);
        try
        {
            await _guardPrompt.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return GuardAnswer.Refuse;
        }

        try
        {
            return await _ui.AskGuardAsync(prompt, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return GuardAnswer.Refuse;
        }
        finally
        {
            _guardPrompt.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // every request

    /// <summary>What the guard (and the visitor) made of one request to an agent.</summary>
    /// <param name="Run">The agent may answer.</param>
    /// <param name="EndCall">Nothing else may answer either: the request itself was refused.</param>
    /// <param name="Note">For the route and the activity log.</param>
    sealed record AgentClearance(bool Run, bool EndCall, string Note);

    /// <summary>The guard looks at a request before the agent sees it. Called before anything is reserved for the call.</summary>
    async Task<AgentClearance> ClearAgentAsync(GrantSession session, IAgentTextSource agent, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var host = Origins.TryParse(session.Origin, out var parsed, out _) ? parsed : session.Origin;
        var purpose = session.Manifest.Summary.Trim();
        var site = _store.Read(s => s.Site(session.Origin) is { } r ? Copy(r) : null);
        if (site is null || purpose.Length < MinPurposeChars)
            return new AgentClearance(false, true, "the site stated no goal for your agent");
        if (site.TrustAgentRequests) return new AgentClearance(true, false, "trusted site: the guard does not look at its requests");
        if (site.AgentPurposeOk is not null && !string.Equals(site.AgentPurposeOk, purpose, StringComparison.Ordinal))
            return new AgentClearance(false, true, "the goal the site states changed since you gave it your agent");

        var text = string.Join("\n\n", messages.Select(m => m.Role + ": " + m.Content));
        var verdict = Guard is { } guard
            ? await guard.CheckRequestAsync(new GuardRequest(session.Origin, purpose, text, AgentAbilities), ct).ConfigureAwait(false)
            : GuardVerdict.Unavailable("the guard is not set up");
        switch (verdict.Outcome)
        {
            case GuardOutcome.Allow:
                return new AgentClearance(true, false, "guard: ok (" + string.Join(", ", verdict.Findings.Select(f => f.Question + " " + f.Percent)) + ")");
            case GuardOutcome.Hold:
                var shown = text.Length > 4000 ? text[..4000] + "…" : text;
                var answer = await AskGuardAsync(new GuardPrompt(session.Origin, host, ForPurpose: false, purpose, shown, verdict.Tripped, agent.Name), ct).ConfigureAwait(false);
                var why = string.Join(", ", verdict.Tripped.Select(f => f.Question + " " + f.Percent));
                switch (answer)
                {
                    case GuardAnswer.Allow:
                        return new AgentClearance(true, false, "guard held it (" + why + "); you let it through");
                    case GuardAnswer.AllowAlways:
                        SetTrustAgentRequests(session.Origin, true);
                        return new AgentClearance(true, false, "guard held it (" + why + "); you let it through and trust this site from now on");
                    case GuardAnswer.TakeAway:
                        TakeAgentAway(session.Origin, agent.Id);
                        return new AgentClearance(false, true, "guard held it (" + why + "); you refused it and took your agent away from the site");
                    default:
                        return new AgentClearance(false, true, "guard held it (" + why + "); you refused it");
                }
            default:
                return StopWhenGuardUnavailable
                    ? new AgentClearance(false, false, "the guard could not be asked (" + verdict.Why + "): the agent did not run")
                    : new AgentClearance(true, false, "the guard could not be asked (" + verdict.Why + "): you chose to let requests through");
        }
    }

    /// <summary>"Trust this site fully": the guard stops looking at its goal and its requests to the visitor's agent (or looks again, when turned off).</summary>
    public void SetTrustAgentRequests(string origin, bool trust)
    {
        _store.Update(s => Site(s, origin).TrustAgentRequests = trust);
        SitesChanged?.Invoke();
    }

    /// <summary>The agent leaves the models of a site, and the site's live grants follow at once.</summary>
    void TakeAgentAway(string origin, string sourceId)
    {
        _store.Update(s =>
        {
            foreach (var models in Site(s, origin).Models) models.SourceIds.RemoveAll(id => id == sourceId);
            Site(s, origin).AgentPurposeOk = null;
        });
        RefreshLiveGrants(origin);
        SitesChanged?.Invoke();
    }

    /// <summary>The note for the activity log: the guard's line joined to whatever else the call has to say.</summary>
    static string? JoinNotes(params string?[] notes)
    {
        var kept = notes.Where(n => !string.IsNullOrEmpty(n)).ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    static string PercentOf(double probability) =>
        Math.Round(probability * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
}
