"""A site that asks for text and for pictures (PROPOSAL-v3 4.1, 4.3, 4.4), on a hidden desktop (nothing appears on the screen somebody works at).

The wallet has its own data folder, a fake OpenAI-compatible runtime on a loopback port for its local models, and a stand-in for OpenRouter on
another (BYOA_OPENROUTER_ENDPOINT: the key is a made-up one, nothing leaves this computer, nothing is paid). Its models:
  alpha     a local text model
  draws     a local model that its owner says writes text and draws (it cannot draw here: only the OpenRouter key draws)
  nano      behind the key, writes text and draws ("Nano Banana")
  imagine   behind the key, only draws
  texty     behind the key, only writes text
  agent     the visitor's own agent (its program is cmd.exe; it is never run here)
Site A asks for text (required) and pictures (optional). The window shows two sections with the same controls; Nano is in both, Imagine only in the
pictures, Texty and Alpha only in the text, the agent only in the text; "1 of your models does not fit" counts the local model that draws, in the
picture section. With the optional picture section unticked, the grant has no image.generate; a new link of site A then shows the same window,
with "already allowed" on the text, and Allow adds the pictures. Site B takes both sections: the wallet asks before the
first picture (the price is not known), the pictures arrive as image frames that join to the stand-in's bytes with the same sha256, one of them over a
megabyte in several parts, one made smaller by the wallet when the site's limit asks for it, and the refs of the grant do not repeat.
Buttons are pressed through UI Automation only.

    <python with uiautomation, pillow, aiohttp> consent_pictures_ui.py --exe <Byoa.Wallet.exe> --home <empty wallet data folder> [--shots <dir>]
"""
import argparse
import asyncio
import hashlib
import io
import json
import os
import subprocess
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
ap.add_argument("--port", type=int, default=5898, help="loopback port of the fake local runtime (never 9931: that is a real Llama)")
ap.add_argument("--openrouter-port", type=int, default=5899, help="loopback port of the stand-in for OpenRouter")
ap.add_argument("--desktop", default="byoa-consent-pictures")
a = ap.parse_args()
assert 9931 not in (a.port, a.openrouter_port)
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)

from aiohttp import web  # noqa: E402
from PIL import Image  # noqa: E402

failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def png(width, height, noise=False):
    image = Image.frombytes("RGB", (width, height), os.urandom(width * height * 3)) if noise else Image.new("RGB", (width, height), (200, 80, 40))
    out = io.BytesIO()
    image.save(out, "PNG")
    return out.getvalue()


SMALL = png(32, 24)
BIG = png(650, 650, noise=True)             # a bit over a megabyte: four parts, and over the default limit of a million bytes
assert len(BIG) > 1_000_000
SMALL_B64 = __import__("base64").b64encode(SMALL).decode()
BIG_B64 = __import__("base64").b64encode(BIG).decode()

# ------------------------------------------------------------------ the fake local runtime and the stand-in for OpenRouter
LOCAL = ("alpha", "draws")
CATALOG = {"data": [
    {"id": "google/nano-banana", "name": "Google: Nano Banana", "context_length": 32768, "created": 1760000000,
     "pricing": {"prompt": "0.0000003", "completion": "0.000001"}, "architecture": {"input_modalities": ["text", "image"], "output_modalities": ["text", "image"]}},
    {"id": "someone/imagine", "name": "Someone: Imagine", "context_length": 4096, "created": 1760000001,
     "pricing": {"prompt": "0.000001", "completion": "0.000001"}, "architecture": {"input_modalities": ["text"], "output_modalities": ["image"]}},
    {"id": "openai/texty", "name": "OpenAI: Texty", "context_length": 100000, "created": 1760000002,
     "pricing": {"prompt": "0.0000001", "completion": "0.0000004"}, "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]}},
]}
local_bodies = []
router_bodies = []          # (headers, body) of every request the stand-in for OpenRouter got
FAKE_KEY = "sk-or-fake"                     # short, so no scanner takes it for a real key


def run_servers():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)

    async def local_models(_):
        return web.json_response({"object": "list", "data": [{"id": m, "object": "model", "status": {"value": "loaded"}} for m in LOCAL]})

    async def local_chat(request):
        local_bodies.append(await request.json())
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": "yes"}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 5, "completion_tokens": 1}})

    async def router_models(_):
        return web.json_response(CATALOG)

    async def router_chat(request):
        body = await request.json()
        router_bodies.append((dict(request.headers), body))
        if "modalities" not in body:
            return web.json_response({"choices": [{"message": {"role": "assistant", "content": "yes"}}], "usage": {"prompt_tokens": 5, "completion_tokens": 1, "cost": 0.0001}})
        prompt = json.dumps(body["messages"])
        data = [BIG_B64] if "BIG" in prompt else [SMALL_B64]          # one picture a request: a count of two takes two requests
        images = [{"type": "image_url", "image_url": {"url": "data:image/png;base64," + d}} for d in data]
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": "here", "images": images}}], "usage": {"cost": 0.005}})

    local = web.Application()
    local.router.add_get("/v1/models", local_models)
    local.router.add_post("/v1/chat/completions", local_chat)
    router = web.Application(client_max_size=16 * 1024 * 1024)
    router.router.add_get("/api/v1/models", router_models)
    router.router.add_post("/api/v1/chat/completions", router_chat)
    for app, port in ((local, a.port), (router, a.openrouter_port)):
        runner = web.AppRunner(app, access_log=None)
        loop.run_until_complete(runner.setup())
        loop.run_until_complete(web.TCPSite(runner, "127.0.0.1", port).start())
    loop.run_forever()


