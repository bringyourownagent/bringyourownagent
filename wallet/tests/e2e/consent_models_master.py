"""The master box over the consent window's models, on a hidden desktop (nothing appears on the screen somebody works at).

A fake site asks for three to eight text models; the wallet (its own data folder, four local models served by a fake OpenAI-compatible runtime on
a loopback port) pre-ticks the models it can give, which are boxes. The master box ("All of them") shows that all are ticked; one press lets go of
every tick (the visitor does not want the crowd), a second press ticks them again, and some ticked by hand show the master box half
ticked. After Allow the site is given only the models the visitor kept. Buttons are pressed through UI Automation only.

    <python with uiautomation, pillow, aiohttp> consent_models_master.py --exe <Byoa.Wallet.exe> --home <empty wallet data folder> [--shots <dir>]
"""
import argparse
import asyncio
import json
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
ap.add_argument("--shots", default=None)
ap.add_argument("--port", type=int, default=5889, help="loopback port of the fake runtime (never 9931: that is a real Llama)")
ap.add_argument("--desktop", default="byoa-consent-master")
a = ap.parse_args()
assert a.port != 9931
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)

from aiohttp import web  # noqa: E402

failed = []
MODELS = ("alpha", "beta", "gamma", "delta")


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


# ------------------------------------------------------------------ a fake OpenAI-compatible runtime with four models
bodies = []


def run_runtime():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)

    async def models(_):
        return web.json_response({"object": "list", "data": [{"id": m, "object": "model", "status": {"value": "loaded"}} for m in MODELS]})

    async def chat(request):
        bodies.append(await request.json())
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

# ------------------------------------------------------------------ the wallet's own folder: the four models, in a route
endpoint = f"http://127.0.0.1:{a.port}/v1"


def source(model):
    return {"id": f"openai-compatible:{model}@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": model, "endpoint": endpoint,
            "model": model, "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False,
            "addedByAgent": False, "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback"}


sources = [source(m) for m in MODELS]
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": sources,
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": s["id"], "enabled": True, "askBeforePaid": True} for s in sources]}]}, f)

# ------------------------------------------------------------------ the site: one to eight text models
from fake_site import FakeSite  # noqa: E402

manifest = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "Answer a small yes or no question", "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 20}},
    "skills": {"text.generate": {"required": True, "models": {"count": {"min": 3, "max": 8}, "why": "More models give more opinions."}}},
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
}
site = FakeSite("master", manifest).start()
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


def boxes(window):
    """[(automation id, ticked)] of the model boxes of the consent window, in the order they are drawn."""
    found = []

    def walk(c, depth):
        try:
            for ch in c.GetChildren():
                if ch.AutomationId.startswith("ConsentModel-") and ch.ControlTypeName == "CheckBoxControl":
                    found.append((ch.AutomationId, ch.GetPattern(auto.PatternId.TogglePattern).ToggleState == 1))
                if depth < 40:
                    walk(ch, depth + 1)
        except Exception:  # noqa: BLE001
            pass

    walk(window, 0)
    return [b for b in found if b[0].startswith("ConsentModel-openai-compatible")]


def master_state(window):
    """0 none ticked, 1 all, 2 some (the master box is three-state)."""
    return ui.control(window, "ConsentModelsAll", 5).GetPattern(auto.PatternId.TogglePattern).ToggleState


def press_master(window):
    ui.control(window, "ConsentModelsAll", 5).GetPattern(auto.PatternId.TogglePattern).Toggle()
    time.sleep(1.0)


try:
    consent = ui.to_consent(40)
    check("the consent window shows", consent is not None)
    time.sleep(1.5)
    ui.invoke(consent, "ConsentModelsChange")                              # all four of the visitor's models, not only the ticked ones
    time.sleep(1.0)
    rows = boxes(consent)
    print("   models:", [(r[0][-18:], r[1]) for r in rows], flush=True)
    check("the four models are boxes (the site takes three to eight)", len(rows) == 4)
    ticked = sum(1 for r in rows if r[1])
    check("the wallet ticked several of them at first", ticked >= 3, str(ticked))
    shot(consent, "1-many-ticked")
    check("the master box is there and shows how many are ticked", master_state(consent) == (1 if ticked == 4 else 2), str(master_state(consent)))

    press_master(consent)
    rows = boxes(consent)
    check("one press lets go of every tick", len(rows) == 4 and not any(r[1] for r in rows), str(rows))
    check("... the master box shows none", master_state(consent) == 0, str(master_state(consent)))
    check("... the count says none are ticked", ui.control(consent, "ConsentModelsCount").Name.startswith("0 "), ui.control(consent, "ConsentModelsCount").Name)
    check("... and Allow waits (the site needs at least one)", not ui.control(consent, "ConsentAllow").IsEnabled)
    shot(consent, "2-none-ticked")

    press_master(consent)
    rows = boxes(consent)
    check("a press with none ticked ticks the models again, up to what the site takes", sum(1 for r in rows if r[1]) == 4, str(sum(1 for r in rows if r[1])))

    press_master(consent)
    rows = boxes(consent)
    keep = [r[0] for r in rows[1:]]                                        # beta, gamma, delta by hand: one of the four stays unticked
    ui.toggle(consent, keep[0], True)
    time.sleep(1.0)
    check("one model ticked by hand: only that one is ticked", [r[0] for r in boxes(consent) if r[1]] == [keep[0]])
    check("... the master box shows some", master_state(consent) == 2, str(master_state(consent)))
    check("... the count says one", ui.control(consent, "ConsentModelsCount").Name.startswith("1 "), ui.control(consent, "ConsentModelsCount").Name)
    check("... and Allow waits (the site needs at least three)", not ui.control(consent, "ConsentAllow").IsEnabled)
    ui.toggle(consent, keep[1], True)
    ui.toggle(consent, keep[2], True)
    time.sleep(1.0)
    rows = boxes(consent)
    check("three ticked by hand: exactly those three", sorted(r[0] for r in rows if r[1]) == sorted(keep), str(rows))
    check("... the master box still shows some (the fourth is not ticked)", master_state(consent) == 2, str(master_state(consent)))
    check("... and Allow is offered", ui.control(consent, "ConsentAllow").IsEnabled)
    shot(consent, "3-three-ticked")

    ui.invoke(consent, "ConsentAllow")
    check("the site is connected", site.wait_hello(30))
    already = len(bodies)
    r = site.call_text()
    check("a call is answered", r.get("ok") is True, str(r)[:160])
    served = {b.get("model") for b in bodies[already:]}
    kept = {k[len("ConsentModel-openai-compatible:"):].split("@")[0] for k in keep}
    check("only models the visitor kept answered, never the one left unticked", bool(served) and served <= kept, f"{served} of {kept}")
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
