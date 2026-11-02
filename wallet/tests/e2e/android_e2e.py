"""End to end for the Android wallet, on a real phone, through adb. The phone is driven the way a person would: a link is opened with a VIEW intent
(as Chrome opens it after "Open with Agent wallet?"), the screens are read from a uiautomator dump and looked at in screenshots, and buttons,
ticks and fields are found by what they say and tapped where they are (see android_ui.py).

    python android_e2e.py --adb <adb.exe> --serial <serial> --shots <new folder> [--apk <apk>] [--build] [--until <step>]

Run it with a Python that has aiohttp (fake_site.py needs it). It builds the APK with --build (the Debug build, android-arm64, assemblies
embedded) or takes --apk, reinstalls the app (uninstall, install: the wallet starts with no data and a new Keystore key), then:

 1. starts two helpers on this computer, each in a hidden process of its own (android_helpers.py): fake_site.py with a text site that shows the
    check code, and a fake OpenAI-compatible model server; `adb reverse` makes both "localhost" for the phone (a Debug build allows ws://localhost);
 2. the home screen; a key is saved in My models and found sealed in the file, and again after the app is stopped and started; the fake model is
    added (My models, Add a model on a server, Test, Add);
 3. a link the visitor declines: the site is never contacted, and nothing is stored;
 4. a link he allows: the check code on the phone is the one the site computes, Allow waits for the tick, the app asks for the notification
    permission at that first moment, the foreground service and its notification appear, the site's call is answered by the fake model, and
    everything goes when the site goes;
 5. the same link twice is used once; the Back button declines a consent; a consent survives the activity being made again; a question that
    comes while the app is in the background is a notification that opens it; a link pasted into "Connect by link";
 6. "Stop" in the notification ends the connection; Sites and Activity show what happened.

No test hook answers a consent: every answer is a tap on the screen. Screenshots of every step are in --shots (look at them: a test that passes on a
screen that looks broken is not a pass). Nothing but the wallet's app is touched on the phone, and no setting. The helpers are stopped at the end
by their process ids.
"""
import argparse
import atexit
import hashlib
import urllib.request
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.stdout.reconfigure(encoding="utf-8", errors="replace")          # what the phone shows has characters a console may not
import android_helpers as helpers  # noqa: E402
from android_ui import PACKAGE, Phone  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--adb", required=True)
ap.add_argument("--serial", required=True)
ap.add_argument("--shots", required=True)
ap.add_argument("--work", help="a folder for the helpers' logs (default: <shots>/work)")
ap.add_argument("--apk")
ap.add_argument("--build", action="store_true")
ap.add_argument("--sdk", default=r"C:\Program Files (x86)\Android\android-sdk")
ap.add_argument("--until", type=int, default=99, help="stop after this step number")
ap.add_argument("--grant-notifications", action="store_true", help="grant the notification permission with pm grant instead of answering the app's own question")
a = ap.parse_args()

os.makedirs(a.shots, exist_ok=True)
work = a.work or os.path.join(a.shots, "work")
os.makedirs(work, exist_ok=True)
PROJECT = os.path.normpath(os.path.join(HERE, "..", "..", "android", "Byoa.Wallet.Android"))
APK = a.apk or os.path.join(PROJECT, "bin", "Debug", "net10.0-android", "android-arm64", PACKAGE + "-Signed.apk")

phone = Phone(a.adb, a.serial, a.shots)
failed = []
checks = []
started = []
HOME_DIR = "files/BYOA/wallet"


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    checks.append({"check": label, "ok": bool(ok), "extra": str(extra)})
    if not ok:
        failed.append(label)


def note(text):
    print("---- " + text, flush=True)


def stop_helpers():
    """Ends the helpers this run started, by their process ids (also when the run ends by an error)."""
    while started:
        try:
            started.pop().stop()
        except Exception:  # noqa: BLE001
            pass


atexit.register(stop_helpers)


def finish(code=None):
    stop_helpers()
    with open(os.path.join(a.shots, "results.json"), "w", encoding="utf-8") as f:
        json.dump({"checks": checks, "failed": failed}, f, indent=1)
    print(f"\n{len(checks) - len(failed)} of {len(checks)} checks passed" + (": FAILED " + "; ".join(failed) if failed else ""), flush=True)
    sys.exit(code if code is not None else (1 if failed else 0))


def stop_at(step):
    if a.until <= step:
        note(f"stopped after step {step}")
        finish()


def wait(predicate, seconds=30, step=0.5):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(step)
    return predicate()


MANIFEST = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "Answer a small yes or no question", "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 20}},
    "skills": {"text.generate": {"required": True}},
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
    "connect": {"check": True},
}
MANIFEST_MODELS = {
    **MANIFEST,
    "purpose": {"summary": "Ask several of your models the same yes or no question", "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 20}},
    "skills": {"text.generate": {"required": True, "models": {"count": {"min": 2, "max": 3}, "why": "More models give more opinions."}}},
}
QUESTION = "Answer with yes or no: is two plus two four? (test question 4711)"


def check_code(session):
    """CONTRACT-DIRECT.md section 3a: character i is alphabet[SHA-256("BYOA-CHECK-1:" + session)[i] mod 31]."""
    alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    digest = hashlib.sha256(("BYOA-CHECK-1:" + session).encode()).digest()
    return "".join(alphabet[digest[i] % 31] for i in range(4))


def settings():
    raw = phone.app_file(HOME_DIR + "/settings.json")
    return json.loads(raw) if raw else {}


def activity_lines():
    out = []
    for name in phone.app_files(HOME_DIR):
        if name.startswith("activity-") and name.endswith(".jsonl"):
            raw = phone.app_file(HOME_DIR + "/" + name) or b""
            out += [json.loads(line) for line in raw.decode("utf-8", errors="replace").splitlines() if line.strip()]
    return out


PROMPT_CHANNEL = "prompts-debug"           # a Debug build's own channel (a Release build's is "prompts")


def on_home_screen_text(nodes):
    return on_home(nodes)


def serving_notification():
    return [n for n in phone.app_notifications() if n["id"] == 1]


def prompt_notification():
    return [n for n in phone.app_notifications() if n["id"] == 2]


def on_consent(nodes):
    return bool(phone.find(nodes, contains="Asks for", cls="TextBlock"))


def on_home(nodes):
    """The home screen itself (the pages under it have a "Connect by link" button too, so it is the home's own line that is looked for)."""
    return bool(phone.find(nodes, contains="Your own AI, for the sites you allow"))


def button(nodes, text):
    found = phone.find(nodes, text=text, cls="Button")
    return found[0] if found else None


def tick(nodes, contains):
    found = phone.find(nodes, contains=contains, cls="CheckBox")
    return found[0] if found else None


def press(text, cls="Button"):
    """Presses a button of the page, scrolling to it first when it is not on the screen (the dump holds only what is on the screen; the keyboard takes
    the lower half, so it is put away first)."""
    phone.hide_keyboard()
    found = phone.wait_for(lambda ns: phone.find(ns, text=text, cls=cls), 4)           # the page may still be moving (the keyboard coming or going)
    node = found[0] if found else phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, text=text, cls=cls)), None), (0, 250, 720, 1380))
    if node is None:
        raise LookupError(f"no {text!r} button, however far the page was scrolled")
    phone.tap(node)


def row_button(nodes, name, text):
    """The button `text` of the row of a list that is called `name`: the one nearest above or below the row's name on the screen."""
    label = next(iter(phone.find(nodes, text=name)), None)
    found = phone.find(nodes, text=text, cls="Button")
    if label is None or not found:
        raise LookupError(f"no {text!r} button in the row {name!r}: {phone.texts(nodes)[:30]}")
    return min(found, key=lambda b: abs(b.center[1] - label.center[1]))


def consent_frame(nodes):
    """The middle of the consent page (what scrolls): the one scroll viewer of the screen."""
    views = phone.find(nodes, cls="ScrollViewer")
    return (views[0].left, views[0].top, views[0].right, views[0].bottom) if views else None


def shown_code(nodes):
    codes = [n.label for n in phone.find(nodes, cls="TextBlock") if re.fullmatch(r"[A-Z2-9]{4}", n.label)]
    return codes[0] if codes else None


def go_home():
    phone.start_app()
    for _ in range(4):                  # the visitor presses Back from wherever the last step left him to the home screen
        if phone.wait_for(on_home, 4):
            return
        phone.back()
    phone.wait_for(on_home, 15)


def open_door(name):
    """A door of the home screen (a button of the list; the last ones are under the first screen: the page is scrolled)."""
    go_home()
    for _ in range(3):
        found = button(phone.dump(), name)
        if found is not None and found.center[1] < phone.screen_size()[1] - 120:
            phone.tap(found)
            return
        phone.swipe_up()
    raise LookupError("no door " + name)


def toggle_model(name):
    nodes = phone.dump()
    phone.tap(next(n for n in phone.find(nodes, cls="CheckBox") if name in n.label))


PAGE = (0, 250, 720, 1380)


def reach(text=None, contains=None, cls=None):
    """Scrolls the page, up or down, until the control is on the screen, and returns it (None when the page has none)."""
    return phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, text=text, contains=contains, cls=cls)), None), PAGE)


def tap_on(text=None, contains=None, cls=None):
    node = reach(text=text, contains=contains, cls=cls)
    if node is None:
        raise LookupError(f"no control {text or contains!r} on the page: {phone.texts()[:30]}")
    phone.tap(node)
    return node


