"""End to end for the direct transport (no extension): bringyourownagent:// links opened the way Windows opens them
(cmd /c start), the installed wallet, its real windows driven through UI Automation, real speech clips, and small fake
sites (fake_site.py) on ws://localhost under the developer switch.

    python direct_e2e.py --samples <dir from make_samples.py> --shots <screenshot dir> --work <empty work dir>
                         [--app %LOCALAPPDATA%\\BYOA\\wallet-app] [--no-browser]

Before: run install.ps1 (it registers the scheme), and start the Llama app with a speech model (Qwen3-ASR) on
127.0.0.1:9931 (--llama / --model name another program or model). The wallet holds no model: the test seeds its own
data folder with that program as the speech source, and every call goes to it. The test uses its own wallet data
folder (BYOA_WALLET_HOME) and BYOA_WALLET_DEV_LOCAL=1, both inherited by the wallet that the link starts.

Checks: the scheme is registered; a link (with its manifest inside, CONTRACT-DIRECT.md §7) starts the wallet and no
request of any kind reaches the site before Allow; the notice and consent (real clicks); hello after Allow, with the
models loaded first, and the site's welcome echoing the manifest; calls with real Russian and Slovak speech over the
connection (answered by the program's speech model); activity marked "via link"; a restart of the wallet reconnects with the stored pairing and no notice; a
site that asks for more on a reconnect gets bye, and a new link shows only the difference; a site whose welcome does
not echo its link's manifest gets bye and nothing of the link is stored or revoked; decline and "Block in Sites…" from a
link: the site never hears from the wallet, and nothing is stored (activity: declined), so a later link asks again; a
link to a paired site that decides on its own still asks first (notice, consent), and a bad session then gets an error
and no retry; Revoke in the settings closes the
connection; once, a real click in a headed Chromium (its own prompt, then the wallet, which gets the link from the
second process over the pipe).
Needs: aiohttp, uiautomation, pillow, patchright (for the browser step). Windows only.
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import walletui as ui  # noqa: E402
from fake_site import FakeSite  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--samples", required=True)
ap.add_argument("--shots", required=True)
ap.add_argument("--work", required=True)
ap.add_argument("--app", default=os.path.join(os.environ["LOCALAPPDATA"], "BYOA", "wallet-app"))
ap.add_argument("--no-browser", action="store_true")
ap.add_argument("--llama", default="http://127.0.0.1:9931/v1", help="the OpenAI-compatible program that runs the speech model")
ap.add_argument("--model", default="Renesas/Qwen3-ASR-1.7B-GGUF:FP16", help="the speech model's id in that program")
a = ap.parse_args()

os.makedirs(a.shots, exist_ok=True)
os.makedirs(a.work, exist_ok=True)
HOME = os.path.join(a.work, "home")
env = dict(os.environ, BYOA_WALLET_HOME=HOME, BYOA_WALLET_DEV_LOCAL="1")
wallet_exe = os.path.join(a.app, "Byoa.Wallet.exe")
results = {"checks": [], "calls": []}
failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    results["checks"].append({"check": label, "ok": bool(ok), "extra": str(extra)})
    if not ok:
        failed.append(label)


def seed_speech_source():
    """The wallet holds no model: its settings name the program that runs one, as the Add a model window would write them."""
    os.makedirs(HOME, exist_ok=True)
    source = {"id": "seeded", "enabled": True, "kind": "openai-compatible", "label": "Qwen3-ASR", "endpoint": a.llama, "model": a.model,
              "skills": ["audio.transcribe"], "endpointConfirmed": True}
    with open(os.path.join(HOME, "settings.json"), "w", encoding="utf-8") as f:
        json.dump({"version": 1, "sources": [source]}, f)


def llama_offers_the_model():
    import urllib.request
    try:
        with urllib.request.urlopen(a.llama.rstrip("/") + "/models", timeout=5) as r:
            return a.model in [m.get("id") for m in json.load(r).get("data", [])]
    except Exception:
        return False


def wallet_pids():
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-Process -Name Byoa.Wallet -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }"],
                         capture_output=True, text=True).stdout
    return {int(x) for x in out.split()}


def activity():
    entries = []
    for f in sorted(glob.glob(os.path.join(HOME, "activity-*.jsonl"))):
        for line in open(f, encoding="utf-8"):
            if line.strip():
                entries.append(json.loads(line))
    return entries


def stored_allowed(origin):
    """True when the wallet's settings.json keeps the origin as allowed (the status is written by name, in any case)."""
    try:
        with open(os.path.join(HOME, "settings.json"), encoding="utf-8") as f:
            sites = json.load(f).get("sites", [])
    except (OSError, ValueError):
        return False
    return any(s.get("origin") == origin and str(s.get("status", "")).lower() == "allowed" for s in sites)


