"""The "Next time, decide on my own" tick of a site, which lives in the settings (Sites, the site's row) since the consent window lost it.
Runs the wallet on a hidden desktop (UI Automation only, nothing appears on the screen somebody works at) with two seeded sites:
one allowed site that does not decide on its own, and one site that does (as direct_e2e.py seeds its site). The tick on the first one reaches
the settings file, the second one is read and written back as it was.

    <python with uiautomation, pillow> site_auto_ui.py --exe <Byoa.Wallet.exe> --home <new folder in the scratchpad>
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
ap.add_argument("--desktop", default="byoa-site-auto")
a = ap.parse_args()
assert "scratchpad" in os.path.abspath(a.home).lower() or "tmp" in os.path.abspath(a.home).lower(), "use a data folder of its own"
os.makedirs(a.home, exist_ok=True)
SETTINGS = os.path.join(a.home, "settings.json")
with open(SETTINGS, "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sites": [
        {"origin": "https://one.example", "status": "allowed", "skills": ["audio.transcribe"], "autoDecide": False},
        {"origin": "http://localhost:5555", "autoDecide": True},
    ]}, f)

desk = hd.HiddenDesktop(a.desktop)
import walletui as ui  # noqa: E402

failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def sites():
    with open(SETTINGS, encoding="utf-8") as f:
        return {s["origin"]: s for s in json.load(f).get("sites", [])}


def wait_for(pred, seconds=8.0):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if pred():
                return True
        except (OSError, ValueError, KeyError):  # the file is being written
            pass
        time.sleep(0.3)
    return False


wallet = desk.start([a.exe], env=dict(os.environ, BYOA_WALLET_HOME=a.home))
try:
    w = ui.find_window("Agent wallet", 40)
    time.sleep(2.0)
    check("the settings window is there", w is not None)
    check("the first site does not decide on its own", sites()["https://one.example"]["autoDecide"] is False)

    ui.tick_site_auto(w, "one.example", True)
    time.sleep(1.0)                                             # the row is built again after the change: look for the box anew
    box = ui.control(ui.find_window("Agent wallet", 10), "SiteAuto-one.example", 5)
    check("the tick is on", box.GetPattern(ui.auto.PatternId.TogglePattern).ToggleState == 1)
    check("... and the settings file says the site decides on its own", wait_for(lambda: sites()["https://one.example"]["autoDecide"] is True))
    check("... while the seeded site that did is read and written back as it was",
          sites().get("http://localhost:5555", {}).get("autoDecide") is True, str(sites().get("http://localhost:5555")))

    ui.tick_site_auto(ui.find_window("Agent wallet", 10), "one.example", False)
    check("the tick off takes it back", wait_for(lambda: sites()["https://one.example"]["autoDecide"] is False))
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
