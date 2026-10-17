"""End to end: a real Chromium with the unpacked extension, the registered native host, the installed desktop wallet,
real speech clips, and the wallet's windows driven through UI Automation (and one pass with real mouse clicks).

    python e2e.py --samples <dir from make_samples.py> --shots <screenshot dir> --work <empty work dir>
                  [--key-file %USERPROFILE%\\.openrouter-key] [--app %LOCALAPPDATA%\\BYOA\\wallet-app]

Before: run install.ps1 (it registers the host for Chrome and Chromium), and start the Llama app with a speech model
(Qwen3-ASR) on 127.0.0.1:9931 (--llama / --model name another program or model). The wallet holds no model: the test
seeds its own data folder with that program as the speech source. The test uses its own wallet data folder
(BYOA_WALLET_HOME=<work>\\home), so the visitor's real wallet settings are not touched. Several local ports act as
different sites (each origin is a different site to the wallet).

Checks: window.byoa in the page; toast -> consent -> Allow (real clicks), the grant's shape; Allow resolves only
when the models are loaded ("Preparing your model..." meanwhile), measured from the click to the page's grant;
transcripts in Russian and Slovak with latency per source; the first call after a grant; a source that is turned off is skipped;
the paid fallback asks first (Allow once / No), with real money kept to a few hundredths of a cent; a no-click
request gives null and a quiet badge; decline (and Esc) gives null; block gives null and no notice next time;
"decide on my own" gives a silent grant without a click; the rate limit, an over-long clip and a tiny budget give
ok:false; release ends the grant; a grant is bound to its origin; asking for more shows only the difference.
Needs: patchright (Chromium), uiautomation, pillow. Windows only.
"""
import argparse
import datetime
import glob
import json
import os
import re
import subprocess
import sys
import threading
import time
import wave
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

from patchright.sync_api import sync_playwright

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walletui as ui  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
WALLET = os.path.abspath(os.path.join(HERE, "..", ".."))
EXTENSION = os.path.join(WALLET, "extension")

ap = argparse.ArgumentParser()
ap.add_argument("--samples", required=True)
ap.add_argument("--shots", required=True)
ap.add_argument("--work", required=True)
ap.add_argument("--key-file", default=os.path.join(os.path.expanduser("~"), ".openrouter-key"))
ap.add_argument("--app", default=os.path.join(os.environ["LOCALAPPDATA"], "BYOA", "wallet-app"))
ap.add_argument("--headed", action="store_true")
ap.add_argument("--no-paid", action="store_true")
ap.add_argument("--llama", default="http://127.0.0.1:9931/v1", help="the OpenAI-compatible program that runs the speech model")
ap.add_argument("--model", default="Renesas/Qwen3-ASR-1.7B-GGUF:FP16", help="the speech model's id in that program")
a = ap.parse_args()

os.makedirs(a.shots, exist_ok=True)
os.makedirs(a.work, exist_ok=True)
HOME = os.path.join(a.work, "home")
GEN = os.path.join(a.work, "gen")
os.makedirs(GEN, exist_ok=True)
results = {"checks": [], "calls": [], "first_calls": {}}
failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    results["checks"].append({"check": label, "ok": bool(ok), "extra": str(extra)})
    if not ok:
        failed.append(label)


def norm(s):
    s = (s or "").lower().replace("\u0451", "\u0435")  # yo -> ye
    return re.sub(r"[^\w\s]", "", s).strip()


def wer(ref, hyp):
    r, h = norm(ref).split(), norm(hyp).split()
    d = [[0] * (len(h) + 1) for _ in range(len(r) + 1)]
    for i in range(len(r) + 1):
        d[i][0] = i
    for j in range(len(h) + 1):
        d[0][j] = j
    for i in range(1, len(r) + 1):
        for j in range(1, len(h) + 1):
            d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (r[i - 1] != h[j - 1]))
    return d[len(r)][len(h)] / max(1, len(r))


# ---------------------------------------------------------------------------------------------- local "sites"
class Handler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def translate_path(self, path):
        path = path.split("?", 1)[0]
        if path.startswith("/samples/"):
            return os.path.join(a.samples, os.path.basename(path))
        if path.startswith("/gen/"):
            return os.path.join(GEN, os.path.basename(path))
        return os.path.join(HERE, "page", "index.html")


