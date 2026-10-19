"""The visitor's side of a link, played on a hidden desktop (nothing appears on the screen somebody works at): starts the wallet with a
bringyourownagent:// link, presses Show, picks the models in the consent window (with the wallet's own search over OpenRouter's catalog),
checks what the window says, presses Allow, and keeps the wallet running until a line "stop" arrives on stdin. Prints one line per step
("STEP ..."), "ALLOWED" when the site has been allowed and "FAILED <why>" otherwise.

    <python with uiautomation, pillow> wallet_side.py --exe <Byoa.Wallet.exe> --home <wallet data folder> --link <link>
        [--models deepseek/deepseek-v4.1-flash,z-ai/glm-5.3-flash] [--key-file <file with the OpenRouter key>] [--shots <dir>]

The wallet runs with its own data folder (BYOA_WALLET_HOME): the visitor's own wallet, its settings and its pipe are never touched.
--key-file: the key is imported into that folder's protected store first and the file is blanked at once.
Buttons are pressed through UI Automation (never the mouse: it would land on the visitor's desktop).
"""
import argparse
import os
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hidden_desktop as hd  # noqa: E402  (before anything that starts COM)

ap = argparse.ArgumentParser()
ap.add_argument("--exe", required=True)
ap.add_argument("--home", required=True)
ap.add_argument("--link", required=True)
ap.add_argument("--models", default="", help="OpenRouter model ids to tick, comma separated (everything else is unticked)")
ap.add_argument("--key-file", default=None)
ap.add_argument("--shots", default=None)
ap.add_argument("--desktop", default="byoa-wallet-side")
ap.add_argument("--decline", action="store_true", help="press Decline instead of Allow (the window is checked all the same)")
a = ap.parse_args()

os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)
env = dict(os.environ, BYOA_WALLET_HOME=a.home)


def say(text):
    print(text, flush=True)


def fail(why):
    say("FAILED " + why)
    sys.exit(1)


if a.key_file:
    p = desk.start([a.exe, "--import-key", "openrouter", a.key_file], env=env)
    end = time.time() + 30
    while p.alive() and time.time() < end:
        time.sleep(0.2)
    open(a.key_file, "w").close()                       # blanked at once; a blank file stays (nothing is deleted)
    say("STEP key imported")

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe, "--url", a.link], env=env)
ui.ONLY_PIDS = {wallet.pid}
say(f"STEP wallet started pid={wallet.pid}")


def shot(window, name):
    if a.shots:
        desk.shot(window, os.path.join(a.shots, name + ".png"))


def boxes(window):
    """(automation id, name, ticked) of every check box in the window."""
    found = []

    def walk(c, depth):
        try:
            for ch in c.GetChildren():
                if ch.ControlTypeName == "CheckBoxControl":
                    toggle = ch.GetPattern(auto.PatternId.TogglePattern)
                    found.append((ch.AutomationId, ch.Name, bool(toggle and toggle.ToggleState == 1)))
                if depth < 30:
                    walk(ch, depth + 1)
        except Exception:  # noqa: BLE001
            pass

    walk(window, 0)
    return found


def texts(window, ident):
    c = window.Control(AutomationId=ident, searchDepth=40)
    return c.Name if c.Exists(0.5, 0.1) else None


notice = ui.find_window("Agent wallet notice", 40)
if notice is None:
    fail("no notice window")
shot(notice, "1-notice")
ui.click(notice, "NoticeShow")
consent = ui.find_window("Agent wallet consent", 20)
if consent is None:
    fail("no consent window")
time.sleep(1.5)
shot(consent, "2-consent")
say("STEP consent shown")
say("INFO why=" + str(texts(consent, "ConsentModelsWhy")))
say("INFO count=" + str(texts(consent, "ConsentModelsCount")))
for ident, name, ticked in boxes(consent):
    if ident.startswith("ConsentModel-"):
        say(f"INFO row {'[x]' if ticked else '[ ]'} {ident[len('ConsentModel-'):]} | {name}")

wanted = [m.strip() for m in a.models.split(",") if m.strip()]
if wanted:
    for ident, name, ticked in boxes(consent):                       # untick what the wallet proposed
        if ident.startswith("ConsentModel-") and ticked and ident[len("ConsentModel-new:openrouter:"):] not in wanted:
            ui.toggle(consent, ident, on=False)
    ui.invoke(consent, "ConsentModelsChange")
    time.sleep(1.0)
    for model in wanted:
        search = ui.control(consent, "ConsentModelsSearch")
        search.GetPattern(auto.PatternId.ValuePattern).SetValue(model.split("/", 1)[-1])
        ident = "ConsentModel-new:openrouter:" + model
        deadline = time.time() + 15
        box = None
        while time.time() < deadline and box is None:
            time.sleep(0.4)
            c = consent.Control(AutomationId=ident, searchDepth=40)
            box = c if c.Exists(0.3, 0.1) else None
        if box is None:
            fail("model not found in the wallet's search: " + model)
        ui.toggle(consent, ident, on=True)
        say("STEP ticked " + model)
    time.sleep(0.8)
    say("INFO count=" + str(texts(consent, "ConsentModelsCount")))
    shot(consent, "3-picked")

if a.decline:
    ui.invoke(consent, "ConsentDecline")
    say("DECLINED")
else:
    allow = ui.control(consent, "ConsentAllow")
    if not allow.IsEnabled:
        fail("Allow is disabled")
    ui.invoke(consent, "ConsentAllow")
    say("ALLOWED")

# keep the wallet alive until told to stop; keep taking pictures of its other windows on request
stop = threading.Event()


def reader():
    for line in sys.stdin:
        if line.strip() == "stop":
            break
    stop.set()


threading.Thread(target=reader, daemon=True).start()
while not stop.is_set() and wallet.alive():
    time.sleep(0.5)
desk.terminate(wallet)
say("STOPPED")