def open_link(link):
    """As Windows opens a link from any program: the shell runs the registered command (cmd's start)."""
    subprocess.run('cmd /c start "" "' + link + '"', env=env)


def wait(pred, seconds, step=0.1):
    end = time.time() + seconds
    while time.time() < end:
        if pred():
            return True
        time.sleep(step)
    return pred()


def kill_mine():
    for pid in sorted(wallet_pids() - before):
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
        started_pids.add(pid)


samples = json.load(open(os.path.join(a.samples, "samples.json"), encoding="utf-8"))
words = {lang: [s for s in samples if s["lang"] == lang and s["kind"] == "word"] for lang in ("ru", "sk")}
sentences = [s for s in samples if s["kind"] == "sentence"]
before = wallet_pids()
ui.EXCLUDE_PIDS = frozenset(before)                      # the visitor's own wallet may be running too: never touch its windows
started_pids = set()
main, dec, blk, web_site, liar = (FakeSite(n).start() for n in ("main", "decline", "block", "browser", "liar"))

# ---------------------------------------------------------------------------------------------- the registration
reg = subprocess.run(["reg", "query", r"HKCU\Software\Classes\bringyourownagent\shell\open\command", "/ve"], capture_output=True, text=True).stdout
proto = subprocess.run(["reg", "query", r"HKCU\Software\Classes\bringyourownagent", "/v", "URL Protocol"], capture_output=True, text=True).returncode == 0
check("bringyourownagent:// is registered for this user: URL Protocol, Byoa.Wallet.exe --url \"%1\"", proto and "Byoa.Wallet.exe" in reg and '--url "%1"' in reg)
check("no wallet with this data folder is running before the test", not os.path.exists(os.path.join(HOME, "settings.json")))
check("the speech program is up and offers the model", llama_offers_the_model(), a.llama + " " + a.model)
if not llama_offers_the_model():
    sys.exit("start the Llama app with the speech model first")
seed_speech_source()

# ---------------------------------------------------------------------------------------------- pair: link, notice, consent, hello
t0 = time.time()
open_link(main.new_link())
notice = ui.find_window("Agent wallet notice", 40)
check("a link starts the wallet, which shows the same notice", notice is not None, f"{time.time() - t0:.1f} s from the link")
check("the notice is up and the site has had no request at all (the manifest came in the link)", main.requests == 0, f"{main.requests} requests")
check("the wallet that the link started uses this test's data folder (it inherited the environment)",
      wait(lambda: os.path.exists(os.path.join(HOME, "settings.json")), 5))
time.sleep(0.8)
ui.shot(notice, os.path.join(a.shots, "20-link-notice.png"))
ui.click(notice, "NoticeShow")
consent = ui.find_window("Agent wallet consent", 10)
time.sleep(0.8)
ui.shot(consent, os.path.join(a.shots, "21-link-consent.png"))
check("nothing reaches the site before Allow", main.requests == 0 and main.upgrades == 0)
ui.toggle(consent, "ConsentAuto", True)
allowed_at = time.time()
ui.click(consent, "ConsentAllow")
prep = ui.find_window("Agent wallet preparing", 4)
if prep is not None:
    try:
        ui.shot(prep, os.path.join(a.shots, "22-link-preparing.png"))
    except Exception:  # it may have closed already
        pass