def open_consent(link, name, first=False):
    """Opens a link as a browser does, and waits for the consent. With `first` (the first question of a fresh install) the system asks first, over the
    app, whether the wallet may show notifications: the visitor allows it, and the consent is under it."""
    phone.open_link(link)
    if first and not a.grant_notifications:
        open_consent.dialog = answer_system_dialog(allow=True, seconds=40)
    got = phone.wait_for(on_consent, 40)
    phone.screenshot(name)
    return got


open_consent.dialog = None


def answer_system_dialog(allow=True, seconds=6):
    """The system's question about notifications for the app (Android 13 and later), shown by the permission controller. Returns its text or None."""
    def dialog(nodes):
        return [n for n in nodes if "permissioncontroller" in n.pkg or "packageinstaller" in n.pkg]
    got = phone.wait_for(dialog, seconds, step=0.5)
    if not got:
        return None
    nodes = phone.dump()
    text = " ".join(n.label for n in nodes if n.label and ("permissioncontroller" in n.pkg or "packageinstaller" in n.pkg))
    phone.screenshot("system-notification-question")
    wanted = [n for n in nodes if ("allow_button" in n.rid and allow) or ("deny_button" in n.rid and not allow)]
    if wanted:
        phone.tap(wanted[0])
    return text


# ---------------------------------------------------------------------------------------------- 0. build, install
note("0. the app on the phone")
if a.build:
    r = subprocess.run(["dotnet", "build", "-c", "Debug", "-r", "android-arm64", "-m:4", "-p:EmbedAssembliesIntoApk=true", f"-p:AndroidSdkDirectory={a.sdk}"],
                       cwd=PROJECT, capture_output=True, text=True, creationflags=helpers.CREATE_NO_WINDOW)
    open(os.path.join(work, "build.log"), "w", encoding="utf-8").write(r.stdout + r.stderr)
    check("the Debug APK builds", r.returncode == 0, "see work/build.log" if r.returncode else "")
    if r.returncode:
        finish()
check("the APK exists", os.path.exists(APK), APK)
devices = subprocess.run([a.adb, "devices"], capture_output=True, text=True, creationflags=helpers.CREATE_NO_WINDOW).stdout
check("the phone is there", re.search(re.escape(a.serial) + r"\s+device", devices) is not None)
phone.wake()
phone.run("uninstall", PACKAGE)                           # the wallet's own app only: it starts with no data and a new Keystore key
for attempt in range(3):                                  # an install that hangs (the phone asleep, the cable) is tried again, with the phone awake
    try:
        r = phone.run("install", "-r", APK, timeout=120)
        break
    except subprocess.TimeoutExpired:
        print("     the install did not finish in two minutes: trying again", flush=True)
        phone.wake()
else:
    raise SystemExit("the APK could not be installed")
check("the APK installs", b"Success" in r.stdout, r.stdout.decode(errors="replace").strip()[-80:])
if a.grant_notifications:
    phone.shell(f"pm grant {PACKAGE} android.permission.POST_NOTIFICATIONS")     # the app's own permission, which the test may grant
phone.shell("logcat -c")

# ---------------------------------------------------------------------------------------------- 1. the helpers
note("1. the helpers on this computer, hidden, with adb reverse")
model_port = 18234
model_requests = os.path.join(work, "model-requests.jsonl")
open(model_requests, "w").close()
model = helpers.start_model(model_port, model_requests, os.path.join(work, "model.stderr.log"))
started.append(model)
manifest_path = os.path.join(work, "manifest.json")
json.dump(MANIFEST, open(manifest_path, "w"))
json.dump(MANIFEST_MODELS, open(os.path.join(work, "manifest-models.json"), "w"))
site = helpers.start_site(manifest_path, os.path.join(work, "site.log"), os.path.join(work, "site.stderr.log"))
started.append(site)
phone.run("reverse", f"tcp:{site.port}", f"tcp:{site.port}", check=True)
phone.run("reverse", f"tcp:{model_port}", f"tcp:{model_port}", check=True)
check("the site and the model server run", site.proc.poll() is None and model.proc.poll() is None, f"site :{site.port}, model :{model_port}")
# A Debug build reads this file of its own folder at start (a door for tests only): OpenRouter's models and list then come from the model server above,
# which also plays a fake OpenRouter whose "paid" model reports a price. Nothing real is spent, and the key a test saves is made up.
phone.shell(f"run-as {PACKAGE} sh -c 'mkdir -p files && echo http://127.0.0.1:{model_port} > files/debug-openrouter.txt'")
check("the app is told where its fake OpenRouter is (Debug only)", (phone.app_file("files/debug-openrouter.txt") or b"").decode().strip() == f"http://127.0.0.1:{model_port}")
stop_at(1)

# ---------------------------------------------------------------------------------------------- 2. the home screen, a key, the model
note("2. the app starts; a key is sealed; the fake model is added")
phone.start_app()
home = phone.wait_for(on_home, 40)
phone.screenshot("home")
nodes = phone.dump()
check("the home screen is there with its four buttons", bool(home) and all(button(nodes, t) for t in ("Connect by link", "My models", "Sites", "Activity")))
check("it says that no site is connected", bool(phone.find(nodes, text="No site is connected.")))
check("it says where keys are kept", bool(phone.find(nodes, contains="Android Keystore")))
check("no foreground service runs and no notification of the app is up", not phone.service_running() and not phone.app_notifications())

phone.tap_text("My models", cls="Button")
phone.wait_text(text="Add a model on a server", cls="Button")
phone.screenshot("my-models-empty")
KEY = "sk-or-test-0123456789abcdefXYZ"
phone.type_text(phone.find(phone.dump(), cls="TextBox")[0], KEY)
press("Save the key")
saved = phone.wait_text(contains="A key is saved", seconds=15)
phone.screenshot("key-saved")
check("a key saved in My models is reported as saved, and never shown", saved is not None and not phone.find(phone.dump(), contains=KEY))
sealed = phone.app_file(HOME_DIR + "/secrets.dat") or b""
check("secrets.dat is there, sealed (the format of the Keystore protector), and has no plain text of the key",
      sealed.startswith(b"BYOAAK1\0") and KEY.encode() not in sealed and b"openrouter" not in sealed and b"sk-or" not in sealed, f"{len(sealed)} bytes")
phone.stop_app()
phone.start_app()
phone.wait_for(on_home, 40)
phone.tap_text("My models", cls="Button")
again = phone.wait_text(contains="A key is saved", seconds=15)
check("after the app is stopped and started the sealed key opens again with the Keystore key", again is not None)
phone.tap_text("Remove the key", cls="Button")
question = phone.wait_text(text="Delete the saved key?", seconds=10)
phone.screenshot("key-delete-question")
check("Remove the key asks first, with Delete and Keep", question is not None and bool(button(phone.dump(), "Delete")) and bool(button(phone.dump(), "Keep")))
phone.tap_text("Keep", cls="Button")
check("Keep leaves the key where it is", phone.wait_text(contains="A key is saved", seconds=10) is not None)
phone.tap_text("Remove the key", cls="Button")
phone.wait_text(text="Delete the saved key?", seconds=10)
phone.tap_text("Delete", cls="Button")
gone = phone.wait_text(contains="Optional. One key", seconds=10)
check("Delete takes the key away", gone is not None and not phone.service_running())

phone.tap_text("Add a model on a server", cls="Button")
phone.wait_text(text="Test", cls="Button")
phone.screenshot("add-server-empty")
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
check("the Add a model page has its three fields", len(fields) == 3, [repr(f) for f in fields])
phone.type_text(fields[0], f"http://127.0.0.1:{model_port}/v1")
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
phone.type_text(fields[1], helpers.MODEL_ID)
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
phone.type_text(fields[2], "Fake GPT")
phone.screenshot("add-server-filled")
add = phone.find(phone.dump(), text="Add", cls="Button")
check("Add waits for a passing test", bool(add) and not add[0].enabled, repr(add))
phone.tap_text("Test", cls="Button")
passed = phone.wait_text(contains="Test passed", seconds=40)
phone.screenshot("add-server-tested")
check("Test passes against the fake model server", passed is not None, phone.texts()[-6:] if passed is None else passed.label)
add = phone.find(phone.dump(), text="Add", cls="Button")
check("Add is on after the test", bool(add) and add[0].enabled)
phone.tap_text("Add", cls="Button")
listed = phone.wait_text(contains="Fake GPT", seconds=20)
phone.screenshot("my-models-with-model")
check("the model is in the list of My models", listed is not None, phone.texts()[:20])
check("the fake model server got the wallet's test question", any(json.loads(line) for line in open(model_requests) if line.strip()))
phone.back()
phone.wait_for(on_home, 15)
stop_at(2)

# ---------------------------------------------------------------------------------------------- 3. a link that is declined
note("3. a link that the visitor declines")
declined = site.ask(cmd="link")
consent = open_consent(declined["link"], "decline-consent-top", first=True)
if not a.grant_notifications:
    check("the first question makes the app ask whether it may show notifications (not the first Allow: the first question)",
          open_consent.dialog is not None and "notif" in open_consent.dialog.lower(), open_consent.dialog)
    if open_consent.dialog is None:
        phone.shell(f"pm grant {PACKAGE} android.permission.POST_NOTIFICATIONS")
check("a link opened on the phone shows the consent page", bool(consent))
nodes = phone.dump()
check("the page names the site and says what it is for", bool(phone.find(nodes, text=f"localhost:{site.port}")) and bool(phone.find(nodes, text="Answer a small yes or no question")))
check("it says what the site asks for: Text generation, with the model that would answer", bool(phone.find(nodes, text="Asks for 1:")) and bool(phone.find(nodes, text="Text generation"))
      and bool(phone.find(nodes, contains="Fake GPT")))
