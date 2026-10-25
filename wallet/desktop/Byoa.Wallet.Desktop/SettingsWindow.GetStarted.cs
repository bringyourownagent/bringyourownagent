using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

// The Get started card, at the top of My models and, while the wallet has no text model at all, at the top of Sites:
// three ways to a first answer, all free — a model a program on this PC already serves, OpenRouter with a free
// account (the sign-in is OAuth PKCE, in the visitor's browser), and a public demo service that answers with no
// sign-up. This file holds only the card; the pages, the tab list and the rest of the window stay in
// SettingsWindow.cs (and its other partials).

sealed partial class SettingsWindow
{
    // The card's own state, kept across redraws (a page is drawn again whenever something in the wallet changes).
    List<RegistryRuntimeInfo>? _getStartedRuntimes;   // the last scan of this PC; null until one came back
    bool _getStartedScanning;
    int _getStartedScanNumber;                        // only the newest scan may paint its answer
    string? _getStartedNote;                          // a line about the last card action (a result, an error)
    string? _getStartedError;                         // the scan's failure, until the next scan succeeds
    OpenRouterAuth? _signIn;                          // the open one-shot callback, while the browser is out
    bool _signedIn;                                   // the OpenRouter key is saved and the free models are on offer
    IReadOnlyList<CatalogModel>? _freePicks;          // the suggested free models, fixed once when the sign-in landed
    readonly HashSet<string> _freeTicked = new(StringComparer.Ordinal);
    string? _demoConfirm;                             // the demo preset whose one plain confirmation is showing

    /// <summary>The card: the local models found here, the OpenRouter sign-in, and the free demo presets.</summary>
    Control GetStartedCard(bool sites)
    {
        var card = new StackPanel { Spacing = 10 };
        card.Children.Add(Ui.Row(8, Ui.Text("Get started", "h2"), Ui.Chip("free", "outline")));
        card.Children.Add(Ui.Wrap(sites
            ? "The wallet has no text model yet, so every site hears “AI unavailable”. Three ways to a first answer, all free:"
            : "Three ways to a first answer, all free. Anything added here behaves exactly like a model from Add a model.", "muted"));
        card.Children.Add(LocalSection());
        card.Children.Add(OpenRouterSection());
        card.Children.Add(DemoSection());
        if (_getStartedError is not null) card.Children.Add(Ui.Wrap(_getStartedError, "small"));
        if (_getStartedNote is not null) card.Children.Add(Ui.Wrap(_getStartedNote, "small"));
        return Ui.Box("card", card);
    }

    // ---- 1 · a model a program on this computer already serves -------------------------------

    Control LocalSection()
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(Ui.Text("1 · A model on this computer", "strong"));
        if (_getStartedRuntimes is null)
        {
            if (!_getStartedScanning) StartGetStartedScan();
            section.Children.Add(Ui.Wrap("Looking for model programs on this computer (LM Studio, Ollama, the Llama app)…", "muted", "small"));
            return section;
        }

        var settings = _w.Store.Snapshot();
        var found = _getStartedRuntimes.Select(r => new RuntimeInfo(r.Kind, r.Endpoint, r.Running, r.Models)).ToList();
        var offers = GetStarted.Local(found, settings.Sources);
        if (offers.Count == 0)
        {
            var running = _getStartedRuntimes.Any(r => r.Running);
            section.Children.Add(Ui.Wrap(running
                ? "A program answers on this computer but offers no model you do not have yet."
                : "No model program answered on this computer (LM Studio, Ollama, the Llama app). Load a model in one — or get Llama, free and open source.", "muted", "small"));
            if (!running) section.Children.Add(Ui.Link("Get Llama", "GetStartedLlama", OpenLlamaPage));
            return section;
        }

        foreach (var offer in offers)
        {
            var state = offer.LoadedKnown ? (offer.Loaded ? "in memory" : "on disk") : "running";
            var name = offer.ModelName is null ? offer.Model : offer.ModelName + " (" + offer.Model + ")";
            var add = Ui.Button("Add", "allow", "GetStartedAdd-" + SafeId(offer.Model), () => AddLocalModel(offer), "small");
            AutomationProperties.SetName(add, "Add " + name + " from " + offer.RuntimeName);
            var runtime = Ui.Text(offer.RuntimeName, "mono");
            runtime.VerticalAlignment = VerticalAlignment.Center;
            var title = Ui.Text(name, "strong");
            title.VerticalAlignment = VerticalAlignment.Center;
            var detail = Ui.Text(state + (offer.SizeBytes is { } size ? " · " + (size / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : ""), "muted", "small");
            detail.VerticalAlignment = VerticalAlignment.Center;
            section.Children.Add(Ui.Columns("*,Auto", Ui.Row(8, runtime, title, detail), add));
        }

        return section;
    }

