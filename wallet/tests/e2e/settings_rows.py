"""The Sites and Sources pages of the settings window: one line per site or model, closed at first, an arrow that opens all its settings,
20 to a page, and the settings that are not about one row below the rows. A wallet on a hidden desktop with 23 sites and 22 models
(UI Automation only; nothing appears on the screen somebody works at).

    <python with uiautomation, pillow> settings_rows.py --exe <Byoa.Wallet.exe> --home <empty folder in the scratchpad>
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hidden_desktop as hd  # noqa: E402  (before anything that starts COM)

ap = argparse.ArgumentParser()
ap.add_argument("--exe", required=True)
ap.add_argument("--home", required=True)
ap.add_argument("--shots", default=None)
ap.add_argument("--desktop", default="byoa-settings-rows")
a = ap.parse_args()
assert "scratchpad" in os.path.abspath(a.home).lower() or "tmp" in os.path.abspath(a.home).lower(), "use a data folder of its own"
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)

SITES, MODELS = 23, 22
endpoint = "http://127.0.0.1:5894/v1"


def source(i):
    model = f"model-{i:02d}"
    return {"id": f"openai-compatible:{model}@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": model, "endpoint": endpoint, "model": model,
            "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False, "addedByAgent": False,
            "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback"}


def site(i):
    return {"origin": f"https://site{i:02d}.example.org", "status": "Allowed", "autoDecide": False, "skills": ["text.generate"], "budget": "0.10 USD",
            "expectedCost": "0.10 USD", "summary": f"Test site number {i}", "privacy": "personal-voice", "languages": [], "paidAllowed": False,
            "quietRequests": 0, "lastRequest": f"2026-10-25T10:{59 - i:02d}:00+00:00", "decidedAt": f"2026-10-25T10:{59 - i:02d}:00+00:00", "paused": False}


sources = [source(i) for i in range(MODELS)]
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sites": [site(i) for i in range(SITES)], "sources": sources,
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": s["id"], "enabled": True, "askBeforePaid": True} for s in sources]}]}, f)

desk = hd.HiddenDesktop(a.desktop)
import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def walk(c, depth=0, out=None):
    out = [] if out is None else out
    try:
        for ch in c.GetChildren():
            out.append(ch)
            if depth < 40:
                walk(ch, depth + 1, out)
    except Exception:  # noqa: BLE001
        pass
    return out


def ids(window):
    """The automation ids of the page, in the order they are drawn (top to bottom)."""
    return [c.AutomationId for c in walk(window) if c.AutomationId]


def count(order, prefix):
    return sum(1 for i in order if i.startswith(prefix))


def first(order, prefix):
    return next((n for n, i in enumerate(order) if i.startswith(prefix)), -1)


def last(order, prefix):
    return max((n for n, i in enumerate(order) if i.startswith(prefix)), default=-1)


def settle(tab):
    for _ in range(6):
        try:
            w = ui.find_window("Agent wallet", 10)
            ui.select(w, "Tab-" + tab)
            time.sleep(2.0)
            return ui.find_window("Agent wallet", 10)
        except Exception as e:  # noqa: BLE001
            if type(e).__name__ != "COMError":
                raise
            time.sleep(1.0)
    raise RuntimeError("the settings window did not settle")


wallet = desk.start([a.exe], env=dict(os.environ, BYOA_WALLET_HOME=a.home))
ui.ONLY_PIDS = {wallet.pid}
try:
    ui.find_window("Agent wallet", 40)
    time.sleep(2.0)

    # ------------------------------------------------------------------------------------------ Sites
    w = settle("sites")
    order = ids(w)
    check("Sites: 20 sites on the first page, each with an arrow", count(order, "SiteExpand-") == 20, str(count(order, "SiteExpand-")))
    check("Sites: all of them closed, none of their settings built", count(order, "SiteRevoke-") + count(order, "SitePause-") + count(order, "SiteAuto-") == 0)
    check("Sites: the page has «Next» and its «Previous» is dark", "RowsNextSites" in order and not ui.control(w, "RowsPreviousSites", 3).IsEnabled)
    check("Sites: «Pause a site after it goes unused» is the last thing, below the rows and the pager",
          first(order, "PauseUnusedAfter") > last(order, "SiteExpand-") and first(order, "PauseUnusedAfter") > first(order, "RowsNextSites") > 0)
    if a.shots:
        desk.shot(w, os.path.join(a.shots, "sites-closed.png"))

    host0 = next(i for i in order if i.startswith("SiteExpand-"))[len("SiteExpand-"):]
    ui.open_row(w, "SiteExpand-" + host0)
    time.sleep(0.8)
    order = ids(ui.find_window("Agent wallet", 10))
    check("Sites: the arrow opens the settings of that site only", count(order, "SiteRevoke-") == 1 and ("SiteRevoke-" + host0) in order, str(count(order, "SiteRevoke-")))
    if a.shots:
        desk.shot(ui.find_window("Agent wallet", 10), os.path.join(a.shots, "sites-one-open.png"))
    ui.open_row(ui.find_window("Agent wallet", 10), "SiteExpand-" + host0)          # already open: nothing to do
    c = ui.control(ui.find_window("Agent wallet", 10), "SiteExpand-" + host0, 5)
    c.GetPattern(auto.PatternId.InvokePattern).Invoke()                                # and again: closes it
    time.sleep(0.8)
    order = ids(ui.find_window("Agent wallet", 10))
    check("Sites: the arrow closes it again", count(order, "SiteRevoke-") == 0)

    ui.invoke(ui.find_window("Agent wallet", 10), "RowsNextSites")
    time.sleep(1.5)
    order = ids(ui.find_window("Agent wallet", 10))
    check("Sites: page two holds the other 3", count(order, "SiteExpand-") == SITES - 20, str(count(order, "SiteExpand-")))
    ui.invoke(ui.find_window("Agent wallet", 10), "RowsPreviousSites")
    time.sleep(1.5)
    check("Sites: «Previous» is back on page one", count(ids(ui.find_window("Agent wallet", 10)), "SiteExpand-") == 20)

    # ------------------------------------------------------------------------------------------ Sources
    w = settle("sources")
    order = ids(w)
    check("Sources: 20 models on the first page, each with an arrow", count(order, "AddedSourceExpand-") == 20, str(count(order, "AddedSourceExpand-")))
    check("Sources: «Add a model» comes first, above the models", 0 <= first(order, "AddModel") < first(order, "AddedSourceExpand-"))
    check("Sources: all of them closed, none of their settings built", count(order, "AddedSourceEdit-") + count(order, "AddedSourceThinking-") == 0)
    check("Sources: the switches that are about all models come below the models and the pager",
          first(order, "WarmUpOnConnect") > last(order, "AddedSourceExpand-") and first(order, "NeverStartModels") > first(order, "WarmUpOnConnect")
          and first(order, "WarmUpOnConnect") > first(order, "RowsNextSources") > 0)
    check("Sources: each model keeps its «Use it» switch on its line", count(order, "AddedSourceEnabled-") == 20)
    if a.shots:
        desk.shot(w, os.path.join(a.shots, "sources-closed.png"))

    id0 = next(i for i in order if i.startswith("AddedSourceExpand-"))[len("AddedSourceExpand-"):]
    ui.open_row(w, "AddedSourceExpand-" + id0)
    time.sleep(0.8)
    order = ids(ui.find_window("Agent wallet", 10))
    check("Sources: the arrow opens the settings of that model only", count(order, "AddedSourceEdit-") == 1 and ("AddedSourceEdit-" + id0) in order)

    # a change of something else draws the page again: what is open stays open
    ui.toggle(ui.find_window("Agent wallet", 10), "WarmUpOnConnect", on=False)
    time.sleep(2.5)
    w = ui.find_window("Agent wallet", 10)
    order = ids(w)
    check("Sources: a model that was open stays open when the page is drawn again", ("AddedSourceEdit-" + id0) in order)
    ui.toggle(w, "WarmUpOnConnect", on=True)
    time.sleep(1.0)

    ui.invoke(ui.find_window("Agent wallet", 10), "RowsNextSources")
    time.sleep(1.5)
    order = ids(ui.find_window("Agent wallet", 10))
    check("Sources: page two holds the other 2 models and the wallet's own speech model",
          count(order, "AddedSourceExpand-") == MODELS - 20 and ("SourceExpand-openrouter-scribe" in order), str(count(order, "AddedSourceExpand-")))
    if a.shots:
        desk.shot(ui.find_window("Agent wallet", 10), os.path.join(a.shots, "sources-page-two.png"))
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