check("it says where the model runs in the words of a phone: this phone, not this PC or this computer",
      bool(phone.find(nodes, contains="this phone")) and not phone.find(nodes, contains="this PC") and not phone.find(nodes, contains="this computer"), phone.texts(nodes)[:30])
check("the check code on the phone is the one the site computes from its session", shown_code(nodes) == check_code(declined["session"]), f"{shown_code(nodes)} / {check_code(declined['session'])}")
check("it shows the budget", bool(phone.find(nodes, contains="Up to $0.05")))
allow = button(nodes, "Allow")
check("Allow is off until the code is confirmed, and the page says why", allow is not None and not allow.enabled
      and bool(phone.find(nodes, contains="Tick that the code is the same")))
state = site.ask(cmd="state")
check("the site has had no request of any kind while the page is up (no contact before Allow)", state["requests"] == 0 and state["upgrades"] == 0, state)
# the visitor leaves the first question unanswered (Home): it is announced, because he allowed notifications when it was put to him
phone.home_key()
announced = wait(lambda: bool(prompt_notification()), 15)
check("a first consent left unanswered is announced by a notification (the permission was asked with the first question)", announced, [n["title"] for n in prompt_notification()])
channel = phone.channel(PROMPT_CHANNEL)
phone.screenshot("first-consent-left")
check("the question's channel, read from the system: high importance, no sound, no vibration (a Debug build's, for a phone somebody sleeps beside)",
      channel is not None and channel["importance"] == 4 and channel["sound"] == "null" and channel["vibration_enabled"] is False, channel)
raw_prompt = (prompt_notification() or [{}])[0].get("raw", "")
check("on the lock screen it is private, with a public version that names no site", "vis=PRIVATE" in raw_prompt and "publicVersion=Notification" in raw_prompt, raw_prompt[:300])
check("the visitor taps the notification in the shade and the first consent is there again, with its code",
      phone.tap_in_shade("wants to use your AI") and bool(phone.wait_for(on_consent, 20)) and shown_code(phone.dump()) == check_code(declined["session"]))
nodes = phone.dump()
frame = consent_frame(nodes)
header = tick(nodes, "Allow Text generation")
check("the block has its Allow box, ticked", header is not None and header.checked, header)
if header is not None:
    phone.tap(tick(nodes, "The code is the same"))
    check("with the code ticked Allow is on", button(phone.dump(), "Allow").enabled)
    phone.tap(tick(phone.dump(), "Allow Text generation"))
    nodes = phone.dump()
    phone.screenshot("block-unticked")
    check("unticking the required block puts Allow off and says why (the site cannot work without it)",
          bool(phone.find(nodes, contains="cannot work without Text generation")) and not button(nodes, "Allow").enabled)
    phone.tap(tick(nodes, "Allow Text generation"))
    check("ticking it again puts Allow back on", button(phone.dump(), "Allow").enabled)
for text in ("why?",):
    why = phone.find(phone.dump(), text=text, cls="Button")
    if why:
        phone.tap(why[0])
        phone.screenshot("why-open")
        check("why? opens the needs of the skill", bool(phone.find(phone.dump(), contains="skill ")) or bool(phone.find(phone.dump(), contains="text.generate")))
phone.tap(button(phone.dump(), "Decline"))
back_home = phone.wait_for(on_home, 15)
phone.screenshot("after-decline")
check("Decline returns to the home screen", bool(back_home))
time.sleep(3)
state = site.ask(cmd="state")
check("after Decline the site was never contacted", state["requests"] == 0 and state["upgrades"] == 0 and state["hellos"] == 0, state)
records = [x for x in settings().get("sites", []) if x.get("origin") == site.origin]
check("nothing is stored for the site: no allowance, no skills, no decline or block, no pairing file",
      all(str(x.get("status", "none")).lower() in ("none", "0") and not x.get("skills") for x in records) and "pairings.dat" not in phone.app_files(HOME_DIR),
      (records, phone.app_files(HOME_DIR)))
declined_entries = [e for e in activity_lines() if e.get("kind") == "request" and e.get("outcome") == "declined"]
check("the activity says: declined from a link, nothing stored", len(declined_entries) == 1 and "nothing stored" in (declined_entries[0].get("note") or ""), declined_entries)
check("no service and no notification after a decline", not phone.service_running() and not serving_notification())
stop_at(3)