servers = {}
for site in ["main", "auto", "decline", "block", "noclick", "rate", "budget"]:
    srv = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    servers[site] = "http://127.0.0.1:%d" % srv.server_address[1]

samples = json.load(open(os.path.join(a.samples, "samples.json"), encoding="utf-8"))
words = {lang: [s for s in samples if s["lang"] == lang and s["kind"] == "word"] for lang in ("ru", "sk")}
sentences = [s for s in samples if s["kind"] == "sentence"]

# A clip longer than the 15 s the manifest allows.
with wave.open(os.path.join(a.samples, sentences[0]["file"])) as w0:
    params, frames = w0.getparams(), w0.readframes(w0.getnframes())
with wave.open(os.path.join(GEN, "long.wav"), "wb") as w1:
    w1.setparams(params)
    while w1.getnframes() < 16.5 * params.framerate:
        w1.writeframes(frames)

# The wallet holds no model: its settings name the program that runs one, as the Add a model window would write them.
SPEECH_ID = "openai-compatible:" + a.model + "@" + a.llama.rstrip("/").lower() + "#transcribe"


def llama_offers_the_model():
    import urllib.request
    try:
        with urllib.request.urlopen(a.llama.rstrip("/") + "/models", timeout=5) as r:
            return a.model in [m.get("id") for m in json.load(r).get("data", [])]
    except Exception:
        return False


if not llama_offers_the_model():
    sys.exit("start the Llama app with the speech model first: " + a.llama + " " + a.model)
os.makedirs(HOME, exist_ok=True)
with open(os.path.join(HOME, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": [{"id": SPEECH_ID, "enabled": True, "kind": "openai-compatible", "label": "Qwen3-ASR",
                                          "endpoint": a.llama, "model": a.model, "skills": ["audio.transcribe"],
                                          "endpointConfirmed": True}]}, f)

# The test wallet gets the OpenRouter key from a file (stored with DPAPI; never printed).
env = dict(os.environ, BYOA_WALLET_HOME=HOME)
wallet_exe = os.path.join(a.app, "Byoa.Wallet.exe")
key_ok = False
if os.path.exists(a.key_file) and not a.no_paid:
    key_ok = subprocess.run([wallet_exe, "--import-key", "openrouter", a.key_file], env=env).returncode == 0
check("OpenRouter key imported into the test wallet's DPAPI store", key_ok or a.no_paid)


def wallet_pids():
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-Process -Name Byoa.Wallet -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }"],
                         capture_output=True, text=True).stdout
    return {int(x) for x in out.split()}


before = wallet_pids()
ui.EXCLUDE_PIDS = frozenset(before)                      # the visitor's own wallet may be running too: never touch its windows


def activity():
    entries = []
    for f in sorted(glob.glob(os.path.join(HOME, "activity-*.jsonl"))):
        for line in open(f, encoding="utf-8"):
            if line.strip():
                entries.append(json.loads(line))
    return entries


def last_call():
    calls = [e for e in activity() if e.get("kind") == "call"]
    return calls[-1] if calls else {}


def ev(page, expression, arg=None):
    """Evaluates in the page's own (main) world, where window.byoa lives; patchright defaults to an isolated one."""
    return page.evaluate(expression, arg, isolated_context=False)