threading.Thread(target=run_servers, daemon=True).start()
time.sleep(0.8)

# ------------------------------------------------------------------ the wallet's own folder
endpoint = f"http://127.0.0.1:{a.port}/v1"


def local_source(model, **said):
    return {"id": f"openai-compatible:{model}@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": model, "endpoint": endpoint,
            "model": model, "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False,
            "addedByAgent": False, "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback", **said}


def key_source(model):
    return {"id": f"openrouter:{model}", "enabled": True, "kind": "openrouter", "model": model, "endpoint": "https://openrouter.ai/api/v1",
            "skills": ["text.generate"], "languages": [], "offer": False}


sources = [local_source("alpha", inputs=["text"], outputs=["text"]), local_source("draws", inputs=["text"], outputs=["text", "image"]),
           key_source("google/nano-banana"), key_source("someone/imagine"), key_source("openai/texty")]
agent = {"id": "agent:fake", "enabled": True, "kind": "agent", "label": "Fake agent", "model": "fake", "skills": ["text.generate"], "languages": [],
         "offer": False, "agentCommand": "cmd", "agentFlavor": "claude-code"}
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": sources + [agent]}, f)
# the wallet's copy of OpenRouter's public list, so that it knows what each model reads and makes from the start
with open(os.path.join(a.home, "models-catalog.json"), "w", encoding="utf-8") as f:
    json.dump(CATALOG, f)
key_file = os.path.join(a.home, "fake-openrouter-key.txt")
with open(key_file, "w", encoding="utf-8") as f:
    f.write(FAKE_KEY)
env = dict(os.environ, BYOA_WALLET_HOME=a.home, BYOA_WALLET_DEV_LOCAL="1", BYOA_OPENROUTER_ENDPOINT=f"http://127.0.0.1:{a.openrouter_port}/")
imported = subprocess.run([a.exe, "--import-key", "openrouter", key_file], env=env, capture_output=True, timeout=60)
check("a made-up OpenRouter key is in the test wallet's store", imported.returncode == 0, imported.stderr.decode(errors="replace")[:200])

# ------------------------------------------------------------------ two sites: text required, pictures optional
from fake_site import FakeSite  # noqa: E402


def manifest(summary):
    return {
        "byoa": "0.1-draft",
        "purpose": {"summary": summary, "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 30}},
        "skills": {
            "text.generate": {"required": True, "models": {
                "count": {"min": 1, "max": 16}, "kind": "any", "recommend": ["agent/*", "anthropic/claude-sonnet-5.5", "google/nano-banana"],
                "why": "Draws each icon as SVG code."}},
            "image.generate": {"required": False, "models": {
                "count": {"min": 1, "max": 4}, "recommend": ["google/nano-banana*"], "why": "Draws picture candidates for your icon."}},
        },
        "privacy": "personal",
        "expectedCost": {"perSession": "0.50 USD"},
    }


site_a = FakeSite("pictures-a", manifest("An icon designer: pictures are optional")).start()
site_b = FakeSite("pictures-b", manifest("The same icon designer, asked again with the pictures on")).start()

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe, "--url", site_a.new_link()], env=env)
ui.ONLY_PIDS = {wallet.pid}
print("wallet started", wallet.pid, flush=True)