# ---------------------------------------------------------------------------------------------- 4. a link that is allowed
note("4. a link that the visitor allows")
good = site.ask(cmd="link")
consent = open_consent(good["link"], "allow-consent-top")
nodes = phone.dump()
check("the second link shows its own consent page", bool(consent) and shown_code(nodes) == check_code(good["session"]), shown_code(nodes))
check("the codes of the two links differ (the code belongs to the session)", check_code(good["session"]) != check_code(declined["session"]))
box = tick(nodes, "The code is the same")
check("the tick for the code is there and not made", box is not None and not box.checked)
phone.tap(button(nodes, "Allow"))                                    # a disabled button: nothing happens
nodes = phone.dump()
check("a tap on Allow before the tick does nothing", on_consent(nodes) and site.ask(cmd="state")["upgrades"] == 0)
phone.tap(box)
nodes = phone.dump()
phone.screenshot("allow-ticked")
check("after the tick Allow is on", button(nodes, "Allow").enabled and tick(nodes, "The code is the same").checked)
phone.tap(tick(phone.dump(), "The code is the same"))
check("taking the tick away puts Allow off again", not button(phone.dump(), "Allow").enabled)
phone.tap(tick(phone.dump(), "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
check("Allow does not ask about notifications again", answer_system_dialog(allow=True, seconds=3) is None)
hello = site.ask(cmd="wait_hello", seconds=40)["ok"]
state = site.ask(cmd="state")
last = state["last_hello"] or {}
check("after Allow the wallet connects to the site and says hello with the link's session code", hello and last.get("session") == good["session"], last)
check("the grant in the hello is the one the visitor gave: text.generate", (last.get("grant") or {}).get("skills") == ["text.generate"], last.get("grant"))
check("the wallet's first contact was the WebSocket upgrade (the manifest came in the link: no other request)", state["manifest_gets"] == 0, state)

phone.wait_for(on_home, 20)
nodes = phone.wait_for(lambda ns: phone.find(ns, contains="Serving 1 site"), 20)
phone.screenshot("home-serving")
check("the home screen says: serving 1 site, and names it", bool(nodes) and bool(phone.find(phone.dump(), text=f"localhost:{site.port}")))
check("under the site it says that it is connected", bool(phone.find(phone.dump(), text="connected")), phone.texts()[:12])
check("the foreground service is running", wait(phone.service_running, 15), phone.shell(f"dumpsys activity services {PACKAGE} | head -30")[:300])
services = phone.shell(f"dumpsys activity services {PACKAGE}")
check("it is a foreground service of the special-use type (types=40000000 is 0x40000000, FOREGROUND_SERVICE_TYPE_SPECIAL_USE)", "isForeground=true" in services and re.search(r"types=(0x)?40000000", services) is not None,
      [l.strip() for l in services.splitlines() if "foreground" in l.lower() or "types=" in l][:4])
notes = serving_notification()
check("the persistent notification says: Agent wallet is serving 1 site, with a Stop action",
      bool(notes) and "serving 1 site" in notes[0]["title"] and "Stop" in notes[0]["actions"], [{k: v for k, v in n.items() if k != "raw"} for n in notes])
if notes:
    open(os.path.join(work, "notification-serving.txt"), "w", encoding="utf-8").write(notes[0]["raw"])

result = site.ask(cmd="call_text", choices=["yes", "no"], text=QUESTION, seconds=90)
check("the site's call is answered", result.get("ok") is True and (result.get("output") or {}).get("text") == "yes", {k: result.get(k) for k in ("ok", "output", "receipt", "error")})
asked = [json.loads(line) for line in open(model_requests) if line.strip()]
check("the answer came from the fake model server: it got the site's question", any("4711" in json.dumps(r.get("messages")) for r in asked), len(asked))
calls = [e for e in activity_lines() if e.get("kind") == "call"]
check("the activity has the call, marked via link, answered by the fake model", len(calls) == 1 and calls[0].get("outcome") == "ok" and calls[0].get("via") == "link"
      and "Fake GPT" in (calls[0].get("source") or ""), calls)

phone.tap_text("Sites", cls="Button")
phone.wait_text(text="Sites", cls="TextBlock")
nodes = phone.dump()
phone.screenshot("sites-allowed")
check("Sites lists the site as Allowed and connected now", bool(phone.find(nodes, text="Allowed")) and bool(phone.find(nodes, text="connected now")) and bool(phone.find(nodes, text=f"localhost:{site.port}")))
phone.back()
phone.tap_text("Activity", cls="Button")
phone.wait_text(text="Activity", cls="TextBlock")
nodes = phone.dump()
phone.screenshot("activity")
check("Activity shows the call as answered and the connection", bool(phone.find(nodes, text="Answered")) and bool(phone.find(nodes, text="Connected")), phone.texts(nodes)[:30])
phone.back()
stop_at(4)

note("4b. the site goes away: the notification and the service go")
site.ask(cmd="drop")
check("when the site closes the connection the foreground service stops", wait(lambda: not phone.service_running(), 30))
check("... and its notification is gone", wait(lambda: not serving_notification(), 15), serving_notification())
nodes = phone.wait_for(lambda ns: phone.find(ns, text="No site is connected."), 20)
phone.screenshot("home-after-drop")
check("the home screen says no site is connected", bool(nodes))
stop_at(5)

# ---------------------------------------------------------------------------------------------- 5. the same link twice, Back, recreate, background, pasted
note("5. the same link twice; Back; the activity made again; the app in the background; a pasted link")
twice = site.ask(cmd="link")
phone.open_link(twice["link"])
phone.wait_for(on_consent, 40)
phone.open_link(twice["link"])
time.sleep(3)
nodes = phone.dump()
phone.screenshot("same-link-twice")
check("the same link twice shows one consent page", on_consent(nodes) and len(phone.find(nodes, text="Asks for 1:")) == 1)
phone.back()
check("the Back button declines the consent and leaves the home screen", bool(phone.wait_for(on_home, 15)))
time.sleep(2)
ignored = [e for e in activity_lines() if e.get("kind") == "link" and e.get("outcome") == "ignored"]
declined_now = [e for e in activity_lines() if e.get("kind") == "request" and e.get("outcome") == "declined"]
check("the second look at the same link was ignored by the engine, and Back was written as a decline", len(ignored) == 1 and len(declined_now) == 2, (len(ignored), len(declined_now)))
check("the site was not contacted", site.ask(cmd="state")["upgrades"] == 1)         # only the one connection of step 4

# a link that finds the app not running at all (a cold start: the link starts the process, and the consent is there when the screen is)
phone.stop_app()
time.sleep(1.5)
check("the app is not running before the link", phone.shell(f"pidof {PACKAGE}").strip() == "")
recreate = site.ask(cmd="link")
phone.open_link(recreate["link"])
cold = phone.wait_for(on_consent, 40)
phone.screenshot("cold-start-consent")
check("a link that starts the app from nothing shows its consent, with the site's code", bool(cold) and shown_code(phone.dump()) == check_code(recreate["session"]), shown_code(phone.dump()))
before = shown_code(phone.dump())
phone.debug_recreate()
time.sleep(2.5)
nodes = phone.dump()
phone.screenshot("after-recreate")
check("the consent page is still there, with the same code, after the activity was destroyed and made again", on_consent(nodes) and shown_code(nodes) == before == check_code(recreate["session"]), (before, shown_code(nodes)))
phone.tap(tick(nodes, "The code is the same"))
phone.tap(button(phone.dump(), "Decline"))
check("and it can be answered", bool(phone.wait_for(on_home, 15)))

background = site.ask(cmd="link")
phone.open_link(background["link"])
phone.wait_for(on_consent, 40)
phone.home_key()
shown = wait(lambda: bool(prompt_notification()), 15)
phone.screenshot("app-in-background")
prompt = prompt_notification()
check("a consent that waits while the app is not on the screen is a notification", shown, [{k: v for k, v in n.items() if k != "raw"} for n in prompt])
check("the notification names the site", shown and f"localhost:{site.port}" in prompt[0]["title"], prompt[0]["title"] if prompt else None)
if prompt:
    open(os.path.join(work, "notification-prompt.txt"), "w", encoding="utf-8").write(prompt[0]["raw"])
check("it is on a high-importance channel (a banner on the screen)", bool(prompt) and prompt[0]["importance"] == 4, prompt[0]["importance"] if prompt else None)
channel = phone.channel(PROMPT_CHANNEL)
check("and the channel, read from the system, has no sound and no vibration (a Debug build's: the phone is muted, somebody sleeps beside it)",
      channel is not None and channel["sound"] == "null" and channel["vibration_enabled"] is False and channel["vibration"] == "null", channel)
check("tapping the notification in the shade brings the app back at the consent", phone.tap_in_shade("wants to use your AI"))
back_on = phone.wait_for(on_consent, 20)
phone.screenshot("from-notification")
check("it is the same consent, with the site's code", bool(back_on) and shown_code(phone.dump()) == check_code(background["session"]))
check("the notification is gone once the app is in front", wait(lambda: not prompt_notification(), 10))
phone.tap(button(phone.dump(), "Decline"))
phone.wait_for(on_home, 15)

note("5b. a question waiting while the app is killed: its notification is not left to lead nowhere")
lost = site.ask(cmd="link")
phone.open_link(lost["link"])
phone.wait_for(on_consent, 40)
phone.home_key()
check("the question is announced", wait(lambda: bool(prompt_notification()), 15))
phone.shell(f"am kill {PACKAGE}")                     # what Android does to a cached app: not a force-stop
time.sleep(2)
check("the app's process is gone", phone.shell(f"pidof {PACKAGE}").strip() == "")
check("the notification outlived it", bool(prompt_notification()))
check("the visitor taps it in the shade: the wallet starts again", phone.tap_in_shade("wants to use your AI"))
stale = phone.wait_text(contains="That question was from before the wallet restarted", seconds=30)
phone.screenshot("stale-question")
check("the home screen says, in one line, that the question was from before the wallet restarted", stale is not None and on_home_screen_text(phone.dump()))
check("the dead process's notification was taken down by the new one", wait(lambda: not prompt_notification(), 15))
check("no question is waiting", not phone.find(phone.dump(), contains="Asks for"))

# a link pasted into "Connect by link"
pasted = site.ask(cmd="link")
phone.tap_text("Connect by link", cls="Button")
phone.wait_text(text="Paste the link a site gave you", cls="TextBlock")
phone.screenshot("connect-by-link-empty")
field = phone.find(phone.dump(), cls="TextBox")[0]
connect = button(phone.dump(), "Connect")
check("Connect is off while the box is empty", connect is not None and not connect.enabled)
text = pasted["link"]


# A visitor has the link on his clipboard (he copied it from a message or a page) and presses Paste. The phone's keyboard loses keys when a test types
# a link of five hundred characters (it is the keyboard, not the wallet: the same box takes a short text exactly), so a Debug build puts the link
# on the clipboard when told to (DEBUG_COPY: it answers nothing), and the test presses the wallet's own button.
phone.copy_to_clipboard(text)
time.sleep(1.0)
phone.wait_text(text="Paste from the clipboard", cls="Button")
phone.tap_text("Paste from the clipboard", cls="Button")
time.sleep(1.0)
got = phone.find(phone.dump(), cls="TextBox")[0].text
check("Paste from the clipboard puts exactly the link in the box", got == text, f"{len(got)} of {len(text)} characters")
phone.screenshot("connect-by-link-typed")
check("the pasted link is in the box and Connect is on", button(phone.dump(), "Connect").enabled)
phone.tap(button(phone.dump(), "Connect"))
consent = phone.wait_for(on_consent, 30)
nodes = phone.dump()
phone.screenshot("handed-consent")
check("a pasted link goes to the same consent page", bool(consent) and shown_code(nodes) == check_code(pasted["session"]))
check("the page says the link did not come through the browser on this phone", bool(phone.find(nodes, contains="did not come through the browser on this phone")))
state = site.ask(cmd="state")
check("the site still had no contact before Allow", state["upgrades"] == 1, state)
phone.tap(tick(nodes, "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
check("Allow on the pasted link connects the wallet to the site", site.ask(cmd="wait_hello", seconds=40, count=2)["ok"])
phone.wait_for(lambda ns: phone.find(ns, contains="Serving 1 site"), 20)
check("the service runs again", wait(phone.service_running, 15))
stop_at(6)

# ---------------------------------------------------------------------------------------------- 6. Stop
note("6. Stop in the notification")
check("the notification has its Stop action", any("Stop" in n["actions"] for n in serving_notification()), [n["actions"] for n in serving_notification()])
pid_before_stop = phone.shell(f"pidof {PACKAGE}").strip()
check("the visitor taps Stop in the notification, in the shade", phone.tap_in_shade("Agent wallet is serving", action="Stop"))
phone.collapse_shade()
check("Stop ends the connection: the site sees the wallet go", site.ask(cmd="wait_closed", seconds=30)["ok"])
check("the service is gone", wait(lambda: not phone.service_running(), 20))
check("the notification is gone", wait(lambda: not serving_notification(), 15))
time.sleep(12)                                       # Android's own timer for a service that did not call startForeground runs for about ten seconds
check("Stop ends the connections and leaves the app alive (the same process, no crash)", bool(pid_before_stop) and phone.shell(f"pidof {PACKAGE}").strip() == pid_before_stop, (pid_before_stop, phone.shell(f"pidof {PACKAGE}").strip()))
rec = [r for r in settings().get("sites", []) if r["origin"].endswith(f":{site.port}")]
check("Stop paused nothing: the site is still Allowed and not paused, in the wallet's settings", bool(rec) and rec[0]["status"] == "Allowed" and rec[0]["paused"] is False, rec[:1])
phone.start_app()
phone.tap_text("Sites", cls="Button")
phone.wait_text(text="Sites", cls="TextBlock")
nodes = phone.dump()
phone.screenshot("sites-after-stop")
check("after Stop the site is still Allowed (Stop pauses nothing) and is not connected", bool(phone.find(nodes, text="Allowed")) and not phone.find(nodes, text="Paused")
      and not phone.find(nodes, text="connected now"), phone.texts(nodes)[:20])
phone.tap(row_button(nodes, f"localhost:{site.port}", "Settings"))
nodes = phone.dump()
phone.screenshot("site-settings-after-stop")
check("its settings offer Pause, not Resume", bool(button(nodes, "Pause")) and not button(nodes, "Resume"), phone.texts(nodes)[:30])
# "decide on my own" keeps the pairing alive: the wallet may come back to the site on its own. The pairing is written when the site's welcome comes,
# so the visitor turns the switch on, and the site's next link (which always asks) gives the connection that is kept.
phone.tap(tick(nodes, "decide on my own"))
time.sleep(1)
check("the switch is written to the wallet's settings", [r for r in settings().get("sites", []) if r["origin"].endswith(f":{site.port}")][0]["autoDecide"] is True)
go_home()
again = site.ask(cmd="link")
consent = open_consent(again["link"], "paired-consent")
phone.tap(tick(phone.dump(), "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
check("the next link asks again, and Allow connects the site", site.ask(cmd="wait_hello", seconds=60, count=3)["ok"])
check("its pairing is stored on the phone", "pairings.dat" in phone.app_files(HOME_DIR))
phone.stop_app()                                      # the process is gone: the connection with it
check("the site sees the wallet go", site.ask(cmd="wait_closed", seconds=30)["ok"])
phone.start_app()
check("a site with a stored pairing that decides on its own comes back when the app is opened again, without a new link", site.ask(cmd="wait_hello", seconds=60, count=4)["ok"])
check("and the service serves it", wait(phone.service_running, 20))
phone.wait_for(on_home_screen_text, 20)
nodes = phone.wait_for(lambda ns: phone.find(ns, contains="Serving 1 site"), 20)
check("Home says it serves the site, with a Stop serving button", bool(nodes) and bool(button(phone.dump(), "Stop serving")))
phone.tap_text("Stop serving", cls="Button")
check("Stop serving on Home ends the connection too", site.ask(cmd="wait_closed", seconds=30)["ok"])
check("and the service goes", wait(lambda: not phone.service_running(), 20))
rec = [r for r in settings().get("sites", []) if r["origin"].endswith(f":{site.port}")]
check("it paused nothing either", bool(rec) and rec[0]["status"] == "Allowed" and rec[0]["paused"] is False, rec[:1])
open_door("Sites")
phone.wait_text(text="Connect by link", cls="Button")
phone.tap(row_button(phone.dump(), f"localhost:{site.port}", "Settings"))
phone.tap(tick(phone.dump(), "decide on my own"))
time.sleep(1)
check("the switch is off in the wallet's settings", [r for r in settings().get("sites", []) if r["origin"].endswith(f":{site.port}")][0]["autoDecide"] is False)
# and it is honoured: the process is ended and the app opened again, which is what brought the site back with the switch on
hellos_before = site.ask(cmd="state")["hellos"]
phone.stop_app()
phone.start_app()
phone.wait_for(on_home, 30)
time.sleep(15)
after = site.ask(cmd="state")
check("with the switch off the wallet does not come back to the site on its own: no hello, no connection, no service after the app was opened again",
      after["hellos"] == hellos_before and not after["live"] and not phone.service_running(), (hellos_before, after["hellos"], after["live"]))
go_home()

stop_at(7)

# ---------------------------------------------------------------------------------------------- 6c. a slow model
note("6c. a slow model: the service starts at Allow, while the app is on the screen, and keeps the connection while the visitor is away")


def model_delay(seconds):
    """The fake model server answers chat calls this many seconds late (the models of a site getting ready take time)."""
    urllib.request.urlopen(f"http://127.0.0.1:{model_port}/control/delay?seconds={seconds}", timeout=10).read()


model_delay(25)
slow_site = helpers.start_site(manifest_path, os.path.join(work, "slow.log"), os.path.join(work, "slow.stderr.log"))
started.append(slow_site)
phone.run("reverse", f"tcp:{slow_site.port}", f"tcp:{slow_site.port}", check=True)
slow_link = slow_site.ask(cmd="link")
consent = open_consent(slow_link["link"], "slow-consent")
check("the link of a site whose model is slow shows its consent", bool(consent))
phone.tap(tick(phone.dump(), "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
phone.home_key()                       # the visitor does not wait: the models are still getting ready, and he puts the phone down
check("the foreground service started with Allow, while the app was on the screen (the models were still getting ready)", wait(phone.service_running, 15))
connecting = wait(lambda: any("Connecting to" in n["title"] for n in serving_notification()), 15)
check("its notification says it is connecting to the site", connecting, [n["title"] for n in serving_notification()])
check("the site has no hello yet (the model is slow)", slow_site.ask(cmd="state")["hellos"] == 0)
check("the wallet connects when the models are ready, with the app in the background, and the service holds it", slow_site.ask(cmd="wait_hello", seconds=90)["ok"])
check("the notification then says it serves 1 site", wait(lambda: any("serving 1 site" in n["title"] for n in serving_notification()), 20), [n["title"] for n in serving_notification()])
model_delay(0)
slow_site.ask(cmd="drop")
check("when the site goes the service stops", wait(lambda: not phone.service_running(), 30))
go_home()

# ---------------------------------------------------------------------------------------------- 6d. Stop while a link is still being connected
note("6d. Stop while the models of an allowed site are still getting ready: Home shows the site with its Stop, and the site never gets a hello")
SLOW = 120                                                 # the models start warming when the consent comes up, and the steps before Stop take a minute
model_delay(SLOW)
stop_site = helpers.start_site(manifest_path, os.path.join(work, "stopcon.log"), os.path.join(work, "stopcon.stderr.log"))
started.append(stop_site)
phone.run("reverse", f"tcp:{stop_site.port}", f"tcp:{stop_site.port}", check=True)
stop_link = stop_site.ask(cmd="link")
warm_from = time.time()
consent = open_consent(stop_link["link"], "stop-connecting-consent")
check("the link of a site whose model is slow shows its consent", bool(consent))
phone.tap(tick(phone.dump(), "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
check("the service starts with Allow and its notification says it is connecting", wait(lambda: any("Connecting to" in n["title"] for n in serving_notification()), 20), [n["title"] for n in serving_notification()])
go_home()
nodes = phone.wait_for(lambda ns: phone.find(ns, contains="Connecting to localhost:"), 15)
phone.screenshot("home-connecting")
nodes = phone.dump()
check("Home says that the site is being connected, and offers Stop (it does not say that no site is connected)",
      bool(phone.find(nodes, contains=f"Connecting to localhost:{stop_site.port}")) and bool(button(nodes, "Stop serving")) and not phone.find(nodes, text="No site is connected."), phone.texts(nodes)[:10])
phone.home_key()
tapped = phone.tap_in_shade("Connecting to", action="Stop")
check("the visitor taps Stop in the notification that says Connecting to", tapped, getattr(phone, "last_shade", None))
phone.collapse_shade()
check("the service goes", wait(lambda: not phone.service_running(), 20))
check("the notification goes", wait(lambda: not serving_notification(), 15))
time.sleep(5)
check("the service is not started again for the link that was stopped, and no 'open the wallet' notification is posted", not phone.service_running() and not [n for n in phone.app_notifications() if n["id"] == 3])
time.sleep(max(0, warm_from + SLOW + 20 - time.time()))   # the model's late answer has come by now: the preparation that Stop cancelled would have ended
state = stop_site.ask(cmd="state")
check("the site never got a hello (and the wallet never even opened a connection to it)", state["hellos"] == 0 and state["upgrades"] == 0 and not state["live"], state)
go_home()
nodes = phone.dump()
phone.screenshot("home-after-stop-connecting")
check("Home says that no site is connected", bool(phone.find(nodes, text="No site is connected.")) and not phone.find(nodes, contains="Connecting to localhost:"), phone.texts(nodes)[:8])
model_delay(0)

# ---------------------------------------------------------------------------------------------- 7. a paid model: add it from OpenRouter, the paid question, the raise
note("7. a paid model behind an OpenRouter key: Add from OpenRouter, the question before a paid call, the raise of a limit")


# the key again (step 2 deleted it), then the models behind it
open_door("My models")
phone.wait_text(text="Add a model on a server", cls="Button")
phone.type_text(phone.find(phone.dump(), cls="TextBox")[0], KEY)
press("Save the key")
check("the key is saved again", phone.wait_text(contains="A key is saved", seconds=15) is not None)
phone.tap_text("Add a model from OpenRouter", cls="Button")
catalog = phone.wait_text(contains="Paid One", seconds=60)
phone.screenshot("openrouter-catalog")
nodes = phone.dump()
check("the models behind the key come from the wallet's list (the fake OpenRouter lists four): they are in the picker",
      catalog is not None and all(phone.find(nodes, contains=n) for n in ("Paid One", "Cheap Two", "Free Three")), phone.texts(nodes)[:30])
check("each one says what an answer costs, or that it is free", bool(phone.find(nodes, contains="about $")) and bool(phone.find(nodes, contains="free")))
box = phone.find(nodes, cls="TextBox")[0]
phone.type_text(box, "paid")
time.sleep(1)
nodes = phone.dump()
phone.screenshot("openrouter-search")
check("the search narrows the list to the words typed", bool(phone.find(nodes, contains="Paid One")) and not phone.find(nodes, contains="Cheap Two"))
phone.tap(phone.find(nodes, text="Free", cls="Button")[0])
time.sleep(1)
nodes = phone.dump()
check("Free keeps the free ones only (none of them matches 'paid')", not phone.find(nodes, contains="Paid One"), phone.texts(nodes)[:20])
phone.tap(phone.find(nodes, text="All", cls="Button")[0])
time.sleep(1)
pick = next(n for n in phone.find(phone.dump(), cls="CheckBox") if "Paid One" in n.label)
phone.tap(pick)
nodes = phone.dump()
check("a ticked model is counted: 1 model will be added", bool(phone.find(nodes, contains="1 model will be added")), [n.label for n in phone.find(nodes, contains="will be")])
frame = consent_frame(nodes) or (0, 300, 720, 1400)
test = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, text="Test", cls="Button")), None), (0, 300, 720, 1450))
add = button(phone.dump(), "Add")
check("Add waits for a passing test", add is not None and not add.enabled)
phone.tap(test)
passed = phone.wait_text(contains="Test passed", seconds=40)
phone.screenshot("openrouter-tested")
check("Test passes against the fake OpenRouter (one short paid question)", passed is not None, phone.texts()[-6:] if passed is None else passed.label)
phone.tap_text("Add", cls="Button")
added = phone.wait_text(contains="paid-one", seconds=20) or phone.wait_text(contains="Paid One", seconds=5)
phone.screenshot("my-models-with-paid")
check("the paid model is in My models", added is not None, phone.texts()[:30])
requests = [json.loads(line) for line in open(model_requests) if line.strip()]
tested = [r for r in requests if r.get("route") == "openrouter"]
check("the key went to its own provider (the fake OpenRouter got it, as a bearer key) and the call named the model", bool(tested) and tested[-1].get("auth") == "Bearer " + KEY
      and tested[-1].get("model") == "fakeco/paid-one", tested[-1:] )

# only the paid model is left to answer: the local model is turned off (it is a model of the visitor's, and he says "Use it" no)
toggle_model("Fake GPT")
time.sleep(1)
check("a model turned off says so", bool(phone.find(phone.dump(), text="turned off")))

site3 = helpers.start_site(manifest_path, os.path.join(work, "site3.log"), os.path.join(work, "site3.stderr.log"))
started.append(site3)
phone.run("reverse", f"tcp:{site3.port}", f"tcp:{site3.port}", check=True)
paid_link = site3.ask(cmd="link")
consent = open_consent(paid_link["link"], "paid-consent-top")
nodes = phone.dump()
check("a site whose only model is paid gets a consent page that says the model is behind a key and paid", bool(consent) and bool(phone.find(nodes, contains="paid-one")) and bool(phone.find(nodes, contains="API key · paid")), phone.texts(nodes)[:30])
phone.tap(tick(nodes, "The code is the same"))
phone.tap(button(phone.dump(), "Allow"))
check("Allow connects the third site", site3.ask(cmd="wait_hello", seconds=40)["ok"])

import threading


def call_in_background(helper, out):
    out.append(helper.ask(cmd="call_text", choices=["yes", "no"], text=QUESTION, seconds=90))


first = []
t1 = threading.Thread(target=call_in_background, args=(site3, first), daemon=True)
t1.start()
asked_paid = phone.wait_text(text="needs a paid source", seconds=60)
phone.screenshot("paid-question")
nodes = phone.dump()
check("the call waits for the visitor: a question appears, naming the paid model and what it may cost", asked_paid is not None and bool(phone.find(nodes, contains="paid-one"))
      and bool(phone.find(nodes, contains="About $")) and bool(phone.find(nodes, contains="Fake GPT")) and bool(phone.find(nodes, contains="turned off")), phone.texts(nodes)[:12])
check("the question has its three answers", all(button(nodes, t) for t in ("Allow once", "Always for this site", "No")))
check("the site is still waiting: nothing was sent to the paid model yet", len([r for r in [json.loads(l) for l in open(model_requests) if l.strip()] if r.get("route") == "openrouter"]) == len(tested))
phone.tap(button(nodes, "Always for this site"))
t1.join(60)
check("after Always the call is answered by the paid model", bool(first) and first[0].get("ok") is True and (first[0].get("output") or {}).get("text") == "yes", first)
cost = (first[0].get("receipt") or {}).get("cost") if first else None
check("its receipt carries what the fake OpenRouter said it cost", cost is not None and cost.startswith("0.04"), cost)

# the second call does not fit what is left of the site's budget for the session: the wallet asks to raise it
second = []
t2 = threading.Thread(target=call_in_background, args=(site3, second), daemon=True)
t2.start()
raised = phone.wait_text(text="reached a limit", seconds=60)
phone.screenshot("raise-question")
nodes = phone.dump()
check("a call that does not fit the session's budget asks the visitor to raise it", raised is not None and bool(phone.find(nodes, contains="Raise it")), phone.texts(nodes)[:12])
raise_buttons = [b for b in phone.find(nodes, cls="Button") if b.label not in ("No",) and "$" in b.label]
check("it offers raises in numbers, smallest first, and No", bool(raise_buttons) and bool(button(nodes, "No")), [b.label for b in phone.find(nodes, cls="Button")])
phone.tap(raise_buttons[0])
t2.join(60)
check("after the raise the call goes on and is answered", bool(second) and second[0].get("ok") is True and (second[0].get("output") or {}).get("text") == "yes", second)
spent = [e for e in activity_lines() if e.get("kind") == "call" and e.get("outcome") == "ok" and "paid-one" in (e.get("source") or "").lower()]
check("the activity has both paid calls with their cost", len(spent) == 2 and all(float(str(e.get("cost", "0")).split()[0]) > 0 for e in spent), [{k: e.get(k) for k in ("source", "cost")} for e in spent])

# a third call reaches the limit again: this time the visitor wants to set the amount by hand, in Limits
third = []
t3 = threading.Thread(target=call_in_background, args=(site3, third), daemon=True)
t3.start()
raised_again = phone.wait_text(text="reached a limit", seconds=60)
nodes = phone.dump()
phone.screenshot("raise-question-by-hand")
check("a third call reaches the limit again, and the question offers Set it in Limits as well (as the desktop's does)", raised_again is not None and bool(button(nodes, "Set it in Limits…")), [b.label for b in phone.find(nodes, cls="Button")])
if button(nodes, "Set it in Limits…"):
    phone.tap(button(nodes, "Set it in Limits…"))
    reached_line = phone.wait_text(contains="This site reached one of these limits", seconds=15)
    phone.screenshot("limits-from-raise")
    check("it opens Limits at the site that reached its limit, and the page says that it did (this is the one way in that does)", reached_line is not None, phone.texts()[:8])
t3.join(70)
check("the call that waited is not raised: it gets no answer", bool(third) and third[0].get("ok") is False, third)
site3.ask(cmd="drop")
wait(lambda: not phone.service_running(), 30)

# back to how it was: the local model is on again, and the paid one is removed (Remove asks first)
open_door("My models")
phone.wait_text(text="Add a model on a server", cls="Button")
toggle_model("Fake GPT")
time.sleep(1)
nodes = phone.dump()
paid_row = next(n.label for n in phone.find(nodes, cls="CheckBox") if "paid-one" in n.label)
phone.tap(row_button(nodes, paid_row, "Settings"))
phone.screenshot("paid-model-settings")
press("Remove")
check("Remove asks first", bool(button(phone.dump(), "Remove it")) and bool(button(phone.dump(), "Keep it")))
press("Remove it")
time.sleep(1.5)
check("the paid model is gone from My models", not [n for n in phone.find(phone.dump(), cls="CheckBox") if "paid-one" in n.label], phone.texts()[:20])
# the key, which paid for real calls in this step, goes too: the visitor is where he began, and the next step starts without a key
press("Remove the key")
check("Remove the key asks first, after paid calls were made with it", phone.wait_text(text="Delete the saved key?", seconds=10) is not None)
press("Delete")
check("Delete takes the key away", phone.wait_text(contains="Optional. One key", seconds=10) is not None)
stop_at(7)

# ---------------------------------------------------------------------------------------------- 8. a site that asks for several models
note("8. a site that asks for two or three models: the model picker of the consent page")
go_home()
missing = []
for n in range(1, 7):
    open_door("My models")
    phone.wait_text(text="Add a model on a server", cls="Button")
    phone.tap_text("Add a model on a server", cls="Button")
    phone.wait_text(text="Test", cls="Button")
    fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n_: n_.top)
    phone.type_text(fields[0], f"http://127.0.0.1:{model_port}/v1")
    fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n_: n_.top)
    phone.type_text(fields[1], f"fake-m{n}")
    phone.tap_text("Test", cls="Button")
    if not phone.wait_text(contains="Test passed", seconds=40):
        check(f"model fake-m{n} tests ok", False, phone.texts()[-5:])
        break
    phone.tap_text("Add", cls="Button")
    if not wait(lambda: any(f"fake-m{n}" in (x.get("model") or "") for x in settings().get("sources", [])), 20):
        missing.append(n)
    go_home()
listed = [x for x in settings().get("sources", []) if x.get("kind") == "openai-compatible"]
check("seven models are in My models now (the wallet's own settings say so, and each one showed in the list when it was added)", len(listed) == 7 and not missing, (len(listed), missing))

site2 = helpers.start_site(os.path.join(work, "manifest-models.json"), os.path.join(work, "site2.log"), os.path.join(work, "site2.stderr.log"))
started.append(site2)
phone.run("reverse", f"tcp:{site2.port}", f"tcp:{site2.port}", check=True)
many = site2.ask(cmd="link")
consent = open_consent(many["link"], "models-consent-top")
nodes = phone.dump()
check("a site that asks for several models gets a consent page with a model list", bool(consent) and bool(phone.find(nodes, contains="ticked ·")), phone.texts(nodes)[:25])
counts = phone.find(nodes, contains="ticked ·")
check("the count says how many are ticked and how many the site uses", bool(counts) and counts[0].label.endswith("the site uses 2 to 3"), counts[0].label if counts else None)
boxes = [n for n in phone.find(nodes, cls="CheckBox") if "fake-" in n.label or "Fake GPT" in n.label]
check("the short list shows the models of the visitor with their place and price", len(boxes) >= 2, [b.label for b in boxes])
# the code first, so that what Allow says is about the models and not about the code (it is at the top of the page, with the site)
phone.tap(tick(nodes, "The code is the same"))
nodes = phone.dump()
frame_here = consent_frame(nodes) or (0, 300, 720, 1400)
more = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, contains="Show more")), None), frame_here)
check("the rest of the models are behind Show more (the page scrolls: only Decline and Allow stay in place)", more is not None, phone.texts()[:30])
master = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, contains="All models", cls="CheckBox")), None), frame_here)
nodes = phone.dump()
check("the master box 'All of them' is there", master is not None, [n.label for n in phone.find(nodes, cls="CheckBox")][:8])
if master is not None:
    phone.tap(master)
    nodes = phone.dump()
    phone.screenshot("models-master-pressed")
    check("pressing All of them lets go of every tick: 0 ticked, and Allow says what is missing",
          bool(phone.find(nodes, contains="0 ticked")) and bool(phone.find(nodes, contains="Tick the models the site needs")) and not button(nodes, "Allow").enabled,
          phone.texts(nodes)[-8:])
