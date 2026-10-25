"""The consent window's model pick and the "Let the model think" switch, on a hidden desktop (nothing appears on the screen somebody works at).

A fake site asks for exactly one text model (count 1..1); the wallet (its own data folder, two local models served by a fake OpenAI-compatible
runtime on a loopback port) shows them as round buttons: picking one lets go of the other, and nothing is greyed. After Allow, the site's
`params.thinking: false` and the visitor's own switch in Sources (Let the model think before it answers) must each reach the runtime as
`chat_template_kwargs.enable_thinking: false`. Buttons are pressed through UI Automation only.

    <python with uiautomation, pillow, aiohttp> consent_radio_thinking.py --exe <Byoa.Wallet.exe> --home <empty wallet data folder> [--shots <dir>]
"""
import argparse
import asyncio
import json
import os
import re
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hidden_desktop as hd  # noqa: E402  (before anything that starts COM)

ap = argparse.ArgumentParser()
ap.add_argument("--exe", required=True)
ap.add_argument("--home", required=True)
ap.add_argument("--shots", default=None)
ap.add_argument("--port", type=int, default=5888, help="loopback port of the fake runtime (never 9931: that is a real Llama)")
ap.add_argument("--desktop", default="byoa-consent-radio")
a = ap.parse_args()
assert a.port != 9931
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)

from aiohttp import web  # noqa: E402

failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


# ------------------------------------------------------------------ a fake OpenAI-compatible runtime with two models
bodies = []


def run_runtime():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)

    async def models(_):
        return web.json_response({"object": "list", "data": [{"id": "alpha", "object": "model"}, {"id": "beta", "object": "model"}]})

    async def chat(request):
        body = await request.json()
        bodies.append(body)
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": "yes"}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 5, "completion_tokens": 1}})

    app = web.Application()
    app.router.add_get("/v1/models", models)
    app.router.add_post("/v1/chat/completions", chat)
    runner = web.AppRunner(app, access_log=None)
    loop.run_until_complete(runner.setup())
    loop.run_until_complete(web.TCPSite(runner, "127.0.0.1", a.port).start())
    loop.run_forever()


threading.Thread(target=run_runtime, daemon=True).start()
time.sleep(0.8)

# ------------------------------------------------------------------ the wallet's own folder: the two models, in a route
endpoint = f"http://127.0.0.1:{a.port}/v1"


def source(model):
    return {"id": f"openai-compatible:{model}@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": model, "endpoint": endpoint,
            "model": model, "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False,
            "addedByAgent": False, "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback"}


sources = [source("alpha"), source("beta")]
settings_path = os.path.join(a.home, "settings.json")
with open(settings_path, "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": sources,
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": s["id"], "enabled": True, "askBeforePaid": True} for s in sources]}]}, f)

# ------------------------------------------------------------------ the site: one text model, count 1..1
from fake_site import FakeSite  # noqa: E402

manifest = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "Answer a small yes or no question", "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 20}},
    "skills": {"text.generate": {"required": True, "models": {"count": {"min": 1, "max": 1}, "why": "One model is enough for a short question."}}},
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
}
site = FakeSite("radio", manifest).start()
link = site.new_link()
env = dict(os.environ, BYOA_WALLET_HOME=a.home, BYOA_WALLET_DEV_LOCAL="1")

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe, "--url", link], env=env)
ui.ONLY_PIDS = {wallet.pid}
print("wallet started", wallet.pid, flush=True)


def shot(window, name):
    if a.shots:
        try:
            desk.shot(window, os.path.join(a.shots, name + ".png"))
        except OSError as e:
            print("no picture:", e)


def model_buttons(window):
    """[(automation id, control type, selected)] of the model buttons of the consent window (round buttons or boxes)."""
    found = []

    def walk(c, depth):
        try:
            for ch in c.GetChildren():
                if ch.AutomationId.startswith("ConsentModel-") and ch.ControlTypeName in ("RadioButtonControl", "CheckBoxControl"):
                    state = None
                    try:
                        state = bool(ch.GetPattern(auto.PatternId.SelectionItemPattern).IsSelected)
                    except Exception:  # noqa: BLE001
                        pass
                    if state is None:
                        toggle = ch.GetPattern(auto.PatternId.TogglePattern)
                        state = bool(toggle and toggle.ToggleState == 1)
                    found.append((ch.AutomationId, ch.ControlTypeName, state, ch.IsEnabled))
                if depth < 40:
                    walk(ch, depth + 1)
        except Exception:  # noqa: BLE001
            pass

    walk(window, 0)
    return found


