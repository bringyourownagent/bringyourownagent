"""Stage C on the phone, against the RELEASE-signed APK (the one make-android-package.ps1 builds with the owner's release key): the web wallet's address
(`https://wallet.bringyourownagent.org/#link=...`, what a site's QR code carries) opens the wallet, an address with no usable link says so in one plain
line, and "Scan a QR code" opens Google's scanner.

    python android_qr_e2e.py --adb <adb.exe> --serial <phone> --shots <folder> --apk <the release-signed apk>

A Release build has no Debug door, so a link to a site on this computer (ws://localhost) is refused, as it should be. The link here therefore names an
endpoint that is never reached (`wss://qr-test.example/byoa/ws`): the consent comes from the link alone, the wallet makes no request before Allow, and
the visitor declines. What a QR code opens is the system's choice (the wallet, once Android has verified the address for it with the domain's
assetlinks.json); the intent has no package and no component. The camera itself is not driven: no code is shown on any screen here, so what a real code
does is for a person to try. Only the wallet's own package is touched on the phone.
"""
import argparse
import atexit
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse

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
ap.add_argument("--verify-seconds", type=int, default=120, help="how long to wait for Android to verify the address (it fetches assetlinks.json itself)")
a = ap.parse_args()
os.makedirs(a.shots, exist_ok=True)
work = os.path.join(a.shots, "work")
os.makedirs(work, exist_ok=True)
phone = Phone(a.adb, a.serial, a.shots)
checks, failed, started = [], [], []
HOST = "wallet.bringyourownagent.org"
NO_LINK_LINE = "did not carry a link from a site"


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


def on_consent(nodes):
    return bool(phone.find(nodes, contains="Asks for", cls="TextBlock"))


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


def check_code(session):
    """CONTRACT-DIRECT section 3a: four characters of SHA-256("BYOA-CHECK-1:" + session)."""
    alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    digest = hashlib.sha256(("BYOA-CHECK-1:" + session).encode()).digest()
    return "".join(alphabet[b % len(alphabet)] for b in digest[:4])


def wait_consent(seconds=40):
    """The consent page; the system's question about notifications (the first question of a fresh install) is answered Allow when it comes over it."""
    end = time.time() + seconds
    while time.time() < end:
        nodes = phone.dump()
        if on_consent(nodes):
            return nodes
        dialog = [n for n in nodes if "allow_button" in n.rid and ("permissioncontroller" in n.pkg or "packageinstaller" in n.pkg)]
        if dialog:
            phone.tap(dialog[0])
            continue
        time.sleep(1)
    return None


def domain_state():
    text = phone.app_links()
    found = re.search(re.escape(HOST) + r":\s*(\S+)", text)
    return (found.group(1) if found else "none"), text


print("---- the release-signed APK on the phone")
check("the APK is there", os.path.exists(a.apk), a.apk)
phone.wake()
phone.run("uninstall", PACKAGE)                       # the wallet's own package only (a Debug build is signed with another key: it cannot be updated by this one)
r = phone.run("install", "-r", a.apk, timeout=300)
check("the release-signed APK installs", b"Success" in r.stdout, r.stdout.decode(errors="replace").strip()[-80:])
phone.shell("logcat -c")

print("---- Android's verification of the web wallet's address")
state, text = domain_state()
if state != "verified":
    phone.reverify_app_links()
    end = time.time() + a.verify_seconds
    while time.time() < end and state != "verified":
        time.sleep(5)
        state, text = domain_state()
check(f"Android has verified {HOST} for the wallet (its assetlinks.json names this APK's certificate)", state == "verified", state)
open(os.path.join(work, "app-links.txt"), "w", encoding="utf-8").write(text)
# Where Android has not verified the address (the phone cannot reach the domain: no network), the system would send it to the browser. What the wallet does with
# an address is still tested, with its activity named, and the two checks that need the system's own choice stay red: they say what is not proven.
explicit = state != "verified"
if explicit:
    print("     NOTE: the address is not verified on this phone: the wallet's activity is named in the checks below (the system's own choice is not tested)", flush=True)

print("---- the helpers (a fake site, only to make a well-formed link)")
manifest_path = os.path.join(work, "manifest.json")
json.dump({"byoa": "0.1-draft", "purpose": {"summary": "Answer a small yes or no question"}, "skills": {"text.generate": {"required": True}}, "privacy": "personal-voice",
           "connect": {"check": True}}, open(manifest_path, "w"))
site = helpers.start_site(manifest_path, os.path.join(work, "site.log"), os.path.join(work, "site.stderr.log"))
started.append(site)
local = site.ask(cmd="link")["link"]
session = urllib.parse.parse_qs(urllib.parse.urlparse(local).query)["session"][0]
link = re.sub(r"endpoint=[^&]+", "endpoint=" + urllib.parse.quote("wss://qr-test.example/byoa/ws", safe=""), local)
address = f"https://{HOST}/#link={link}"
expected_code = check_code(session)

print("---- a model on a server, so that the consent has something to offer")
model_port = 18234
model_requests = os.path.join(work, "model-requests.jsonl")
open(model_requests, "w").close()
model = helpers.start_model(model_port, model_requests, os.path.join(work, "model.stderr.log"))
started.append(model)
phone.run("reverse", f"tcp:{model_port}", f"tcp:{model_port}", check=True)
phone.start_app()
phone.wait_for(home, 60)
open_door("My models")
phone.tap_text("Add a model on a server", cls="Button")
phone.wait_text(text="Test", cls="Button")
for index, text in enumerate((f"http://127.0.0.1:{model_port}/v1", helpers.MODEL_ID, "Fake GPT")):
    fields = sorted(phone.find(phone.dump(), cls="TextBox"), key=lambda n: n.top)
    phone.type_text(fields[index], text)