frame = consent_frame(nodes)
for name in ("Fake GPT", "fake-m1"):
    # the list is under the box "All of them", and a person scrolls to a model before he presses it
    box = phone.scroll_into_view(lambda ns, name=name: next((n for n in phone.find(ns, cls="CheckBox") if name in n.label), None), frame)
    if box is None:
        check(f"the short list has {name}", False, [n.label for n in phone.find(nodes, cls="CheckBox")])
        break
    phone.tap(box)
    nodes = phone.dump()
phone.screenshot("models-two-ticked")
check("two ticked models are what the site needs: the count says 2, and Allow is on", bool(phone.find(nodes, contains="2 ticked")) and button(nodes, "Allow").enabled,
      [n.label for n in phone.find(nodes, contains="ticked")])
more = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, contains="Show more")), None), frame)
if more is not None:
    phone.tap(more)
    nodes = phone.dump()
    phone.screenshot("models-show-more")
    last = phone.scroll_into_view(lambda ns: next((n for n in phone.find(ns, cls="CheckBox") if "fake-m6" in n.label), None), frame)
    less = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, contains="Show less")), None), frame)
    check("Show more opens the whole list (down to the last model), and Show less folds it again", last is not None and less is not None)
    if less is not None:
        phone.tap(less)
        time.sleep(1)
        check("Show less folds it", not phone.find(phone.dump(), contains="fake-m6"))