ok = main.wait_hello(45)
hello_after = time.time() - allowed_at
hello = main.hellos[-1] if main.hellos else {}
check("after Allow the wallet connects and says hello with the session code",
      ok and hello.get("session") and hello.get("pairing") is None and hello.get("grant", {}).get("skills") == ["audio.transcribe"]
      and hello.get("wallet", {}).get("name") == "Agent wallet", f"{hello_after:.2f} s after Allow")
allowed = [e for e in activity() if e.get("kind") == "request" and e.get("outcome") == "allowed"]
check("... only once the models were loaded (\"Preparing your model\" meanwhile)",
      allowed and allowed[-1].get("via") == "link" and "models ready after" in (allowed[-1].get("note") or ""), allowed[-1].get("note") if allowed else "")
results["allow_to_hello_seconds"] = round(hello_after, 2)

# ---------------------------------------------------------------------------------------------- calls with real speech
for item in words["ru"][:4] + words["sk"][:4] + sentences[:2]:
    wav = open(os.path.join(a.samples, item["file"]), "rb").read()
    r = main.call(wav, item["lang"])
    entry = [e for e in activity() if e.get("kind") == "call"][-1]
    results["calls"].append({"lang": item["lang"], "kind": item["kind"], "expect": item["expect"], "ok": r.get("ok"),
                             "text": (r.get("output") or {}).get("text"), "site_ms": r["ms"], "source": entry.get("source"),
                             "source_ms": entry.get("milliseconds"), "via": entry.get("via"), "receipt": r.get("receipt")})
ru = [c for c in results["calls"] if c["lang"] == "ru"]
sk = [c for c in results["calls"] if c["lang"] == "sk"]
check("Russian over the connection: answered by the speech model in the program", all(c["ok"] and (c["source"] or "").startswith("Qwen3-ASR") for c in ru),
      "; ".join(f"{c['text']} ({c['site_ms']} ms)" for c in ru))
check("Slovak over the connection: also answered by it (any language)", all(c["ok"] and (c["source"] or "").startswith("Qwen3-ASR") for c in sk),
      "; ".join(f"{c['text']} ({c['site_ms']} ms)" for c in sk))
check("the words came back as spoken (at least 3 of the 4 Russian words)", sum(1 for c in ru if c["kind"] == "word" and c["text"] and c["expect"].lower().replace("\u0451", "\u0435") in c["text"].lower().replace("\u0451", "\u0435")) >= 3,
      "; ".join(f"{c['expect']} -> {c['text']}" for c in ru))
check("receipts say local, 0.000 USD", all(c["receipt"] == {"cost": "0.000 USD", "provider": "local"} for c in results["calls"]))
check("the activity marks the request and the calls \"via link\"", all(c["via"] == "link" for c in results["calls"])
      and any(e.get("kind") == "link" and e.get("outcome") == "connected" for e in activity()))

# ---------------------------------------------------------------------------------------------- restart with the stored pairing
pairings = os.path.join(HOME, "pairings.dat")
token = hello and main.pairings and next(iter(main.pairings))
check("the pairing is stored (\"decide on my own\" was ticked), encrypted", os.path.exists(pairings) and token
      and token.encode() not in open(pairings, "rb").read())
kill_mine()
check("the wallet quit: the site sees the connection end", wait(lambda: main.live is None, 10))
notices_before = ui.find_window("Agent wallet notice", 0.5)
restart_at = time.time()
subprocess.Popen([wallet_exe, "--background"], env=env)
ok = wait(lambda: len(main.hellos) >= 2 and main.live is not None, 45)
hello2 = main.hellos[-1]
check("after a wallet restart it reconnects with the stored pairing: no link, no notice",
      ok and hello2.get("session") is None and hello2.get("pairing") == token and ui.find_window("Agent wallet notice", 0.5) is None,
      f"{time.time() - restart_at:.1f} s from the start of the wallet")
r = main.call(open(os.path.join(a.samples, words["ru"][4]["file"]), "rb").read(), "ru")
check("... and answers calls again", r.get("ok") is True, (r.get("output") or {}).get("text"))