    async void StartGetStartedScan()
    {
        _getStartedScanning = true;
        var number = ++_getStartedScanNumber;
        try
        {
            var runtimes = await _w.Registry.ListRuntimesAsync();
            if (number != _getStartedScanNumber || _getStartedRuntimes is not null) return;
            _getStartedRuntimes = runtimes.ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (number != _getStartedScanNumber) return;
            _getStartedError = "The search on this computer failed: " + e.Message;
            _getStartedRuntimes = [];
        }
        finally
        {
            _getStartedScanning = false;
        }

        Render();
    }

    void AddLocalModel(LocalModelOffer offer)
    {
        // The same add the Add a model window makes for a local model: one draft; the registry checks it and routes it.
        var result = _w.Registry.AddSource(new SourceDraft(offer.Kind, offer.Model, offer.Endpoint, [Vocabulary.Generate]));
        _getStartedNote = result.Ok
            ? "Added " + (offer.ModelName ?? offer.Model) + " — it is on My models now."
            : "Could not add " + offer.Model + ": " + result.Message;
        Render();
    }

    // ---- 2 · OpenRouter with a free account --------------------------------------------------

    Control OpenRouterSection()
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(Ui.Text("2 · Sign in with OpenRouter (a free account)", "strong"));
        var hasKey = _w.Registry.ListSources().FirstOrDefault(s => s.Id == OpenRouterSource.SourceId)?.HasApiKey == true;

        if (_signIn is not null)
        {
            section.Children.Add(Ui.Wrap("Waiting for OpenRouter in your browser… finish the sign-in there, or cancel here. The wallet listens on this computer only.", "muted", "small"));
            section.Children.Add(Ui.Button("Cancel the sign-in", "deny", "GetStartedSignInCancel", CancelSignIn, "small"));
            return section;
        }

        if (_signedIn && _freePicks is { Count: > 0 })
        {
            section.Children.Add(Ui.Wrap("Your OpenRouter key is saved (it is never shown again). These free models answer with it at no cost, rate-limited; untick what you do not want:", "muted", "small"));
            foreach (var pick in _freePicks)
            {
                var tick = new CheckBox { IsChecked = _freeTicked.Contains(pick.Id) };
                AutomationProperties.SetAutomationId(tick, "GetStartedPick-" + SafeId(pick.Id));
                var id = pick.Id;
                tick.IsCheckedChanged += (_, _) =>
                {
                    if (tick.IsChecked == true) _freeTicked.Add(id);
                    else _freeTicked.Remove(id);
                };
                var name = Ui.Text(pick.Name, "strong");
                name.VerticalAlignment = VerticalAlignment.Center;
                var detail = Ui.Text(pick.Id + " · " + pick.ContextLength.ToString("N0", CultureInfo.InvariantCulture) + " tokens context", "muted", "small");
                detail.VerticalAlignment = VerticalAlignment.Center;
                section.Children.Add(Ui.Row(8, tick, name, detail));
            }

            section.Children.Add(Ui.Button("Add the ticked models", "allow", "GetStartedAddPicks", AddPickedModels, "small"));
            return section;
        }