add_models = phone.scroll_into_view(lambda ns: next(iter(phone.find(ns, text="Add models", cls="Button")), None), frame)
check("Add models is there", add_models is not None)
if add_models is not None:
    phone.tap(add_models)
    nodes = phone.dump()
    phone.screenshot("models-add-models")
    check("Add models without an OpenRouter key says where to get one", bool(phone.find(nodes, contains="add your OpenRouter key")) and bool(button(nodes, "Done")))
    phone.back()
    # the consent is the page with Decline and Allow, and without the Add models page's Done (the top of the consent may have scrolled away)
    check("Back closes that page and does not decline the consent",
          bool(phone.wait_for(lambda ns: bool(button(ns, "Allow")) and bool(button(ns, "Decline")) and not button(ns, "Done"), 10)))
nodes = phone.dump()
check("with two models and the code ticked Allow is still on", button(nodes, "Allow").enabled, phone.texts(nodes)[-6:])
phone.tap(button(nodes, "Allow"))
check("Allow connects the second site", site2.ask(cmd="wait_hello", seconds=40)["ok"])
hello2 = site2.ask(cmd="state")["last_hello"] or {}
given = (hello2.get("grant") or {}).get("models") or {}
shown = [m for group in given.values() for m in group]
check("the hello lists the two models the visitor ticked, and no others", len(shown) == 2, json.dumps(given)[:300])
check("the site is told the models by a letter, not by what the visitor called them or by their address",
      all(m.get("name", "").startswith("Model ") and m.get("id") is None for m in shown) and "fake" not in json.dumps(given).lower() and "127.0.0.1" not in json.dumps(given), json.dumps(given)[:300])
