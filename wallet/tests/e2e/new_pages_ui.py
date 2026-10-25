"""The pages added for the first-time visitor, on a wallet with nothing in it (a hidden desktop, UI Automation only, nothing appears on the screen
somebody works at): the short tab list and "Show all settings", the Get started card with its free demo models, the Everywhere tab, and the
"Connect a site by link" window with its plain error for a text that is not a link.

    <python with uiautomation, pillow> new_pages_ui.py --exe <Byoa.Wallet.exe> --home <empty folder in the scratchpad>
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
ap.add_argument("--desktop", default="byoa-new-pages")
a = ap.parse_args()
assert "scratchpad" in os.path.abspath(a.home).lower() or "tmp" in os.path.abspath(a.home).lower(), "use a data folder of its own"
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1}, f)

desk = hd.HiddenDesktop(a.desktop)
import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def has_control(window, automation_id, timeout=1.5):
    try:
        return ui.control(window, automation_id, timeout) is not None
    except LookupError:
        return False


def has_text(window, part, timeout=1.5):
    return window.TextControl(SubName=part, searchDepth=40).Exists(timeout, 0.2) or window.ButtonControl(SubName=part, searchDepth=40).Exists(timeout, 0.2)


def settle(tab):
    for _ in range(6):
        try:
            w = ui.find_window("Agent wallet", 10)
            ui.select(w, "Tab-" + tab)
            time.sleep(1.8)
            return ui.find_window("Agent wallet", 10)
        except Exception as e:  # noqa: BLE001
            if type(e).__name__ != "COMError":
                raise
            time.sleep(1.0)
    raise RuntimeError("the page " + tab + " did not open")


wallet = desk.start([a.exe], env=dict(os.environ, BYOA_WALLET_HOME=a.home))
try:
    ui.find_window("Agent wallet", 40)
    time.sleep(2.0)

    # ------------------------------------------------------------------------------------------ the short tab list
    w = settle("sites")
    check("the tab list is short at first: Sites, My models, Everywhere, Activity",
          all(has_control(w, "Tab-" + t) for t in ("sites", "sources", "everywhere", "activity")))
    check("... and Agent guard and My MCP wait behind \"Show all settings\"", not has_control(w, "Tab-guard", 1) and not has_control(w, "Tab-connect", 1))
    check("the switch \"Show all settings\" is there", has_control(w, "ShowAllSettings"))

    # ------------------------------------------------------------------------------------------ Get started
    check("a wallet with no model shows the Get started card on Sites", has_text(w, "Get started"))
    check("... with the free demo models offered", has_text(w, "free demo model"))
    if a.shots:
        desk.shot(w, os.path.join(a.shots, "sites-get-started.png"))
    w = settle("sources")
    check("My models shows the Get started card as well", has_text(w, "Get started"))

    # ------------------------------------------------------------------------------------------ Connect a site by link
    w = settle("sites")
    check("Sites has the button \"Connect a site by link\"", has_control(w, "ConnectByLink"))
    ui.invoke(w, "ConnectByLink")
    c = ui.find_window("Connect a site by link", 10)
    if c is None:                                       # a window owned by the settings window may hang under it in the UIA tree
        owned = w.WindowControl(searchDepth=4, Name="Connect a site by link")
        c = owned if owned.Exists(3, 0.2) else None
    if c is None:
        print("windows:", [x.Name for x in auto.GetRootControl().GetChildren() if x.Name][:20], [x.Name for x in w.GetChildren()][:20])
    check("the button opens the window with a box for the link", c is not None and has_control(c, "ConnectByLinkText"))
    if c is not None:
        c.Control(AutomationId="ConnectByLinkText", searchDepth=30).GetPattern(auto.PatternId.ValuePattern).SetValue("this is not a link")
        time.sleep(0.5)
        ui.invoke(c, "ConnectByLinkGo")
        time.sleep(1.2)
        error = ui.control(c, "ConnectByLinkError", 5)
        check("a text that is not a link gets a plain error, not a connection", error is not None and len(error.Name or "") > 10, (error.Name if error else "")[:100])
        if a.shots:
            desk.shot(c, os.path.join(a.shots, "connect-by-link-error.png"))
        ui.invoke(c, "ConnectByLinkCancel")
        check("Cancel closes the window", ui.window_gone("Connect a site by link", 6))

    # ------------------------------------------------------------------------------------------ Everywhere
    w = settle("everywhere")
    check("the Everywhere tab opens and explains dictation", has_text(w, "ictat"))
    if a.shots:
        desk.shot(w, os.path.join(a.shots, "everywhere.png"))

    # ------------------------------------------------------------------------------------------ Show all settings
    ui.toggle(w, "ShowAllSettings", True)
    time.sleep(1.5)
    w = ui.find_window("Agent wallet", 10)
    check("with \"Show all settings\" the other tabs appear", all(has_control(w, "Tab-" + t) for t in ("guard", "connect", "myagents")))
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