# ---------------------------------------------------------------------------------------------- the browser
with sync_playwright() as p:
    ctx = p.chromium.launch_persistent_context(
        os.path.join(a.work, "profile"),
        channel="chromium",
        headless=not a.headed,
        env=env,
        viewport={"width": 1100, "height": 760},
        args=[
            f"--disable-extensions-except={EXTENSION}",
            f"--load-extension={EXTENSION}",
            "--disable-features=LocalNetworkAccessChecks",
        ],
    )
    sw = ctx.service_workers[0] if ctx.service_workers else ctx.wait_for_event("serviceworker", timeout=15000)
    ext_id = sw.url.split("/")[2]
    check("extension loaded with the fixed ID", ext_id == "niobopdoiopjilmlonjcgllpmgjemhpk", ext_id)

    def open_site(name):
        pg = ctx.new_page()
        pg.goto(servers[name])
        pg.wait_for_function("window.byoa !== undefined", timeout=10000)
        return pg

    def wait_grant(pg, timeout=140000):
        pg.wait_for_function("window.grant !== undefined", timeout=timeout)
        return ev(pg, "window.grant")

    def press_connect(pg):
        ev(pg, "window.grant = undefined")
        pg.click("#connect")

    def popup_status():
        pp = ctx.new_page()
        pp.goto(f"chrome-extension://{ext_id}/popup.html")
        end = time.time() + 10
        while pp.inner_text("#chip") in ("Checking…", "Opening…") and time.time() < end:
            time.sleep(0.2)
        text = pp.inner_text("body")
        return pp, text

    # ------------------------------------------------------------------ the page API
    main = open_site("main")
    check("window.byoa is in the page, version 0.1", ev(main, "window.byoa && window.byoa.version") == "0.1")
    check("window.byoa cannot be replaced by the page", ev(main, "(() => { try { window.byoa = 1; } catch (e) {} return typeof window.byoa; })()") == "object")
    pp, text = popup_status()
    check("popup before the wallet starts says it is not running", "not running" in text, text.split("\n")[1] if "\n" in text else text)
    pp.close()

    # ------------------------------------------------------------------ the real UI pass: real mouse clicks
    press_connect(main)
    toast = ui.find_window("Agent wallet notice", 40)
    check("after a click the notice appears above the tray", toast is not None)
    time.sleep(0.9)
    ui.shot(toast, os.path.join(a.shots, "01-toast.png"))
    ui.click(toast, "NoticeShow")                 # real input
    consent = ui.find_window("Agent wallet consent", 10)
    check("Show opens the short consent window", consent is not None)
    time.sleep(0.9)
    ui.shot(consent, os.path.join(a.shots, "02-consent.png"))
    ui.invoke(consent, "ConsentWhy-audio.transcribe")
    time.sleep(0.6)
    ui.shot(consent, os.path.join(a.shots, "03-consent-why.png"))
    has_focus = consent.HasKeyboardFocus or ui.auto.GetFocusedControl().GetTopLevelControl().Name == "Agent wallet consent"
    check("the consent window has the keyboard focus", has_focus)
    allowed_at = time.time() * 1000
    ui.click(consent, "ConsentAllow")              # real input
    # A fresh wallet has no model loaded yet: the grant waits for them, with "Preparing your model..." meanwhile.
    preparing = ui.find_window("Agent wallet preparing", 5)
    if preparing is not None:
        time.sleep(0.4)
        try:
            ui.shot(preparing, os.path.join(a.shots, "15-preparing.png"))
        except Exception:  # a program that has its model loaded already answers at once: the toast may be gone by now
            pass
    grant = wait_grant(main)
    check("Allow gives a grant {grantId, skills, expiresAt}",
          isinstance(grant, dict) and isinstance(grant.get("grantId"), str) and grant.get("skills") == ["audio.transcribe"] and "expiresAt" in grant, grant)
    granted_at = time.time()
    allow_to_grant = round((ev(main, "window.grantAt") - allowed_at) / 1000, 2)
    allowed_entry = [e for e in activity() if e.get("kind") == "request" and e.get("outcome") == "allowed"][-1:]
    results["allow_to_grant_seconds"] = {"ru+sk": allow_to_grant}
    note = (allowed_entry[0].get("note") or "") if allowed_entry else ""
    ready_after = re.search(r"models ready after ([0-9.]+) s", note)
    quick = ready_after is not None and float(ready_after.group(1)) < 0.5      # the toast only shows after 150 ms of waiting
    check("while the models load, the toast says \"Preparing your model…\" (or the program had its model loaded already)",
          (preparing is not None and ui.window_gone("Agent wallet preparing", 35)) or quick, note)
    check("Allow resolves once the routed models are ready (at most ~30 s)", 0 < allow_to_grant <= 32 and "models ready after" in (allowed_entry[0].get("note") or "" if allowed_entry else ""),
          f"{allow_to_grant} s from the click; {allowed_entry[0].get('note') if allowed_entry else ''}")

    # ------------------------------------------------------------------ transcripts, first-call latency
    def run_clip(pg, item, grant_id=None):
        r = ev(pg, "([f, l, g]) => window.transcribe(f, l, g)", [item["file"], item["lang"], grant_id])
        entry = last_call()
        text = (r["result"].get("output") or {}).get("text", "") if r["result"].get("ok") else None
        rec = {"file": item["file"], "lang": item["lang"], "kind": item["kind"], "expect": item["expect"], "got": text,
               "page_ms": r["ms"], "source": entry.get("source"), "source_ms": entry.get("milliseconds"),
               "fallback": entry.get("fallback"), "receipt": r["result"].get("receipt")}
        if item["kind"] == "word":
            rec["exact"] = text is not None and norm(text) == norm(item["expect"])
        else:
            rec["wer"] = round(wer(item["expect"], text or ""), 3)
        results["calls"].append(rec)
        return rec

    # A call that waits for the visitor: started in the page, finished after the wallet's window is answered
    # (the browser API is driven from this one thread only).
    def start_clip(pg, file, lang):
        ev(pg, "([f, l]) => { window.pendingClip = window.transcribe(f, l); }", [file, lang])

    def finish_clip(pg):
        return ev(pg, "window.pendingClip")

    # The first calls right after the grant: the wallet warms the models of the site's languages when it grants.
    first_ru = run_clip(main, words["ru"][0])
    results["first_calls"]["ru"] = {"seconds_after_grant": round(time.time() - granted_at, 2), "page_ms": first_ru["page_ms"], "source": first_ru["source"]}
    first_sk = run_clip(main, words["sk"][0])
    results["first_calls"]["sk"] = {"seconds_after_grant": round(time.time() - granted_at, 2), "page_ms": first_sk["page_ms"], "source": first_sk["source"]}
    print("first calls:", json.dumps(results["first_calls"], ensure_ascii=False))
    check("the first calls after Allow are already warm (no cold load in them)", first_ru["page_ms"] < 1000 and first_sk["page_ms"] < 2500,
          f"ru {first_ru['page_ms']} ms by {first_ru['source']}, sk {first_sk['page_ms']} ms by {first_sk['source']}")
    for item in words["ru"][1:] + words["sk"][1:] + sentences:
        run_clip(main, item)
    for lang in ("ru", "sk"):
        recs = [c for c in results["calls"] if c["lang"] == lang and c["kind"] == "word" and c["got"] is not None]
        exact = sum(1 for c in recs if c["exact"])
        check(f"{lang} single words transcribed (answers for every clip)", len(recs) == len(words[lang]), f"{exact}/{len(words[lang])} exact")
    sent = [c for c in results["calls"] if c["kind"] == "sentence"]
    if sent:
        mean_wer = sum(c["wer"] for c in sent) / len(sent)
        check("ru sentences transcribed", all(c["got"] for c in sent), f"mean WER {mean_wer:.2f} over {len(sent)}")
    ru_sources = {c["source"] for c in results["calls"] if c["lang"] == "ru"}
    sk_sources = {c["source"] for c in results["calls"] if c["lang"] == "sk"}
    check("Russian and Slovak are answered by the speech model in the program", all(s and s.startswith("Qwen3-ASR") for s in ru_sources | sk_sources), f"{ru_sources} / {sk_sources}")
    check("receipts say local and cost 0.000 USD", all(c["receipt"] == {"cost": "0.000 USD", "provider": "local"} for c in results["calls"]))
    pp, text = popup_status()
    check("popup says the wallet is connected", "Wallet connected" in text)
    pp.screenshot(path=os.path.join(a.shots, "12-popup.png"), clip={"x": 0, "y": 0, "width": 340, "height": 200})

    # ------------------------------------------------------------------ the settings window, a fallback
    pp.click("#open")
    settings = ui.find_window("Agent wallet", 15)
    check("the popup's link opens the wallet", settings is not None)
    pp.close()
    ui.select(settings, "Tab-sources")
    time.sleep(1)
    def toggle_speech(on):
        """The page redraws itself when a switch changes, so a press may meet a control that is gone: find it again."""
        for _ in range(6):
            try:
                ui.toggle(settings, "AddedSourceEnabled-" + SPEECH_ID, on)
                return
            except Exception:
                time.sleep(0.8)
        raise RuntimeError("the Use switch of the speech model could not be pressed")

    check("the speech model is listed among the added models, with its Use switch", settings.CheckBoxControl(AutomationId="AddedSourceEnabled-" + SPEECH_ID, searchDepth=60).Exists(3, 0.2))
    ui.shot(settings, os.path.join(a.shots, "10-sources.png"))
    toggle_speech(False)
    time.sleep(1)
    off = run_clip(main, words["ru"][0])
    check("with the speech model turned off, the call is not answered by it (no paid fallback in this run)",
          off["got"] is None or not (off["source"] or "").startswith("Qwen3-ASR"), f"{off['fallback']} -> {off['got']}")
    toggle_speech(True)
    time.sleep(1)
    again = run_clip(main, words["ru"][0])
    check("turned on again, the speech model answers again", again["got"] is not None and (again["source"] or "").startswith("Qwen3-ASR"), f"{again['source']} -> {again['got']}")
    toggle_speech(False)
    time.sleep(1)

    # ------------------------------------------------------------------ the paid fallback asks first
    if key_ok:
        box = {}
        start_clip(main, words["ru"][1]["file"], "ru")
        paid = ui.find_window("Agent wallet paid fallback", 20)
        check("a paid fallback asks first", paid is not None)
        time.sleep(0.8)
        ui.shot(paid, os.path.join(a.shots, "04-paid-toast.png"))
        ui.invoke(paid, "PaidOnce")
        box["r"] = finish_clip(main)
        r = box.get("r", {}).get("result", {})
        entry = last_call()
        check("Allow once: answered with the API key, cost on the receipt", r.get("ok") is True and r.get("receipt", {}).get("provider") == "api-key", f"{r.get('receipt')} {entry.get('source')} {entry.get('milliseconds')} ms")
        results["calls"].append({"file": words["ru"][1]["file"], "lang": "ru", "kind": "word", "expect": words["ru"][1]["expect"],
                                 "got": (r.get("output") or {}).get("text"), "page_ms": box.get("r", {}).get("ms"), "source": entry.get("source"),
                                 "source_ms": entry.get("milliseconds"), "receipt": r.get("receipt"), "fallback": entry.get("fallback")})
        box.clear()
        start_clip(main, words["sk"][1]["file"], "sk")
        paid = ui.find_window("Agent wallet paid fallback", 20)
        ui.invoke(paid, "PaidNo")
        box["r"] = finish_clip(main)
        check("No: the call gets ok:false", box.get("r", {}).get("result") == {"ok": False})
        box.clear()
        start_clip(main, words["sk"][2]["file"], "sk")
        paid = ui.find_window("Agent wallet paid fallback", 20)
        ui.invoke(paid, "PaidOnce")
        box["r"] = finish_clip(main)
        r = box.get("r", {}).get("result", {})
        entry = last_call()
        results["calls"].append({"file": words["sk"][2]["file"], "lang": "sk", "kind": "word", "expect": words["sk"][2]["expect"],
                                 "got": (r.get("output") or {}).get("text"), "page_ms": box.get("r", {}).get("ms"), "source": entry.get("source"),
                                 "source_ms": entry.get("milliseconds"), "receipt": r.get("receipt"), "fallback": entry.get("fallback")})
        check("Slovak through the paid source", r.get("ok") is True, (r.get("output") or {}).get("text"))

        # A tiny budget: the gate stops the paid call before it runs (no money spent).
        bud = open_site("budget")
        ev(bud, "window.MANIFEST.expectedCost = { perSession: '0.00001 USD' }")
        press_connect(bud)
        w = ui.find_window("Agent wallet notice", 20)
        ui.invoke(w, "NoticeShow")
        c = ui.find_window("Agent wallet consent", 10)
        ui.invoke(c, "ConsentAllow")
        wait_grant(bud)
        r = ev(bud, "([f, l]) => window.transcribe(f, l)", [words["ru"][2]["file"], "ru"])
        check("over the budget per session: ok:false, nothing paid", r["result"] == {"ok": False}, last_call().get("note"))
        info = ui.find_window("Agent wallet message", 5)
        if info:
            ui.shot(info, os.path.join(a.shots, "05-limit-toast.png"))
            ui.invoke(info, "NoticeClose")
    toggle_speech(True)
    time.sleep(1)

    # ------------------------------------------------------------------ no click: null, no notice, a quiet badge
    # Patchright's evaluate() runs with a user gesture, so the page makes this request on its own from a timer,
    # 6.5 s later, when no input has reached it; the page records navigator.userActivation.isActive at that moment.
    nc = open_site("noclick")
    ev(nc, "window.grant = undefined; window.requestLater(6500)")
    notice_seen = ui.find_window("Agent wallet notice", 9) is not None
    nc.wait_for_function("window.grant !== undefined", timeout=20000)
    check("no user activation at the moment of the no-click request", ev(nc, "window.activeAtRequest") is False)
    check("a request without a click resolves null", ev(nc, "window.grant") is None, f"{ev(nc, 'window.requestMs')} ms")
    check("... and shows no notice", not notice_seen)
    pp, text = popup_status()
    check("... and leaves a quiet badge (held back)", "held back" in text and [e for e in activity() if e.get("outcome") == "held"], text.replace("\n", " | "))
    pp.close()
    # The badge on the tray icon: its tooltip changes with it. The icon may sit in the taskbar or in the
    # "Show hidden icons" flyout; the flyout is opened for the screenshot and closed again.
    badge_name = "a site asked without a click"            # Windows may prefix the tooltip with a space
    taskbar = ui.auto.PaneControl(ClassName="Shell_TrayWnd", searchDepth=1)
    tray = taskbar.ButtonControl(SubName=badge_name, searchDepth=12)
    chevron = None
    if not tray.Exists(1, 0.2):
        chevron = taskbar.ButtonControl(ClassName="SystemTray.NormalButton", AutomationId="SystemTrayIcon", searchDepth=12)
        if chevron.Exists(1, 0.2):
            chevron.GetPattern(ui.auto.PatternId.InvokePattern).Invoke()
            time.sleep(1.2)
            tray = ui.auto.PaneControl(ClassName="TopLevelWindowForOverflowXamlIsland", searchDepth=1).ButtonControl(SubName=badge_name, searchDepth=6)
    found = tray.Exists(2, 0.2)
    if found:
        r0 = tray.BoundingRectangle
        ui.shot_rect((r0.left - 70, r0.top - 30, r0.right + 70, r0.bottom + 30), os.path.join(a.shots, "11-tray-badge.png"))
    check("the tray icon shows the badge (its tooltip says a site asked without a click)", found)
    if chevron is not None:
        ui.auto.SendKeys("{Esc}", waitTime=0.3)

    # ------------------------------------------------------------------ decline, Esc, block
    dec = open_site("decline")
    press_connect(dec)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeDecline")
    check("Decline in the notice: null", wait_grant(dec, 15000) is None)
    press_connect(dec)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeShow")
    c = ui.find_window("Agent wallet consent", 10)
    c.SetFocus()
    time.sleep(0.3)
    ui.auto.SendKeys("{Esc}", waitTime=0.2)
    check("Esc in the consent window declines: null", wait_grant(dec, 15000) is None)

    blk = open_site("block")
    press_connect(blk)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeBlock")
    check("Block this site: null", wait_grant(blk, 15000) is None)
    press_connect(blk)
    t0 = time.time()
    g = wait_grant(blk, 15000)
    check("a blocked site: null at once, and no notice", g is None and ui.find_window("Agent wallet notice", 1.5) is None, f"{time.time() - t0:.2f} s")

    # ------------------------------------------------------------------ decide on my own
    au = open_site("auto")
    press_connect(au)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeShow")
    c = ui.find_window("Agent wallet consent", 10)
    ui.toggle(c, "ConsentAuto", True)
    ui.invoke(c, "ConsentAllow")
    g1 = wait_grant(au)
    au.reload()
    au.wait_for_function("window.byoa !== undefined")
    ev(au, "window.grant = undefined; window.requestLater(6500)")
    notice_seen = ui.find_window("Agent wallet notice", 9) is not None
    au.wait_for_function("window.grant !== undefined", timeout=20000)
    g2 = ev(au, "window.grant")
    check("no user activation at the moment of the silent request", ev(au, "window.activeAtRequest") is False)
    check("decide on my own: a grant without a click and without a notice", isinstance(g2, dict) and g2.get("grantId") and g2["grantId"] != g1["grantId"]
          and not notice_seen, f"{ev(au, 'window.requestMs')} ms")
    r = ev(au, "([f, l, g]) => window.transcribe(f, l, g)", [words["ru"][3]["file"], "ru", g2["grantId"]])
    check("... and it works", r["result"].get("ok") is True)

    # ------------------------------------------------------------------ limits: rate and clip length
    rt = open_site("rate")
    ev(rt, "window.MANIFEST.purpose.limits.maxCallsPerMinute = 3")
    press_connect(rt)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeShow")
    c = ui.find_window("Agent wallet consent", 10)
    ui.invoke(c, "ConsentAllow")
    wait_grant(rt)
    oks = [ev(rt, "([f, l]) => window.transcribe(f, l)", [words["ru"][i]["file"], "ru"])["result"].get("ok") for i in range(4)]
    check("rate limit (3 a minute): the 4th call gets ok:false", oks == [True, True, True, False], oks)
    info = ui.find_window("Agent wallet message", 3)
    if info:
        ui.invoke(info, "NoticeClose")
    r = ev(main, "([f, l]) => window.transcribe(f, l)", ["/gen/long.wav", "ru"])
    check("a clip over 15 s gets ok:false (measured by the wallet)", r["result"] == {"ok": False}, last_call().get("note"))

    # ------------------------------------------------------------------ origin binding and release
    r = ev(dec, "([f, l, g]) => window.transcribe(f, l, g)", [words["ru"][0]["file"], "ru", grant["grantId"]])
    check("another site cannot use this site's grant", r["result"] == {"ok": False})

    # ------------------------------------------------------------------ asking for more shows only the difference
    ev(main, "window.MANIFEST.expectedCost = { perSession: '0.10 USD' }")
    press_connect(main)
    w = ui.find_window("Agent wallet notice", 20)
    ui.invoke(w, "NoticeShow")
    c = ui.find_window("Agent wallet consent", 10)
    time.sleep(0.8)
    diff = c.TextControl(Name="Only what is new", searchDepth=30).Exists(2, 0.2)
    ui.shot(c, os.path.join(a.shots, "13-consent-difference.png"))
    check("asking for more shows only the difference", diff)
    ui.invoke(c, "ConsentAllow")
    g3 = wait_grant(main)
    check("... and Allow grants it", isinstance(g3, dict))

    ev(main, "(g) => window.byoa.release(g)", g3["grantId"])
    r = ev(main, "([f, l, g]) => window.transcribe(f, l, g)", [words["ru"][0]["file"], "ru", g3["grantId"]])
    check("after release the grant no longer works", r["result"] == {"ok": False})

    # ------------------------------------------------------------------ settings screenshots
    settings = ui.find_window("Agent wallet", 10)
    settings.SetActive()
    for i, tab in enumerate(["sites", "sources", "skills", "limits", "activity"]):
        ui.select(settings, "Tab-" + tab)
        time.sleep(1.2)
        ui.shot(settings, os.path.join(a.shots, f"{6 + i:02d}-settings-{tab}.png"))
    ctx.close()