phone.wait_for(lambda ns: phone.find(ns, contains="Serving 1 site"), 20)
phone.screenshot("models-site-served")
site2.ask(cmd="drop")
check("when that site goes the service stops", wait(lambda: not phone.service_running(), 30))

# ---------------------------------------------------------------------------------------------- 9. the pages
note("9. My models, Sites, Limits, Activity and Help, as the desktop wallet has them")
def dollars(text):
    """The number of a money setting ("1.00 USD" and "1 USD" are the same amount)."""
    return float(str(text).split()[0])


def site_record(origin_port):
    return [r for r in settings().get("sites", []) if r["origin"].endswith(f":{origin_port}")][0]


# --- My models: the settings of a model
open_door("My models")
phone.wait_text(text="Add a model on a server", cls="Button")
phone.tap(row_button(phone.dump(), "Fake GPT", "Settings"))
found = {t: reach(contains=t) for t in ("In my default set", "Let the model think", "Thinking limit")}
phone.screenshot("model-settings")
check("a model's settings say what it is and let the visitor change it: default set, thinking, thinking limit", all(v is not None for v in found.values()), {k: v is not None for k, v in found.items()})
check("its place in the order of its skill is shown", reach(contains="Text generation order: 1 of 7") is not None)
tap_on(contains="Move down", cls="Button")
check("Down moves it one place (the order is the wallet's own: 2 of 7)", wait(lambda: reach(contains="Text generation order: 2 of 7") is not None, 10))
tap_on(contains="Move up", cls="Button")
check("Up moves it back", wait(lambda: reach(contains="Text generation order: 1 of 7") is not None, 10))
tap_on(contains="Let the model think", cls="CheckBox")
time.sleep(1)
thinking = [x for x in settings().get("sources", []) if "fake-gpt" in (x.get("model") or "")]
check("the thinking switch is written to the settings", bool(thinking) and thinking[0].get("thinking") is not None, thinking[:1])
tap_on(contains="Let the model think", cls="CheckBox")
tap_on(text="Test", cls="Button")
check("Test, in a model's settings, tests that model", wait(lambda: reach(contains="Test passed") is not None, 40))
tap_on(text="Edit", cls="Button")
phone.wait_text(text="Test", cls="Button")
edit_nodes = phone.dump()
phone.screenshot("edit-model")
filled = [n.label for n in phone.find(edit_nodes, cls="TextBox")]
check("Edit opens the model's page with what it has: its address, its model id, its name", any(f"127.0.0.1:{model_port}" in t for t in filled) and "fake-gpt" in filled and "Fake GPT" in filled, filled)
check("it is titled as an edit", bool(phone.find(edit_nodes, text="Edit a model")))
phone.back()
phone.wait_text(text="Add a model on a server", cls="Button")
tap_on(text="Hide", cls="Button")
time.sleep(1)
check("Hide folds the settings again", not phone.find(phone.dump(), contains="Thinking limit"))

# --- Sites
open_door("Sites")
phone.wait_text(text="Connect by link", cls="Button")
nodes = phone.dump()
phone.screenshot("sites-list")
host = f"localhost:{site.port}"
host2 = f"localhost:{site2.port}"
check("Sites lists the sites that asked, each with its answer", bool(phone.find(nodes, text=host)) and bool(phone.find(nodes, text="Allowed")), phone.texts(nodes)[:20])
phone.tap(row_button(nodes, host, "Settings"))
found = {t: reach(contains=t) for t in ("Text generation", "Up to $", "decide on my own")}
found.update({t: reach(text=t, cls="Button") for t in ("Pause", "Revoke", "Block")})
phone.screenshot("site-settings")
check("a site's settings say what it may use and its limits, and offer what can be done with it", all(v is not None for v in found.values()), {k: v is not None for k, v in found.items()})
tap_on(text="Pause", cls="Button")
check("Pause pauses the site: the card says Paused and offers Resume", wait(lambda: reach(text="Resume", cls="Button") is not None, 10))
check("and the wallet's settings say it", site_record(site.port)["paused"] is True)
tap_on(text="Resume", cls="Button")
check("Resume lifts it", wait(lambda: reach(text="Pause", cls="Button") is not None, 10) and site_record(site.port)["paused"] is False)
tap_on(text="Hide", cls="Button")
# block, and unblock, another site (the first one is needed below)
time.sleep(1)
phone.tap(row_button(phone.dump(), host2, "Settings"))
tap_on(text="Block", cls="Button")
blocked = wait(lambda: reach(text="Blocked sites") is not None, 10)
phone.screenshot("sites-blocked")
check("Block puts the site in the list of blocked sites, with Unblock", blocked and reach(text="Unblock", cls="Button") is not None, phone.texts()[:20])
tap_on(text="Unblock", cls="Button")
check("Unblock takes it out of that list", wait(lambda: not phone.find(phone.dump(), text="Blocked sites"), 10))
check("the one setting for every site is at the bottom: pause a site after it goes unused", reach(text="Pause a site after it goes unused for") is not None and reach(text="a week", cls="Button") is not None)
tap_on(text="a week", cls="Button")
time.sleep(1)
check("it is written to the settings", settings().get("limits", {}).get("pauseUnusedAfterHours") == 168, settings().get("limits"))
tap_on(text="a day", cls="Button")

# --- Limits
open_door("Limits")
phone.wait_text(text="Whole wallet, all sites")
phone.screenshot("limits")
before = settings()["limits"]
tap_on(text="Raise: Money per day", cls="Button")
time.sleep(1)
after = settings()["limits"]
check("a plus raises the wallet's money per day by the desktop's step", dollars(after["walletPerDay"]) > dollars(before["walletPerDay"]), (before["walletPerDay"], after["walletPerDay"]))
tap_on(text="Lower: Money per day", cls="Button")
time.sleep(1)
check("a minus lowers it back", dollars(settings()["limits"]["walletPerDay"]) == dollars(before["walletPerDay"]), (before["walletPerDay"], settings()["limits"]["walletPerDay"]))
check("the text limits are there for every site", reach(contains="Text a call may send") is not None)
phone.back()
# the limits of one site, from its own Settings in Sites
open_door("Sites")
phone.wait_text(text="Connect by link", cls="Button")
phone.tap(row_button(phone.dump(), host, "Settings"))
tap_on(text="Limits of this site", cls="Button")
first = phone.wait_text(text="This site: " + host, seconds=10)
phone.screenshot("site-limits")
check("Limits of this site opens the Limits page at that site", first is not None)
check("opened from the site's Settings, the page does not say that the site reached a limit (it did not)", not phone.find(phone.dump(), contains="reached one of these limits"), phone.texts()[:10])
budget_before = site_record(site.port)["budget"]
tap_on(text="Raise: Budget per session", cls="Button")
time.sleep(1)
budget_after = site_record(site.port)["budget"]
check("the site's own budget goes up with a plus", dollars(budget_after) > dollars(budget_before), (budget_before, budget_after))
tap_on(text="Lower: Budget per session", cls="Button")
time.sleep(1)
check("and comes back with a minus", dollars(site_record(site.port)["budget"]) == dollars(budget_before), (budget_before, site_record(site.port)["budget"]))