phone.tap_text("Test", cls="Button")
check("the model passes its test", phone.wait_text(contains="Test passed", seconds=40) is not None)
phone.tap_text("Add", cls="Button")
check("the model is in My models", phone.wait_text(contains="Fake GPT", seconds=20) is not None)

print("---- the address a site's QR code carries opens the wallet, with the handed consent")
phone.open_address(address, explicit=explicit)
nodes = wait_consent()
check("the wallet's consent comes", nodes is not None, phone.focus())
check("the system itself chose the wallet for the address (no package, no component named)", not explicit and nodes is not None)
if nodes:
    phone.screenshot("qr-consent")
    texts = phone.texts(nodes)
    check("the consent says where the link came from (not through the browser on this phone)", any("did not come through the browser on this phone" in t for t in texts), texts[:8])
    codes = [n.label for n in phone.find(nodes, cls="TextBlock") if re.fullmatch(r"[A-Z2-9]{4}", n.label)]
    check("it shows the check code, the one the link's session gives", codes[:1] == [expected_code], (codes, expected_code))
    check("it names the site by its endpoint", any("qr-test.example" in t for t in texts), texts[:6])
    allow = phone.find(nodes, text="Allow", cls="Button")
    check("Allow waits for the tick that the code is the same", bool(allow) and not allow[0].enabled and bool(phone.find(nodes, contains="Tick that the code is the same")), phone.texts(nodes)[-4:])
    check("the app that opened is the wallet", PACKAGE in phone.focus(), phone.focus())
    decline = phone.find(nodes, text="Decline", cls="Button")
    if decline:
        phone.tap(decline[0])
    gone = phone.wait_for(lambda ns: not on_consent(ns), 15)
    check("Decline closes the consent (back to the page the visitor was on)", bool(gone))
    go_home()
    # (that the wallet makes no request to the link's site before Allow is asserted with a witness in android_e2e.py step 10 and in the core's tests; a
    # Release build refuses ws:// and local hosts, so no witness can be named by a link here: nothing is claimed about contact in this run)
    check("nothing was connected: Home says that no site is connected", bool(phone.find(phone.dump(), text="No site is connected.")))

print("---- an address with no usable link says so in one plain line")
bad = [
    ("no fragment", f"https://{HOST}/"),
    ("another fragment", f"https://{HOST}/#other=1"),
    ("a fragment that is not a link from a site", f"https://{HOST}/#link=https://evil.example/byoa/ws"),
    ("an empty link", f"https://{HOST}/#link="),
    ("a link that cannot be used (its endpoint is not wss://)", f"https://{HOST}/#link=bringyourownagent://connect?v=0.1&endpoint=ws%3A%2F%2Fevil.example%2Fbyoa%2Fws"),
]
for index, (what, url) in enumerate(bad, 1):
    go_home()
    phone.open_address(url, explicit=explicit)
    got = phone.wait_text(contains=NO_LINK_LINE if index < len(bad) else "That link cannot be used", seconds=15)
    nodes = phone.dump()
    phone.screenshot(f"bad-address-{index}")
    check(f"{what}: the home screen has the plain line", got is not None and home(nodes), phone.texts(nodes)[:6])
    check(f"{what}: no consent opened", not on_consent(nodes))

print("---- Scan a QR code")
go_home()
nodes = phone.dump()
scan = phone.find(nodes, text="Scan a QR code", cls="Button")
check("the home screen has Scan a QR code", bool(scan))
if scan:
    phone.screenshot("home-with-scan")
    phone.tap(scan[0])
    end = time.time() + 30
    focus = ""
    while time.time() < end:
        focus = phone.focus()
        if PACKAGE not in focus and "systemui" not in focus and "Window{" in focus:
            break
        if phone.find(phone.dump(), contains="The scanner could not"):
            break
        time.sleep(1.5)
    time.sleep(2)
    phone.screenshot("scanner")
    check("Google's scanner opens over the wallet (a screen of Google Play services, not of the wallet)", PACKAGE not in focus and "gms" in focus, focus)
    phone.back()
    got = phone.wait_for(home, 15)
    check("Back from the scanner returns to the wallet's home screen", bool(got), phone.focus())
    nodes = phone.dump()
    check("closing the scanner leaves no line behind and connects nothing", not phone.find(nodes, contains="The scanner could not") and bool(phone.find(nodes, text="No site is connected.")))
go_home()
phone.tap_text("Connect by link", cls="Button")
phone.wait_text(text="Connect", cls="Button")
check("Connect by link has Scan a QR code too", bool(phone.find(phone.dump(), text="Scan a QR code", cls="Button")))
phone.screenshot("connect-by-link-with-scan")
phone.back()

print("---- no crash")
crash = [l for l in (phone.logcat(2000) + phone.crash_log()).splitlines() if ("FATAL EXCEPTION" in l or "AndroidRuntime" in l) and PACKAGE in l]
check("no crash of the app in the whole run", not crash, crash[:3])
stop_helpers()
with open(os.path.join(a.shots, "results.json"), "w", encoding="utf-8") as f:
    json.dump({"checks": checks, "failed": failed}, f, indent=1)
print(f"\n{len(checks) - len(failed)} of {len(checks)} checks passed" + (": FAILED " + "; ".join(failed) if failed else ""), flush=True)
sys.exit(1 if failed else 0)