# ---------------------------------------------------------------------------------------------- summary
mine = wallet_pids() - before
results["wallet_pids_started_by_test"] = sorted(mine)
for pid in sorted(mine):                                   # only the wallet this test started, by its PID
    subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
paid_cost = sum(e.get("cost", "0 USD").split()[0] != "0" and float(e["cost"].split()[0]) or 0 for e in activity() if e.get("kind") == "call")
results["paid_cost_usd"] = paid_cost
by_source = {}
for c in results["calls"]:
    if c.get("source_ms") is not None and c.get("got") is not None:
        by_source.setdefault((c["source"] or "?").split(" · ")[0], []).append(c["source_ms"])
results["latency_ms_by_source"] = {k: {"n": len(v), "median": sorted(v)[len(v) // 2], "max": max(v)} for k, v in by_source.items()}
json.dump(results, open(os.path.join(a.work, "e2e-results.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("latency by source (ms, inside the wallet):", json.dumps(results["latency_ms_by_source"], ensure_ascii=False))
print(f"paid: ${paid_cost:.6f}")
print("wallet processes started by this test (stopped):", sorted(mine))
print(f"{len(results['checks']) - len(failed)}/{len(results['checks'])} checks passed" + (": FAILED " + "; ".join(failed) if failed else ""))
sys.exit(1 if failed else 0)