# --- Activity: the whole log, a page at a time
open_door("Activity")
phone.wait_text(text="Activity", cls="TextBlock")
nodes = phone.wait_for(lambda ns: [n for n in phone.find(ns, cls="TextBlock") if re.search(r"^\d+-\d+ of \d+ · page 1 of \d+$", n.label)], 15)
place = nodes[0].label if nodes else ""
phone.screenshot("activity-paged")
m = re.match(r"^(\d+)-(\d+) of (\d+) · page 1 of (\d+)$", place)
check("the log is paged: the place says which entries are shown, of how many", m is not None, place)
if m and int(m.group(3)) > 50:
    check("with more than 50 entries there are several pages", int(m.group(4)) >= 2, place)
    tap_on(text="Older", cls="Button")
    older = phone.wait_for(lambda ns: [n for n in phone.find(ns, cls="TextBlock") if re.search(r"^51-\d+ of \d+ · page 2 of \d+$", n.label)], 10)
    phone.screenshot("activity-older")
    check("Older shows the next entries (51 on)", bool(older), phone.texts()[:12])
    tap_on(text="Newest", cls="Button")
    check("Newest goes back to the top", bool(phone.wait_for(lambda ns: [n for n in phone.find(ns, cls="TextBlock") if re.search(r"^1-\d+ of \d+ · page 1 of \d+$", n.label)], 10)))
    tap_on(text="200", cls="Button")
    check("200 entries a page shows the whole log on one page", bool(phone.wait_for(lambda ns: [n for n in phone.find(ns, cls="TextBlock") if re.search(r"page 1 of 1$", n.label)], 10)))
    tap_on(text="50", cls="Button")
else:
    check("(the log has no more than 50 entries here: the paging is not exercised)", True, place)
check("the entries have the day above them", reach(text="Today") is not None)
phone.back()

# --- Help
open_door("Help")
phone.wait_text(contains="Short articles for people who are not developers")
nodes = phone.dump()
phone.screenshot("help")
reads = phone.find(nodes, text="Read", cls="Button")
check("Help lists the articles of the wallet, each with Read", len(reads) >= 3, len(reads))
check("the articles of the desktop-only features are named as such, not offered", reach(text="Desktop only") is not None)
phone.screenshot("help-desktop-only")
tap_on(text="Read", cls="Button")
time.sleep(1.5)
nodes = phone.dump()
phone.screenshot("help-article")
check("an article opens on a page of its own, with its text", bool(phone.find(nodes, text="Back")) and len([n for n in phone.find(nodes, cls="TextBlock") if len(n.label) > 80]) >= 1, phone.texts(nodes)[:6])
phone.back()
stop_at(9)

# ---------------------------------------------------------------------------------------------- 10. the address that a site's QR code carries
note("10. the web wallet's address (a site's QR code) with the link in its fragment, and Scan a QR code")
WEB_WALLET = "https://wallet.bringyourownagent.org/"
site4 = helpers.start_site(manifest_path, os.path.join(work, "site4.log"), os.path.join(work, "site4.stderr.log"))
started.append(site4)
phone.run("reverse", f"tcp:{site4.port}", f"tcp:{site4.port}", check=True)
go_home()
WITNESS = ("requests", "manifest_gets", "upgrades", "hellos")


def witness():
    """What the witness site (site4: every address of this step names it) has been asked so far: any request of any kind moves one of these."""
    state = site4.ask(cmd="state")
    return {k: state[k] for k in WITNESS} | {"live": state["live"]}


qr = site4.ask(cmd="link")
quiet = witness()
# A Debug build is signed with another key than the one the web wallet's assetlinks.json names, so Android has not verified the address for it: the
# wallet's activity is named (the Release run, android_qr_e2e.py, lets the system pick). What the activity does with the address is what is tested here.
phone.open_address(WEB_WALLET + "#link=" + qr["link"], explicit=True)
got = phone.wait_for(on_consent, 40)
time.sleep(6)                                              # whatever the wallet would do to the site before Allow, it has done by now
nodes = phone.dump()
up = witness()
phone.screenshot("qr-address-consent")
check("the web wallet's address opens the wallet's consent page", bool(got))
check("the site named in the link received no HTTP or WebSocket request while the consent was up (its counters, read 6 s after the consent came up, did not move)", up == quiet and on_consent(nodes), (quiet, up))
check("the consent says the link came from another device or was pasted (a handed link)", bool(phone.find(nodes, contains="did not come through the browser on this phone")), phone.texts(nodes)[:8])
check("it shows the check code that the site's session gives", shown_code(nodes) == check_code(qr["session"]), (shown_code(nodes), check_code(qr["session"])))
check("Allow waits for the tick that the code is the same", bool(button(nodes, "Allow")) and not button(nodes, "Allow").enabled)
phone.tap(tick(nodes, "The code is the same"))
allow_button = button(phone.dump(), "Allow")
last_look = witness()                                      # the last look before Allow: the first sample was taken several taps and dumps ago
check("and still none right before Allow (the counters read again, after the tick and just before the tap on Allow)", last_look == quiet and allow_button is not None, (quiet, last_look))
phone.tap(allow_button)
check("after the tick, Allow connects the site: it sees the wallet's hello", site4.ask(cmd="wait_hello", seconds=60)["ok"])
hello = (site4.ask(cmd="state").get("last_hello") or {})
check("the hello is the same as for any link: the link's session and the grant the visitor gave", hello.get("session") == qr["session"] and (hello.get("grant") or {}).get("skills") == ["text.generate"], str(hello)[:200])
site4.ask(cmd="drop")
check("the service goes with the site", wait(lambda: not phone.service_running(), 30))
# bad addresses: each one NAMES the witness site (a valid link to it, or its address), so that a request to it would show in its counters
# (what the counters cannot see: a name lookup, or a TCP connect that sends no HTTP; the product has no such path before a grant)
good = site4.ask(cmd="link")["link"]
bad = [
    ("no fragment (the link is in the query)", WEB_WALLET + "?link=" + good, "did not carry a link from a site"),
    ("another fragment (the link is in it)", WEB_WALLET + "#other=" + good, "did not carry a link from a site"),
    ("a fragment that is not a link from a site (it is the witness's address)", WEB_WALLET + f"#link=http://localhost:{site4.port}/byoa/ws", "did not carry a link from a site"),
    ("a link that cannot be used (it names the witness, with no session)", WEB_WALLET + f"#link=bringyourownagent://connect?v=0.1&endpoint=ws%3A%2F%2Flocalhost%3A{site4.port}%2Fbyoa%2Fws", "That link cannot be used"),
]
for what, url, words in bad:
    go_home()
    before = witness()
    phone.open_address(url, explicit=True)
    line = phone.wait_text(contains=words, seconds=15)
    time.sleep(4)
    nodes = phone.dump()
    check(f"{what}: the home screen has one plain line and no consent opened", line is not None and on_home(nodes) and not on_consent(nodes), phone.texts(nodes)[:5])
    check(f"{what}: the site it names received no HTTP or WebSocket request", witness() == before, (before, witness()))
phone.screenshot("qr-address-bad")
refused = [e for e in activity_lines() if e.get("kind") == "link" and e.get("outcome") == "invalid"]
check("a link that was refused leaves its entry in the activity log, as the engine does for links", any("a link was ignored" in (e.get("note") or "") for e in refused), [e.get("note") for e in refused][-3:])
go_home()
scan = button(phone.dump(), "Scan a QR code")
check("the home screen has Scan a QR code", scan is not None)
if scan is not None:
    phone.tap(scan)
    focus = ""
    for _ in range(20):
        focus = phone.focus()
        if PACKAGE not in focus and "systemui" not in focus and "Window{" in focus:
            break
        time.sleep(1.5)
    time.sleep(2)
    phone.screenshot("scanner")
    check("Google's scanner opens over the wallet (a screen of Google Play services)", PACKAGE not in focus and "gms" in focus, focus)
    phone.back()
    check("Back from the scanner returns to the wallet's home screen", bool(phone.wait_for(on_home, 15)), phone.focus())
go_home()
phone.tap_text("Connect by link", cls="Button")
phone.wait_text(text="Connect", cls="Button")
check("Connect by link has Scan a QR code too", bool(button(phone.dump(), "Scan a QR code")))
phone.back()
stop_at(10)

errors = (phone.app_file(HOME_DIR + "/errors.log") or b"").decode("utf-8", errors="replace")
check("the app wrote no error log", errors.strip() == "", errors[:300])
crash = [l for l in (phone.logcat(2000) + phone.crash_log()).splitlines() if ("FATAL EXCEPTION" in l or "AndroidRuntime" in l) and "org.bringyourownagent" in l]
check("logcat has no crash of the app (the whole run: the system's crash buffer was cleared at the start)", not crash, crash[:3])
finish()
