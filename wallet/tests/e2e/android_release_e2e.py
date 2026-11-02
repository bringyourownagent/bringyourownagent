"""The short run against the RELEASE APK of the Android wallet (the one make-android-package.ps1 builds), on a phone connected with adb.

A Release build has none of the Debug-only doors that android_e2e.py uses (links to a site on the device itself, the clipboard intent, the intent that
makes the activity again, the fake OpenRouter, and reading the app's own folder with run-as), so the long run cannot be made against it. What this run
does is what a visitor can do with the app alone: install it, open every page, add a model on a server (the fake model server on this computer, reached
through adb reverse) and test it, and see that a link to a site on the device itself is refused (the secure behaviour: only a Debug build takes it).

    python android_release_e2e.py --adb <adb.exe> --serial <phone> --shots <folder> --apk <the release apk>

Only the wallet's own package is touched on the phone. Run it with a Python that has aiohttp (the fake model server and site need it). The helpers are
hidden processes, stopped by their own process ids when the run ends.
"""
import argparse
import atexit
import glob
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import android_helpers as helpers  # noqa: E402
from android_ui import PACKAGE, Phone  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--adb", required=True)
ap.add_argument("--serial", required=True)
ap.add_argument("--shots", required=True)
ap.add_argument("--apk", required=True)
a = ap.parse_args()
os.makedirs(a.shots, exist_ok=True)
work = os.path.join(a.shots, "work")
os.makedirs(work, exist_ok=True)
phone = Phone(a.adb, a.serial, a.shots)
checks, failed, started = [], [], []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    checks.append({"check": label, "ok": bool(ok), "extra": str(extra)})
    if not ok:
        failed.append(label)


def stop_helpers():
    while started:
        try:
            started.pop().stop()
        except Exception:  # noqa: BLE001
            pass


atexit.register(stop_helpers)


def home(nodes):
    return bool(phone.find(nodes, contains="Your own AI, for the sites you allow"))


def go_home():
    phone.start_app()
    for _ in range(5):
        if phone.wait_for(home, 4):
            return
        phone.back()
    phone.wait_for(home, 15)


def open_door(name):
    go_home()
    for _ in range(3):
        found = phone.find(phone.dump(), text=name, cls="Button")
        if found and found[0].center[1] < phone.screen_size()[1] - 120:
            phone.tap(found[0])
            return
        phone.swipe_up()
    raise LookupError("no door " + name)


print("---- the Release APK on the phone")
check("the APK is there", os.path.exists(a.apk), a.apk)
cert = subprocess.run([a.adb, "-s", a.serial, "shell", "echo", "ok"], capture_output=True, text=True, creationflags=helpers.CREATE_NO_WINDOW).stdout.strip()
check("the phone is there", cert == "ok")
phone.wake()
phone.run("uninstall", PACKAGE)                     # the wallet's own package only
r = phone.run("install", "-r", a.apk, timeout=300)
check("the Release APK installs", b"Success" in r.stdout, r.stdout.decode(errors="replace").strip()[-80:])
flags = phone.shell(f"dumpsys package {PACKAGE} | grep -a -i -m8 'flags='")
check("the system reports the package's pkgFlags (an empty answer would pass a weaker check), and none of them is DEBUGGABLE", "pkgFlags=[" in flags and "DEBUGGABLE" not in flags, flags.strip()[:200])