        section.Children.Add(Ui.Wrap(hasKey
            ? "Your OpenRouter key is saved. More of OpenRouter's free models can be added any time with Add a model."
            : "Hundreds of models behind one key, and some of them cost nothing. The sign-in happens in your browser; the key comes back to the wallet and is never shown again.", "muted", "small"));
        if (!hasKey) section.Children.Add(Ui.Button("Sign in with OpenRouter", "allow", "GetStartedSignIn", StartSignIn, "small"));
        return section;
    }

    void StartSignIn()
    {
        try
        {
            _signIn = OpenRouterAuth.Start(_w.Http, OpenBrowser);
            _getStartedNote = null;
            Render();
            _ = FinishSignInAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _signIn = null;
            _getStartedNote = "The sign-in could not start: " + e.Message;
            Render();
        }
    }

    async Task FinishSignInAsync()
    {
        var auth = _signIn!;
        try
        {
            var key = await auth.WaitAsync();
            // Stored the way the Add API key window stores a key: through the registry, into the secret store — never logged, never shown.
            var stored = await _w.Registry.SetApiKeyAsync(OpenRouterSource.SourceId, key);
            if (!stored.Ok) throw new InvalidOperationException(stored.Message);
            _signedIn = true;
            _freePicks = OpenRouterFreeModels.Pick(_w.Sources.Catalog, OpenRouterModelsAlready());
            _freeTicked.Clear();
            foreach (var pick in _freePicks) _freeTicked.Add(pick.Id);
            _getStartedNote = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _getStartedNote = e is TimeoutException
                ? "The OpenRouter sign-in timed out: nothing was received and nothing was sent. You can try again."
                : "The OpenRouter sign-in did not go through: " + e.Message;
        }
        finally
        {
            auth.Dispose();
            if (_signIn == auth) _signIn = null;
        }

        Render();
    }

    void CancelSignIn()
    {
        _signIn?.Dispose();
        _signIn = null;
        _getStartedNote = "The sign-in was cancelled; nothing was stored.";
        Render();
    }

    void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _getStartedNote = "Could not open the browser. Open this address yourself: " + url;
        }
    }

    IReadOnlyList<string> OpenRouterModelsAlready() =>
        _w.Store.Snapshot().Sources.Where(s => string.Equals(s.Kind, SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase) && s.Model is not null)
            .Select(s => s.Model!).ToList();

    void AddPickedModels()
    {
        var added = 0;
        string? failure = null;
        foreach (var pick in _freePicks ?? [])
        {
            if (!_freeTicked.Contains(pick.Id)) continue;
            var result = _w.Registry.AddSource(new SourceDraft(SourceKinds.OpenRouter, pick.Id, "https://openrouter.ai/api/v1", [Vocabulary.Generate]));
            if (result.Ok) added++;
            else failure ??= pick.Id + ": " + result.Message;
        }

        _freePicks = null;
        _freeTicked.Clear();
        _getStartedNote = added == 0
            ? "No free model was added" + (failure is null ? "." : ": " + failure)
            : "Added " + added.ToString(CultureInfo.InvariantCulture) + (added == 1 ? " free model" : " free models") + (failure is null ? "." : "; " + failure);
        Render();
    }

    // ---- 3 · free demo models, no sign-up ----------------------------------------------------

    Control DemoSection()
    {
        var section = new StackPanel { Spacing = 6 };
        var settings = _w.Store.Snapshot();
        section.Children.Add(Ui.Text("3 · Free demo models, no sign-up", "strong"));
        section.Children.Add(Ui.Wrap("A public service answers with no key at all. Not private: it may keep what you send; it is slow and limited.", "muted", "small"));
        foreach (var preset in GetStarted.DemoPresets)
        {
            var model = GetStarted.DemoModel(preset.Id);
            if (settings.Sources.Any(s => string.Equals(s.Service, preset.Id, StringComparison.OrdinalIgnoreCase) && s.Model == model))
            {
                section.Children.Add(Ui.Row(8, Ui.Text(preset.Name, "strong"), Ui.Text(model, "mono"), Ui.Chip("added", "outline")));
                continue;
            }

            if (_demoConfirm == preset.Id)
            {
                var confirm = new StackPanel { Spacing = 6 };
                confirm.Children.Add(Ui.Wrap(GetStarted.DemoWarning, "small"));
                confirm.Children.Add(Ui.Row(8,
                    Ui.Button("Add it", "deny", "GetStartedDemoConfirm-" + preset.Id, () => AddDemo(preset), "small"),
                    Ui.Button("No", "deny", "GetStartedDemoCancel-" + preset.Id, () => { _demoConfirm = null; Render(); }, "small")));
                section.Children.Add(Ui.Box("field", confirm));
                continue;
            }

            var add = Ui.Button("Add", "deny", "GetStartedDemoAdd-" + preset.Id, () => { _demoConfirm = preset.Id; Render(); }, "small");
            AutomationProperties.SetName(add, "Add the free demo model of " + preset.Name);
            var name = Ui.Text(preset.Name, "strong");
            name.VerticalAlignment = VerticalAlignment.Center;
            var detail = Ui.Text(model + " · a few requests a minute", "muted", "small");
            detail.VerticalAlignment = VerticalAlignment.Center;
            section.Children.Add(Ui.Columns("*,Auto", Ui.Row(8, name, detail), add));
        }

        return section;
    }

    void AddDemo(ApiService preset)
    {
        _demoConfirm = null;
        var result = _w.Registry.AddSource(new SourceDraft(ApiServices.Kind, GetStarted.DemoModel(preset.Id), null, [Vocabulary.Generate], null, null, preset.Id));
        if (result.Ok)
        {
            // Free but not private: it waits out of the sites' default set until the visitor ticks it for a site.
            _w.Store.Update(s => { var one = s.Source(result.Id!); if (one is not null) one.Offer = false; });
            _getStartedNote = "The demo model is on My models. It answers for free; switch it on for a site in the site's window.";
        }
        else
        {
            _getStartedNote = "Could not add the demo model: " + result.Message;
        }

        Render();
    }
}