try:
    notice = ui.find_window("Agent wallet notice", 40)
    check("the notice for the link shows", notice is not None)
    ui.click(notice, "NoticeShow")
    consent = ui.find_window("Agent wallet consent", 20)
    check("the consent window shows", consent is not None)
    time.sleep(1.5)
    shot(consent, "1-consent")

    ui.invoke(consent, "ConsentModelsChange")                              # the list shows only the ticked model until "Change models…" opens all of yours
    time.sleep(1.0)
    rows = [r for r in model_buttons(consent) if r[0].startswith("ConsentModel-openai-compatible")]
    print("   models:", [(r[0][-24:], r[1], r[2], r[3]) for r in rows], flush=True)
    check("the two models are round buttons (the site uses one model)", len(rows) == 2 and all(r[1] == "RadioButtonControl" for r in rows), str(rows))
    check("exactly one is picked at first, and neither is greyed", sum(1 for r in rows if r[2]) == 1 and all(r[3] for r in rows))
    other = next(r for r in rows if not r[2])
    ui.select(consent, other[0])
    time.sleep(1.0)
    rows = [r for r in model_buttons(consent) if r[0].startswith("ConsentModel-openai-compatible")]
    check("picking the other lets go of the first at once", len(rows) == 2 and sum(1 for r in rows if r[2]) == 1 and next(r for r in rows if r[2])[0] == other[0], str(rows))
    count = ui.control(consent, "ConsentModelsCount").Name
    check("the count says one is picked", count.startswith("1 "), count)
    shot(consent, "2-picked")
    check("Allow is offered", ui.control(consent, "ConsentAllow").IsEnabled)
    picked = other[0][len("ConsentModel-"):]
    ui.invoke(consent, "ConsentAllow")
    check("the site is connected", site.wait_hello(30))

    # a page that asks for no thinking
    r = site.call_text()
    check("a call is answered", r.get("ok") is True, str(r)[:160])
    check("a call without a word about thinking leaves the model to think", "chat_template_kwargs" not in bodies[-1], str(bodies[-1].keys()))
    r = site.call_text(params={"thinking": False})
    check("params.thinking: false reaches the runtime as enable_thinking: false", bodies[-1].get("chat_template_kwargs") == {"enable_thinking": False}, str(bodies[-1].get("chat_template_kwargs")))
    r = site.call_text(params={"thinking": True})
    check("params.thinking: true changes nothing", "chat_template_kwargs" not in bodies[-1])

    # the visitor's own switch, in Sources
    subprocess_started = desk.start([a.exe], env=env)                      # a second start hands over to the running wallet: its settings
    settings = ui.find_window("Agent wallet", 30)
    check("the settings window opens", settings is not None)
    ui.select(settings, "Tab-sources")
    time.sleep(1.5)
    switch_id = "AddedSourceThinking-" + re.sub(r"[^A-Za-z0-9]", "-", picked)
    box = ui.control(settings, switch_id, 10)
    shot(settings, "3-sources")
    check("each text model has «Let the model think before it answers», ticked at first", auto.CheckBoxControl(AutomationId=switch_id).Exists(1, 0.2)
          and box.GetPattern(auto.PatternId.TogglePattern).ToggleState == 1)
    ui.toggle(settings, switch_id, on=False)
    time.sleep(1.0)
    saved = json.load(open(settings_path, encoding="utf-8"))
    mine = [s for s in saved["sources"] if s["id"] == picked]
    check("unticking it is saved in the settings", len(mine) == 1 and mine[0].get("thinking") is False, str(mine[0].get("thinking") if mine else None))
    r = site.call_text()
    check("the model answers at once now, though the page said nothing", bodies[-1].get("chat_template_kwargs") == {"enable_thinking": False}, str(bodies[-1].get("chat_template_kwargs")))
    r = site.call_text(params={"thinking": True})
    check("... and a page that wants thinking does not override the visitor", bodies[-1].get("chat_template_kwargs") == {"enable_thinking": False})
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