# ---------------------------------------------------------------------------------------------- a reconnect where the site asks for more
byes = main.byes
main.manifest = dict(main.manifest, expectedCost={"perSession": "0.60 USD"})        # above the visitor's $0.05
kill_mine()
wait(lambda: main.live is None, 10)
subprocess.Popen([wallet_exe, "--background"], env=env)
got_bye = wait(lambda: main.byes == byes + 1 and len(main.hellos) >= 3, 45)
time.sleep(3)
asked_more = [e for e in activity() if e.get("kind") == "link" and "asks for more" in (e.get("note") or "")]
check("after a restart the site's welcome asks for more: the wallet says bye and does not stay or retry",
      got_bye and main.live is None and len(main.hellos) == 3 and asked_more and ui.find_window("Agent wallet notice", 0.5) is None,
      asked_more[-1].get("note") if asked_more else "")
open_link(main.new_link())
w = ui.find_window("Agent wallet notice", 20)
ui.invoke(w, "NoticeShow")
c = ui.find_window("Agent wallet consent", 10)
time.sleep(0.8)
only_new = c.TextControl(Name="Only what is new", searchDepth=30).Exists(2, 0.2)
ui.shot(c, os.path.join(a.shots, "26-link-difference.png"))
ui.toggle(c, "ConsentAuto", True)
ui.invoke(c, "ConsentAllow")
check("a new link then shows only the difference, and Allow connects again", only_new and main.wait_hello(45) and main.live is not None)

# ---------------------------------------------------------------------------------------------- a site whose welcome does not echo its link's manifest
open_link(liar.new_link())
liar.echo = liar.manifest64[:-4] + "AAAA"                     # its server says another manifest than the link showed
w = ui.find_window("Agent wallet notice", 20)
ui.invoke(w, "NoticeShow")
c = ui.find_window("Agent wallet consent", 10)
ui.invoke(c, "ConsentAllow")
got_bye = wait(lambda: liar.byes == 1, 45)
time.sleep(1)
lies = [e for e in activity() if e.get("origin") == liar.origin]
check("a welcome whose manifest differs from the link's: bye, and nothing is stored (not allowed) or revoked",
      got_bye and any(e.get("kind") == "link" and e.get("note") == "the site's manifest differs from its link" for e in lies)
      and not any(e.get("outcome") == "revoked" for e in lies) and not stored_allowed(liar.origin), f"{len(lies)} entries")

# ---------------------------------------------------------------------------------------------- decline (the link goes to the running wallet)
open_link(dec.new_link())
w = ui.find_window("Agent wallet notice", 20)
check("a second link reaches the running wallet (handed over the pipe): its notice", w is not None)
ui.invoke(w, "NoticeDecline")
time.sleep(4)
check("decline: the site never hears from the wallet (no request of any kind)", dec.requests == 0)

# ---------------------------------------------------------------------------------------------- "Block in Sites…" on a link's notice
open_link(blk.new_link())
w = ui.find_window("Agent wallet notice", 20)
ui.invoke(w, "NoticeBlock")                                  # a link's notice has no block: this opens Sites, and the answer is "not now"
time.sleep(2)
open_link(blk.new_link())
w2 = ui.find_window("Agent wallet notice", 20)               # nothing was stored: the site is asked again
seen = w2 is not None
if w2 is not None:
    ui.invoke(w2, "NoticeBlock")
time.sleep(1)
not_now = [e for e in activity() if e.get("origin") == blk.origin and e.get("kind") == "request" and e.get("outcome") == "declined"
           and e.get("note") == "declined from a link: nothing stored"]
check("\"Block in Sites…\" on a link's notice: no connection to the site, nothing stored (declined, never blocked), so a later link asks again",
      blk.requests == 0 and seen and len(not_now) == 2
      and not any(e.get("origin") == blk.origin and e.get("outcome") == "blocked" for e in activity()), f"{len(not_now)} entries")

# ---------------------------------------------------------------------------------------------- a bad session
# A link always asks, also for a paired site that decides on its own: the visitor shows and allows it, then the site answers error.
upgrades, errors = main.upgrades, main.errors_sent
open_link(main.new_link(code="a-code-this-site-never-made-0123456789"))
w = ui.find_window("Agent wallet notice", 20)
check("a link to a paired site that decides on its own still shows a notice (no silent grant)", w is not None)
if w is not None:
    ui.invoke(w, "NoticeShow")
    c = ui.find_window("Agent wallet consent", 10)
    if c is not None:
        ui.invoke(c, "ConsentAllow")
