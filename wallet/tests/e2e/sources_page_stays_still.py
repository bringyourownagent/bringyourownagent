"""The Sources page of the settings window must not draw itself again while nothing changes (each redraw flashes the scroll back to the top).

Once a text box on that page raised TextChanged for the text it was made with, the page saved the settings, drew itself again, made a new
box, and so on twice a second. A wallet on a hidden desktop with its own data folder (one local model, so that the page has the boxes);
every 200 ms the runtime id of a control of the page is read: a new id means the page was built again. UI Automation only; nothing appears
on the screen somebody works at.

    <python with uiautomation, pillow> sources_page_stays_still.py --exe <Byoa.Wallet.exe> --home <empty wallet data folder> [--seconds 25]
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
ap.add_argument("--seconds", type=int, default=25)
ap.add_argument("--desktop", default="byoa-sources-still")
a = ap.parse_args()
os.makedirs(a.home, exist_ok=True)

endpoint = "http://127.0.0.1:5889/v1"                       # nothing listens there: the model is shown as not running, the page has its boxes all the same
source = {"id": f"openai-compatible:alpha@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": "alpha", "endpoint": endpoint,
          "model": "alpha", "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False,
          "addedByAgent": False, "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback"}
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": [source],
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": source["id"], "enabled": True, "askBeforePaid": True}]}]}, f)

desk = hd.HiddenDesktop(a.desktop)
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe], env=dict(os.environ, BYOA_WALLET_HOME=a.home))
ui.ONLY_PIDS = {wallet.pid}
failed = []
try:
    w = ui.find_window("Agent wallet", 40)
    ui.select(w, "Tab-sources")
    time.sleep(3.0)                                          # the page settles (the first scan of the runtimes comes in)
    box_id = "AddedSourceThinkingBudget-" + "".join(c if c.isalnum() else "-" for c in source["id"])
    ui.control(ui.find_window("Agent wallet", 5), box_id, 10)   # the page has the box that once made it flash
    last, redraws, started = None, [], time.time()
    while time.time() - started < a.seconds:
        try:
            rid = tuple(ui.control(ui.find_window("Agent wallet", 5), "NeverStartModels", 2).GetRuntimeId())
        except Exception as e:  # noqa: BLE001
            rid = ("err", type(e).__name__)
        if last is not None and rid != last:
            redraws.append(round(time.time() - started, 1))
        last = rid
        time.sleep(0.2)
    ok = len(redraws) == 0
    print(("ok   " if ok else "FAIL ") + f"the Sources page was not drawn again in {a.seconds} s" + ("" if ok else f"  [{len(redraws)} times: {redraws[:10]}]"))
    if not ok:
        failed.append("redraws")
    with open(os.path.join(a.home, "settings.json"), encoding="utf-8") as f:
        saved = json.load(f)
    stamp = os.path.getmtime(os.path.join(a.home, "settings.json"))
    quiet = time.time() - stamp > a.seconds - 5
    print(("ok   " if quiet else "FAIL ") + "the settings file was not written again meanwhile")
    if not quiet:
        failed.append("rewrites")
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
