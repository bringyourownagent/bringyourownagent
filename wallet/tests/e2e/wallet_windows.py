"""Pictures of the wallet's own windows, taken on a hidden desktop (nothing appears on the screen somebody works at): opens the settings of a
wallet with the data folder you give it (BYOA_WALLET_HOME), goes through its pages and the "Add a model" window and saves a PNG of each.
Prints what it saw. Buttons are pressed through UI Automation only.

    <python with uiautomation, pillow> wallet_windows.py --exe <Byoa.Wallet.exe> --home <wallet data folder> --shots <dir>
"""
import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hidden_desktop as hd  # noqa: E402  (before anything that starts COM)

ap = argparse.ArgumentParser()
ap.add_argument("--exe", required=True)
ap.add_argument("--home", required=True)
ap.add_argument("--shots", required=True)
ap.add_argument("--desktop", default="byoa-wallet-windows")
a = ap.parse_args()
os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)
env = dict(os.environ, BYOA_WALLET_HOME=a.home)

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe], env=env)
ui.ONLY_PIDS = {wallet.pid}
print("wallet started", wallet.pid, flush=True)
settings = ui.find_window("Agent wallet", 40)
if settings is None:
    print("FAILED no settings window")
    desk.terminate(wallet)
    sys.exit(1)
time.sleep(1.5)


def names(window, kind=None, limit=60):
    out = []

    def walk(c, depth):
        try:
            for ch in c.GetChildren():
                if ch.Name and (kind is None or ch.ControlTypeName == kind):
                    out.append((ch.ControlTypeName, ch.AutomationId, ch.Name[:100]))
                if depth < 40:
                    walk(ch, depth + 1)
        except Exception:  # noqa: BLE001
            pass

    walk(window, 0)
    return out[:limit]


for page in ("sites", "sources", "limits", "activity"):
    ui.select(settings, "Tab-" + page)
    time.sleep(1.2)
    desk.shot(settings, os.path.join(a.shots, f"settings-{page}.png"))
    print("page", page, "shot", flush=True)
    if page == "limits":
        for kind, ident, name in names(settings, "EditControl"):
            print("   edit", ident, "|", name, flush=True)
        for kind, ident, name in names(settings, "ButtonControl"):
            if ident.startswith(("SiteLimits", "Limit", "UseSite", "Reset")):
                print("   button", ident, "|", name, flush=True)

ui.select(settings, "Tab-sources")
time.sleep(1)
ui.invoke(settings, "AddModel")
add = ui.find_window("Add a model", 15)
print("Add a model window:", bool(add), flush=True)
if add:
    time.sleep(1.5)
    desk.shot(add, os.path.join(a.shots, "add-model-local.png"))
    ui.invoke(add, "ModelApiKey")
    time.sleep(2)
    desk.shot(add, os.path.join(a.shots, "add-model-key.png"))
    print("   saved key shown:", bool(add.Control(AutomationId="ModelKeySaved", searchDepth=40).Exists(1, 0.2)), flush=True)
    ui.invoke(add, "CatalogCheap")
    time.sleep(1.5)
    desk.shot(add, os.path.join(a.shots, "add-model-key-cheap.png"))
    boxes = [c for c in names(add, "CheckBoxControl", 200) if c[1].startswith("PickModel-")]
    print("   models listed under Cheap:", len(boxes), flush=True)
    for b in boxes[:5]:
        print("     ", b[1], "|", b[2], flush=True)
desk.terminate(wallet)
print("done")