def aapt2_path():
    """aapt2 of the newest build-tools of the Android SDK (the same SDK the packaging script uses)."""
    for sdk in (os.environ.get("ANDROID_HOME"), os.environ.get("ANDROID_SDK_ROOT"), r"C:\Program Files (x86)\Android\android-sdk", os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk")):
        if sdk and os.path.isdir(os.path.join(sdk, "build-tools")):
            found = sorted(glob.glob(os.path.join(sdk, "build-tools", "*", "aapt2.exe")))
            if found:
                return found[-1]
    return None


aapt2 = aapt2_path()
badging = subprocess.run([aapt2, "dump", "badging", a.apk], capture_output=True, text=True, creationflags=helpers.CREATE_NO_WINDOW).stdout if aapt2 else ""
check("the APK's own manifest says that it is not debuggable (aapt2 read it: the package line is there, 'application-debuggable' is not)", "package: name='" + PACKAGE + "'" in badging and "application-debuggable" not in badging, "aapt2 not found" if not aapt2 else "")
refused = phone.run("exec-out", "run-as", PACKAGE, "ls", "files").stdout.decode(errors="replace") + phone.run("exec-out", "run-as", PACKAGE, "ls", "files").stderr.decode(errors="replace")
check("the phone refuses run-as on it (the app's own folder is not open to the shell)", "not debuggable" in refused.lower() or "unknown package" in refused.lower() or "is not" in refused.lower(), refused.strip()[:120])
phone.shell("logcat -c")

print("---- the helpers")
model_port = 18234
model_requests = os.path.join(work, "model-requests.jsonl")
open(model_requests, "w").close()
model = helpers.start_model(model_port, model_requests, os.path.join(work, "model.stderr.log"))
started.append(model)
manifest_path = os.path.join(work, "manifest.json")
json.dump({"byoa": "0.1-draft", "purpose": {"summary": "Answer a small yes or no question"}, "skills": {"text.generate": {"required": True}}, "privacy": "personal-voice",
           "connect": {"check": True}}, open(manifest_path, "w"))
site = helpers.start_site(manifest_path, os.path.join(work, "site.log"), os.path.join(work, "site.stderr.log"))
started.append(site)
phone.run("reverse", f"tcp:{site.port}", f"tcp:{site.port}", check=True)
phone.run("reverse", f"tcp:{model_port}", f"tcp:{model_port}", check=True)

print("---- the app starts and its pages open")
phone.start_app()
check("the home screen is there", bool(phone.wait_for(home, 60)))
phone.screenshot("release-home")
nodes = phone.dump()
check("it says that no site is connected and where keys are kept", bool(phone.find(nodes, text="No site is connected.")) and bool(phone.find(nodes, contains="Android Keystore")))
for door, marker in (("My models", "Add a model on a server"), ("Sites", "Pause a site after it goes unused for"), ("Limits", "Whole wallet, all sites"), ("Activity", "Activity"), ("Help", None)):
    open_door(door)
    time.sleep(1.5)
    nodes = phone.dump()
    phone.screenshot("release-" + door.lower().replace(" ", "-"))
    ok = bool(phone.find(nodes, contains=marker)) if marker else bool(phone.find(nodes, contains="Short articles for people who are not developers"))
    check(f"{door} opens in a Release build", ok, phone.texts(nodes)[:6])

print("---- a model on a server, added and tested")
open_door("My models")
phone.tap_text("Add a model on a server", cls="Button")
phone.wait_text(text="Test", cls="Button")
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
phone.type_text(fields[0], f"http://127.0.0.1:{model_port}/v1")
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
phone.type_text(fields[1], helpers.MODEL_ID)
fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
phone.type_text(fields[2], "Fake GPT")
phone.tap_text("Test", cls="Button")
check("Test passes against the fake model server (the network works in a Release build)", phone.wait_text(contains="Test passed", seconds=40) is not None)
phone.tap_text("Add", cls="Button")
check("the model is in My models", phone.wait_text(contains="Fake GPT", seconds=20) is not None)
phone.screenshot("release-model-added")
asked = [json.loads(line) for line in open(model_requests) if line.strip()]
check("the fake model server got the wallet's test question", bool(asked))

print("---- a link to a site on the device itself is refused")
link = site.ask(cmd="link")["link"]
phone.open_link(link)
time.sleep(8)
nodes = phone.dump()
check("no consent comes (only a Debug build takes a ws://localhost site)", not phone.find(nodes, contains="Asks for") and site.ask(cmd="state")["upgrades"] == 0, phone.texts(nodes)[:6])
open_door("Activity")
time.sleep(1.5)
phone.screenshot("release-link-refused")
check("the activity says the link was ignored", bool(phone.find(phone.dump(), contains="link was ignored")) or bool(phone.find(phone.dump(), contains="Could not connect")), phone.texts()[:12])

print("---- no crash")
crash = [l for l in (phone.logcat(2000) + phone.crash_log()).splitlines() if ("FATAL EXCEPTION" in l or "AndroidRuntime" in l) and PACKAGE in l]
check("no crash of the app in the whole run (the system's crash buffer was cleared at the start and is read in full)", not crash, crash[:3])
stop_helpers()
with open(os.path.join(a.shots, "results.json"), "w", encoding="utf-8") as f:
    json.dump({"checks": checks, "failed": failed}, f, indent=1)
print(f"\n{len(checks) - len(failed)} of {len(checks)} checks passed" + (": FAILED " + "; ".join(failed) if failed else ""), flush=True)
sys.exit(1 if failed else 0)