got_error = wait(lambda: main.errors_sent == errors + 1, 20)
time.sleep(4)
refused = [e for e in activity() if e.get("kind") == "link" and e.get("outcome") == "error"]
check("a bad session: the site answers error, the wallet closes and does not try again",
      got_error and main.upgrades == upgrades + 1 and refused and "bad-session" in refused[-1].get("note", ""), refused[-1].get("note") if refused else "")
check("... and the paired connection is still there", main.live is not None)

# ---------------------------------------------------------------------------------------------- the settings: connected, via link; Revoke
subprocess.Popen([wallet_exe], env=env)                     # a second start opens the running wallet's window
settings = ui.find_window("Agent wallet", 15)
host = f"localhost:{main.port}"
if settings is not None:
    ui.select(settings, "Tab-sites")
    time.sleep(1.2)
    connected_text = settings.TextControl(SubName="Connected without the extension", searchDepth=40).Exists(2, 0.2)
    ui.shot(settings, os.path.join(a.shots, "23-settings-sites-link.png"))
    ui.select(settings, "Tab-activity")
    time.sleep(1.2)
    via_text = settings.TextControl(SubName="via link", searchDepth=40).Exists(2, 0.2)
    ui.shot(settings, os.path.join(a.shots, "24-settings-activity-link.png"))
    check("Sites says the site is connected without the extension; Activity says \"via link\"", connected_text and via_text)
    ui.select(settings, "Tab-sites")
    time.sleep(1)
    byes = main.byes
    ui.invoke(settings, "SiteRevoke-" + host)
    check("Revoke closes the connection with bye", wait(lambda: main.byes == byes + 1 and main.live is None, 10))
else:
    check("the settings window opens", False)

# ---------------------------------------------------------------------------------------------- once from a real click in Chromium
if not a.no_browser:
    from patchright.sync_api import sync_playwright

    with sync_playwright() as p:
        ctx = p.chromium.launch_persistent_context(os.path.join(a.work, "profile"), channel="chromium", headless=False, env=env,
                                                   viewport={"width": 900, "height": 600})
        page = ctx.new_page()
        page.goto(web_site.origin + "/")
        page.bring_to_front()
        page.click("#go")                                    # a real click (input events): the browser allows the launch
        prompt = ui.find_protocol_prompt(15)
        check("Chromium asks before it opens the link (its own prompt)", prompt is not None, prompt[1].Name if prompt else "")
        if prompt:
            ui.shot(prompt[0], os.path.join(a.shots, "25-chromium-prompt.png"))
            time.sleep(1.0)                                  # Chromium ignores a press right after its dialog appears
            prompt[1].GetPattern(ui.auto.PatternId.InvokePattern).Invoke()
            w = ui.find_window("Agent wallet notice", 20)
            check("... then the wallet's notice (the link came over the pipe from the second process)", w is not None)
            if w is not None:
                ui.invoke(w, "NoticeShow")
                c = ui.find_window("Agent wallet consent", 10)
                ui.invoke(c, "ConsentAllow")
                check("... Allow, and the site gets the wallet's hello", web_site.wait_hello(45))
                r = web_site.call(open(os.path.join(a.samples, words["sk"][4]["file"]), "rb").read(), "sk")
                check("... and a call works", r.get("ok") is True, (r.get("output") or {}).get("text"))
        ctx.close()

# ---------------------------------------------------------------------------------------------- end
kill_mine()
results["wallet_pids_started_by_test"] = sorted(started_pids)
json.dump(results, open(os.path.join(a.work, "direct-results.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("wallet processes started by this test (stopped):", sorted(started_pids))
print(f"{len(results['checks']) - len(failed)}/{len(results['checks'])} checks passed" + (": FAILED " + "; ".join(failed) if failed else ""))
sys.exit(1 if failed else 0)
