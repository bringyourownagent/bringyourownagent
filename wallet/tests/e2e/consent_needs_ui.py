"""A site that asks for models by what they can do (PROPOSAL-v2 3.1 to 3.8), on a hidden desktop (nothing appears on the screen somebody works at).

The wallet has its own data folder with four local models served by a fake OpenAI-compatible runtime on a loopback port, and the visitor's agent:
  alpha    declared by the visitor to read and make text only
  vision   declared to read pictures
  painter  declared to draw pictures (and not ticked "Also use as a text model")
  mystery  nothing declared (nobody knows what it reads)
  agent    the visitor's own agent (its program is cmd.exe; it is never run here)
A fake site asks for two skills; its text models must read pictures. The window shows two sections, the chips of what the site wants, only the
models that fit (ticked) or whose abilities are not known (marked, not ticked), the agent as an "agent" row that "can: ... / may lack: ..." and is
tickable, and a line "2 of your models do not fit" that opens to a reason for each. After Allow the hello carries the wallet's features and only
verdicts (booleans). A second site that asks for what nothing here is gets no Allow and is told what would fix it. Buttons are pressed through
UI Automation only.

    <python with uiautomation, pillow, aiohttp> consent_needs_ui.py --exe <Byoa.Wallet.exe> --home <empty wallet data folder> [--shots <dir>]
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
ap.add_argument("--port", type=int, default=5897, help="loopback port of the fake runtime (never 9931: that is a real Llama)")
ap.add_argument("--desktop", default="byoa-consent-needs")
a = ap.parse_args()
assert a.port != 9931
os.makedirs(a.home, exist_ok=True)
if a.shots:
    os.makedirs(a.shots, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)

from aiohttp import web  # noqa: E402

failed = []
MODELS = ("alpha", "vision", "painter", "mystery")


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


# ------------------------------------------------------------------ a fake OpenAI-compatible runtime with the four models
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

# ------------------------------------------------------------------ the wallet's own folder: the four models and the agent
endpoint = f"http://127.0.0.1:{a.port}/v1"


def source(model, **said):
    return {"id": f"openai-compatible:{model}@{endpoint}", "enabled": True, "kind": "openai-compatible", "label": model, "endpoint": endpoint,
            "model": model, "skills": ["text.generate"], "languages": [], "secretRef": None, "endpointConfirmed": True, "labelFromAgent": False,
            "addedByAgent": False, "service": None, "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback", **said}


sources = [
    source("alpha", inputs=["text"], outputs=["text"]),
    source("vision", inputs=["text", "image"]),
    source("painter", outputs=["text", "image"]),
    source("mystery"),
]
agent = {"id": "agent:fake", "enabled": True, "kind": "agent", "label": "Fake agent", "model": "fake", "skills": ["text.generate"], "languages": [],
         "offer": False, "agentCommand": "cmd", "agentFlavor": "claude-code"}
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": sources + [agent],
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": s["id"], "enabled": True, "askBeforePaid": True} for s in sources]}]}, f)

# ------------------------------------------------------------------ the site: two skills, text models that read pictures
from fake_site import FakeSite  # noqa: E402

manifest = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "An icon designer: it asks a model that can look at a candidate to write clean SVG code for it", "limits": {"maxTextChars": 20000, "maxClipSeconds": 15, "maxCallsPerMinute": 20}},
    "skills": {
        "text.generate": {"required": True, "models": {
            "count": {"min": 1, "max": 2}, "kind": "any", "needs": {"input": ["text", "image"], "output": ["text"]},
            "recommend": ["anthropic/claude-sonnet-5.5", "agent/*"], "abilities": ["web.search", "image.read"],
            "why": "Looks at a candidate and writes clean SVG code for it."}},
        "audio.transcribe": {"required": False, "languages": ["ru"]},
    },
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
}
site = FakeSite("needs", manifest).start()
link = site.new_link()
env = dict(os.environ, BYOA_WALLET_HOME=a.home, BYOA_WALLET_DEV_LOCAL="1")

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe, "--url", link], env=env)
ui.ONLY_PIDS = {wallet.pid}
print("wallet started", wallet.pid, flush=True)

KEY = {m: f"openai-compatible:{m}@{endpoint}" for m in MODELS}
AGENT = "agent:fake"


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


def ticked(window, key):
    return ui.control(window, "ConsentModel-" + key).GetPattern(auto.PatternId.TogglePattern).ToggleState == 1


def name_of(window, automation_id):
    return ui.control(window, automation_id).Name or ""


def has_text(window, text):
    return window.Control(Name=text, searchDepth=40).Exists(1.5, 0.2)


second = None
try:
    consent = ui.to_consent(40)
    check("the consent window shows", consent is not None)
    time.sleep(1.5)

    # two skills, two sections
    check("a section for text generation", has_text(consent, "Text generation"))
    check("a section for speech to text", has_text(consent, "Speech to text"))
    check("the site's reason for its models is there", present(consent, "ConsentModelsWhy"))

    # the chips of what the site wants, in plain words
    needs = name_of(consent, "ConsentModelsNeeds")
    check("the site's needs are said in plain words", "reads pictures" in needs and "a model or your agent" in needs, needs)

    ui.invoke(consent, "ConsentModelsChange")                              # every model of the visitor's, not only the ticked ones
    time.sleep(1.0)
    shot(consent, "1-sections")

    # the primary-role rule and the fit
    check("a model declared to draw pictures is not in the text section", not present(consent, "ConsentModel-" + KEY["painter"]))
    check("a model declared text-only is not in a section that needs pictures", not present(consent, "ConsentModel-" + KEY["alpha"]))
    check("a model declared to read pictures is there and ticked", present(consent, "ConsentModel-" + KEY["vision"]) and ticked(consent, KEY["vision"]))
    check("a model nobody knows is there", present(consent, "ConsentModel-" + KEY["mystery"]))
    check("... and is not ticked", not ticked(consent, KEY["mystery"]))
    mark = name_of(consent, "ConsentModelMark-" + KEY["mystery"])
    check("... and is marked as not known", mark.startswith("not known") and "what it reads" in mark, mark)

    # the agent: its own row, what it can do and what it may lack, tickable
    check("the agent has a row", present(consent, "ConsentModel-" + AGENT))
    abilities = name_of(consent, "ConsentAgentAbilities-" + AGENT)
    check("... that says what it can do and what it may lack", "can: search the web, read web pages" in abilities and "may lack: look at pictures" in abilities, abilities)
    check("... and what falls short", "cannot read pictures" in name_of(consent, "ConsentModelMark-" + AGENT))
    check("... it is not ticked by the wallet", not ticked(consent, AGENT))
    ui.toggle(consent, "ConsentModel-" + AGENT, True)
    time.sleep(0.8)
    check("... and the visitor can tick it", ticked(consent, AGENT))
    ui.toggle(consent, "ConsentModel-" + AGENT, False)
    time.sleep(0.8)
    check("... and untick it again", not ticked(consent, AGENT))

    # the line of the models that do not fit
    line = name_of(consent, "ConsentModelsMisses")
    check("a line counts the models that do not fit", line.startswith("2 of your models do not fit"), line)
    ui.invoke(consent, "ConsentModelsMisses")
    time.sleep(0.8)
    print("   the line after it was pressed:", name_of(consent, "ConsentModelsMisses"), flush=True)
    reasons = [name_of(consent, f"ConsentModelsMiss-{i}") for i in range(2) if present(consent, f"ConsentModelsMiss-{i}")]
    check("... and opens to a reason for each", len(reasons) == 2, str(reasons))
    check("... the picture model: it draws pictures", any("painter" in r and "draws pictures" in r for r in reasons), str(reasons))
    check("... the text-only model: it cannot read pictures", any("alpha" in r and "cannot read pictures" in r for r in reasons), str(reasons))
    shot(consent, "2-do-not-fit")

    # the names the site suggests that the visitor has none of
    examples = name_of(consent, "ConsentModelsExamples") if present(consent, "ConsentModelsExamples") else ""
    check("a name the site suggests and he has none of is an example, with a way to add it",
          "anthropic/claude-sonnet-5.5" in examples and present(consent, "ConsentModelsExampleAdd"), examples)

    check("Allow is offered (the model that reads pictures is ticked)", ui.control(consent, "ConsentAllow").IsEnabled)
    ui.invoke(consent, "ConsentDetails")                                   # the settings window opens too: the editors are tried below
    time.sleep(1.0)
    ui.invoke(consent, "ConsentAllow")
    check("the site is connected", site.wait_hello(30))

    # what the site is told
    hello = site.hellos[-1]["grant"] if site.hellos else {}
    check("the hello names the features of the wallet", hello.get("capabilities") == ["needs", "can", "kind", "cancel"], str(hello.get("capabilities")))
    models = (hello.get("models") or {}).get("text.generate", [])
    check("the site is given the one model that reads pictures", len(models) == 1, str(models))
    can = models[0].get("can") if models else None
    check("... with verdicts only: a yes for what it reads and what it writes, nothing for what nobody said",
          can == {"input": True, "output": True}, str(can))
    already = len(bodies)
    r = site.call_text()
    check("a call is answered", r.get("ok") is True, str(r)[:160])
    served = {b.get("model") for b in bodies[already:]}
    check("... by the model that reads pictures", served == {"vision"}, str(served))

    # ------------------------------------------------------------------ the editors in My models and the marks in Sites
    settings_path = os.path.join(a.home, "settings.json")

    def stored(model):
        for _ in range(20):
            try:
                with open(settings_path, encoding="utf-8") as f:
                    return next(x for x in json.load(f)["sources"] if x["model"] == model)
            except (OSError, ValueError, StopIteration, KeyError):  # the file is being written
                time.sleep(0.3)
        return {}

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

    settings = ui.find_window("Agent wallet", 20)
    check("the settings window is open", settings is not None)
    ui.select(settings, "Tab-sources")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    mystery_id = KEY["mystery"]
    ui.open_row(settings, "AddedSourceExpand-" + mystery_id)
    check("a model's settings have a 'What it can do' with ticks", present(settings, "CanReads-" + safe(mystery_id)) and present(settings, "CanDraws-" + safe(mystery_id)))
    check("... and no 'Also use as a text model' while the model is not known to draw pictures", not present(settings, "CanAlsoText-" + safe(mystery_id), 1.0))
    ui.toggle(settings, "CanReads-" + safe(mystery_id), True)
    check("'Reads pictures' reaches the settings file as the visitor's word",
          wait_for(lambda: stored("mystery").get("inputs") == ["text", "image"]), str(stored("mystery").get("inputs")))
    settings = ui.find_window("Agent wallet", 10)
    ui.toggle(settings, "CanReads-" + safe(mystery_id), False)
    check("... and unticked it goes from the file again (nothing said is nothing written)",
          wait_for(lambda: "inputs" not in stored("mystery") and "outputs" not in stored("mystery") and "alsoText" not in stored("mystery")), str(stored("mystery")))

    vision_id = KEY["vision"]
    settings = ui.find_window("Agent wallet", 10)
    ui.open_row(settings, "AddedSourceExpand-" + vision_id)
    ui.toggle(settings, "CanDraws-" + safe(vision_id), True)
    check("'Draws pictures' brings the 'Also use as a text model' tick", wait_for(lambda: present(ui.find_window("Agent wallet", 10), "CanAlsoText-" + safe(vision_id), 1.0)))
    check("... and reaches the settings file", wait_for(lambda: stored("vision").get("outputs") == ["text", "image"]), str(stored("vision").get("outputs")))
    check("... and the tick for text is off", stored("vision").get("alsoText") is None)
    shot(ui.find_window("Agent wallet", 10), "4-can-do")

    # the site was given this model; now it draws pictures and is not for text: the Sites page says so
    settings = ui.find_window("Agent wallet", 10)
    ui.select(settings, "Tab-sites")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    host = "localhost:" + str(site.port)
    ui.open_row(settings, "SiteExpand-" + host)
    note_id = f"SiteModelNote-{host}-{safe(vision_id)}"
    note = name_of(settings, note_id) if present(settings, note_id, 4.0) else ""
    check("the Sites page marks the model the site was given that no longer suits text", note.startswith("not given to the site: draws pictures"), note)
    shot(settings, "5-sites-mark")

    # and he can say the model may answer text too: the mark goes
    settings = ui.find_window("Agent wallet", 10)
    ui.select(settings, "Tab-sources")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    ui.open_row(settings, "AddedSourceExpand-" + vision_id)
    ui.toggle(settings, "CanAlsoText-" + safe(vision_id), True)
    check("'Also use as a text model' reaches the settings file", wait_for(lambda: stored("vision").get("alsoText") is True), str(stored("vision")))
    settings = ui.find_window("Agent wallet", 10)
    ui.select(settings, "Tab-sites")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    ui.open_row(settings, "SiteExpand-" + host)
    check("... and the Sites page no longer marks it", not present(settings, note_id, 2.0))

    # the agent's abilities are text, derived by the wallet; the guard page says what it cannot read
    settings = ui.find_window("Agent wallet", 10)
    ui.select(settings, "Tab-myagents")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    ui.open_row(settings, "AddedSourceExpand-" + AGENT)
    abilities_id = "AgentAbilities-" + safe(AGENT)
    text = name_of(settings, abilities_id) if present(settings, abilities_id, 4.0) else ""
    check("the agent's settings say what it can do, as text", "search the web, read web pages" in text and "cannot look at pictures" in text, text)
    settings = ui.find_window("Agent wallet", 10)
    ui.select(settings, "Tab-guard")
    time.sleep(1.5)
    settings = ui.find_window("Agent wallet", 10)
    guard_text = name_of(settings, "GuardReadsTextOnly") if present(settings, "GuardReadsTextOnly", 4.0) else ""
    check("the Agent guard page says it reads text only and a picture is not text", "reads text only" in guard_text and "picture is not text" in guard_text, guard_text)

    # ------------------------------------------------------------------ a second site asks for what nothing here is: models behind a key
    second_manifest = {
        "byoa": "0.1-draft",
        "purpose": {"summary": "A reader of long documents: it needs a model that runs behind an API key", "limits": {"maxTextChars": 20000}},
        "skills": {"text.generate": {"required": True, "models": {
            "count": 1, "needs": {"runs": ["api-key"]}, "why": "Reads a whole book at once."}}},
        "privacy": "personal-voice",
        "expectedCost": {"perSession": "0.05 USD"},
    }
    site2 = FakeSite("needs-key", second_manifest).start()
    second = desk.start([a.exe, "--url", site2.new_link()], env=env)
    ui.ONLY_PIDS = {wallet.pid, second.pid}                              # (the second process hands its link to the first and ends)
    consent2 = ui.to_consent(40)
    check("a second site's window shows", consent2 is not None)
    time.sleep(1.5)
    problem = name_of(consent2, "ConsentModelsProblem") if present(consent2, "ConsentModelsProblem") else ""
    check("the site asks for what no model here is: the window says what would fix it", "Add a model that fits" in problem and "runs on an API key" in problem, problem)
    check("... and Allow stays off", not ui.control(consent2, "ConsentAllow").IsEnabled)
    shot(consent2, "3-nothing-fits")
    ui.invoke(consent2, "ConsentDecline")
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