NANO, IMAGINE, TEXTY = "openrouter:google/nano-banana", "openrouter:someone/imagine", "openrouter:openai/texty"
ALPHA, DRAWS, AGENT = f"openai-compatible:alpha@{endpoint}", f"openai-compatible:draws@{endpoint}", "agent:fake"


def shot(window, name):
    if a.shots:
        try:
            desk.shot(window, os.path.join(a.shots, name + ".png"))
        except OSError as e:
            print("no picture:", e)


def present(window, automation_id, seconds=1.5):
    try:
        ui.control(window, automation_id, seconds)
        return True
    except LookupError:
        return False


def ticked(window, key, tag=""):
    return ui.control(window, "ConsentModel" + tag + "-" + key).GetPattern(auto.PatternId.TogglePattern).ToggleState == 1


def name_of(window, automation_id):
    return ui.control(window, automation_id).Name or ""


def has_text(window, text):
    return window.Control(Name=text, searchDepth=40).Exists(1.5, 0.2)


def safe(source_id):
    return "".join(c if c.isalnum() else "-" for c in source_id)


def wait_for(pred, seconds=8.0):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if pred():
                return True
        except (OSError, ValueError, KeyError):
            pass
        time.sleep(0.3)
    return False


try:
    # ------------------------------------------------------------------ site A: two sections, one rule, the optional one left off
    consent = ui.to_consent(40)
    check("the consent window shows", consent is not None)
    time.sleep(1.5)
    check("a section for text generation", has_text(consent, "Text generation"))
    check("a section for image generation", has_text(consent, "Image generation"))
    check("the picture section is optional and has its own Include box", present(consent, "ConsentInclude-image.generate"))
    check("the text section is not optional", not present(consent, "ConsentInclude-text.generate", 0.5))
    check("each section has the site's reason", present(consent, "ConsentModelsWhy") and present(consent, "ConsentModelsWhyPictures"))
    ui.invoke(consent, "ConsentModelsChange")                     # every model of the visitor's, not only the ticked ones
    ui.invoke(consent, "ConsentModelsChangePictures")
    time.sleep(1.0)
    shot(consent, "1-two-sections")

    # the offering rule, section by section
    check("a model that writes text and draws is in the text section", present(consent, "ConsentModel-" + NANO))
    check("... and in the picture section, with the price of a picture not known", present(consent, "ConsentModelPictures-" + NANO)
          and "price unknown" in name_of(consent, "ConsentModelPictures-" + NANO), name_of(consent, "ConsentModelPictures-" + NANO))
    check("a model that only draws is not in the text section", not present(consent, "ConsentModel-" + IMAGINE))
    check("... and is in the picture section", present(consent, "ConsentModelPictures-" + IMAGINE))
    check("a model that only writes is in the text section and not in the picture section",
          present(consent, "ConsentModel-" + TEXTY) and not present(consent, "ConsentModelPictures-" + TEXTY))
    check("a local text model is only in the text section", present(consent, "ConsentModel-" + ALPHA) and not present(consent, "ConsentModelPictures-" + ALPHA))
    check("a local model its owner says draws is in the text section", present(consent, "ConsentModel-" + DRAWS))
    check("... and is not offered for pictures, because only the OpenRouter key draws", not present(consent, "ConsentModelPictures-" + DRAWS))
    check("the agent is only in the text section", present(consent, "ConsentModel-" + AGENT) and not present(consent, "ConsentModelPictures-" + AGENT))
    check("Nano, which the site recommends, is ticked in each section", ticked(consent, NANO) and ticked(consent, NANO, "Pictures"))
    check("Imagine is not ticked (nobody recommended it)", not ticked(consent, IMAGINE, "Pictures"))

    # the line of the models that do not fit, in each section
    text_misses = name_of(consent, "ConsentModelsMisses")
    check("the text section counts the model that only draws", text_misses.startswith("1 of your models does not fit"), text_misses)
    picture_misses = name_of(consent, "ConsentModelsMissesPictures")
    check("the picture section counts the local model that draws", picture_misses.startswith("1 of your models does not fit"), picture_misses)
    ui.invoke(consent, "ConsentModelsMissesPictures")
    time.sleep(0.8)
    reason = name_of(consent, "ConsentModelsMissPictures-0") if present(consent, "ConsentModelsMissPictures-0") else ""
    check("... with its reason", "draws" in reason and "only behind an OpenRouter key" in reason, reason)
    shot(consent, "2-do-not-fit")

    # the optional section left off: the grant has no pictures
    ui.toggle(consent, "ConsentInclude-image.generate", False)
    time.sleep(0.8)
    check("Allow is offered", ui.control(consent, "ConsentAllow").IsEnabled)
    ui.invoke(consent, "ConsentAllow")
    check("site A is connected", site_a.wait_hello(30))
    grant = site_a.hellos[-1]["grant"] if site_a.hellos else {}
    check("the grant has no image.generate", grant.get("skills") == ["text.generate"], str(grant.get("skills")))
    check("... and no list of picture models", "image.generate" not in (grant.get("models") or {}), str(grant.get("models")))
    check("... but the wallet says it can serve pictures", "image.generate" in (grant.get("capabilities") or []) and "parts" in grant["capabilities"], str(grant.get("capabilities")))
    refused = site_a.call_image("m1")
    check("a picture call on that grant is answered ok:false", refused.get("ok") is False, str(refused)[:200])

    # ------------------------------------------------------------------ site A asks again for its pictures: the same window, the text "already allowed"
    again = desk.start([a.exe, "--url", site_a.new_link()], env=env)
    ui.ONLY_PIDS = {wallet.pid, again.pid}
    consent = ui.to_consent(40)
    check("site A's second window shows", consent is not None)
    time.sleep(1.5)
    check("it is the same window as the first time: both sections, under \"Asks for 2:\"",
          name_of(consent, "ConsentAsksFor") == "Asks for 2:" and has_text(consent, "Text generation") and has_text(consent, "Image generation"))
    check("each block has its number", present(consent, "ConsentNumber-text.generate") and present(consent, "ConsentNumber-image.generate"))
    check("the text, allowed before, says \"already allowed\"", present(consent, "ConsentAllowed-text.generate"))
    check("the pictures, asked for now, do not", not present(consent, "ConsentAllowed-image.generate", 0.5))
    check("no \"Only what is new\" and no \"updated\" chip", not has_text(consent, "Only what is new") and not has_text(consent, "updated"))
    check("the text models are ticked as before", ticked(consent, NANO))
    shot(consent, "2b-asks-for-more")
    ui.invoke(consent, "ConsentAllow")
    check("site A is connected again, now with the pictures",
          wait_for(lambda: len(site_a.hellos) >= 2 and sorted(site_a.hellos[-1]["grant"].get("skills") or []) == ["image.generate", "text.generate"], 30),
          str(site_a.hellos[-1]["grant"].get("skills") if site_a.hellos else None))

    # ------------------------------------------------------------------ site B: both sections taken, a picture comes
    second = desk.start([a.exe, "--url", site_b.new_link()], env=env)
    ui.ONLY_PIDS = {wallet.pid, second.pid}                         # (the second process hands its link to the first and ends)
    consent = ui.to_consent(40)
    check("site B's window shows", consent is not None)
    time.sleep(1.5)
    check("the picture section is included by default", ui.control(consent, "ConsentInclude-image.generate").GetPattern(auto.PatternId.TogglePattern).ToggleState == 1)
    shot(consent, "3-both-sections")
    ui.invoke(consent, "ConsentDetails")                            # the settings window opens too: the Sites page is looked at below
    time.sleep(1.0)
    ui.invoke(consent, "ConsentAllow")
    check("site B is connected", site_b.wait_hello(30))
    grant = site_b.hellos[-1]["grant"] if site_b.hellos else {}
    check("the grant has both skills", sorted(grant.get("skills") or []) == ["image.generate", "text.generate"], str(grant.get("skills")))
    texts = [m["ref"] for m in (grant.get("models") or {}).get("text.generate", [])]
    pictures = [m["ref"] for m in (grant.get("models") or {}).get("image.generate", [])]
    check("the text models have refs m1.., the picture models go on from there, and no ref repeats",
          texts and pictures and len(set(texts + pictures)) == len(texts + pictures) and pictures[0] == f"m{len(texts) + 1}", f"{texts} {pictures}")
    ref = pictures[0] if pictures else "m2"

    # the first picture: the wallet asks, because the price of a picture is not known
    outcome = {}

    def draw():
        try:
            outcome["result"] = site_b.call_image(ref, "Draw a fox", aspect="1:1", seconds=90)
        except Exception as e:  # noqa: BLE001
            outcome["error"] = repr(e)

    caller = threading.Thread(target=draw, daemon=True)
    caller.start()
    toast = ui.find_window("Agent wallet paid fallback", 30)
    check("the wallet asks before the first picture is paid for", toast is not None)
    if toast is not None:
        shot(toast, "4-paid-question")
        ui.invoke(toast, "PaidAlways")
    caller.join(60)
    result = outcome.get("result") or {}
    check("the call is answered ok", result.get("ok") is True, str(outcome)[:300])
    first = (result.get("pictures") or [{}])[0]
    check("the picture is the stand-in's, with the same sha256", first.get("data") == SMALL and first.get("sha_ok") and first.get("size_ok"), str(first.get("mime")))
    check("... a PNG by its bytes", first.get("mime") == "image/png" and first.get("reduced") is None)
    check("the receipt carries the cost OpenRouter reported and the ref", (result.get("receipt") or {}) == {"cost": "0.005 USD", "provider": "api-key", "model": ref}, str(result.get("receipt")))
    sent = [b for h, b in router_bodies if "modalities" in b]
    check("the stand-in was asked for pictures with the made-up key", bool(sent) and router_bodies[-1][0].get("Authorization") == "Bearer " + FAKE_KEY)
    body = sent[0] if sent else {}
    check("... for the model, with modalities, the usage and the aspect",
          body.get("model") == "google/nano-banana" and body.get("modalities") == ["image", "text"] and body.get("usage") == {"include": True}
          and body.get("image_config") == {"aspect_ratio": "1:1"}, json.dumps({k: v for k, v in body.items() if k != "messages"}))
    check("... and the prompt", body.get("messages", [{}])[0].get("content", [{}])[0] == {"type": "text", "text": "Draw a fox"})

    # more than one picture; "Always" is remembered, so no second question
    two = site_b.call_image(ref, "TWO foxes", count=2, seconds=60)
    check("count 2 gives two pictures without a second question", two.get("ok") is True and len(two.get("pictures") or []) == 2
          and all(p["data"] == SMALL and p["sha_ok"] for p in two["pictures"]), str(two)[:200])
    check("... each asked of the model in a request of its own", len([1 for _, b in router_bodies if "modalities" in b and "TWO" in json.dumps(b["messages"])]) == 2)

    # a picture over a megabyte comes in parts of at most 524,288 characters, and joins to the same bytes
    big = site_b.call_image(ref, "BIG fox", max_bytes=3_000_000, seconds=90)
    p = (big.get("pictures") or [{}])[0]
    check("a picture over a megabyte comes in several parts", big.get("ok") is True and p.get("parts", 0) >= 4 and p.get("frames_in_order"), str(p.get("parts")))
    check("... and joins to the same bytes (size and sha256 both match)", p.get("data") == BIG and p.get("size_ok") and p.get("sha_ok"))

    # the site's limit asks for a smaller picture: the wallet makes it smaller, in its own type, and says so
    small = site_b.call_image(ref, "BIG fox again", max_bytes=1_000_000, seconds=90)
    q = (small.get("pictures") or [{}])[0]
    check("a picture over the default limit is made smaller", small.get("ok") is True and q.get("reduced") is True and len(q.get("data") or b"") <= 1_000_000, str(len(q.get("data") or b"")))
    check("... it is still a PNG, whose size and sha256 describe the bytes that were sent", q.get("mime") == "image/png" and q.get("size_ok") and q.get("sha_ok"))
    check("... and it is a picture of the same proportions",
          (lambda im: abs(im.width / im.height - 1.0) < 0.02 and im.width < 650)(Image.open(io.BytesIO(q.get("data") or b""))) if q.get("data") else False)

    # a call that names no model, or a model that is not in the picture list, is answered ok:false
    check("a picture call for a ref that is not in the picture list is answered ok:false", site_b.call_image("m99").get("ok") is False)
    wrong = site_b.call_image(texts[0] if texts else "m1")
    check("... and so is one for a text model's ref", wrong.get("ok") is False, str(wrong)[:160])

    # the Sites page: each skill has its own list
    settings = ui.find_window("Agent wallet", 20)
    check("the settings window is open", settings is not None)
    ui.select(settings, "Tab-sites")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    host = "localhost:" + str(site_b.port)
    ui.open_row(settings, "SiteExpand-" + host)
    check("the site's card lists its text models", present(settings, f"SiteModelRemove-{host}-{safe(NANO)}", 4.0))
    check("... and, apart, the models it draws pictures with", present(settings, f"SiteModelPicturesRemove-{host}-{safe(NANO)}", 4.0))
    check("... and the card does not say that nothing can serve its pictures", not settings.Control(SubName="nothing on this computer can serve", searchDepth=40).Exists(1.0, 0.2))
    shot(settings, "5-sites")
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
