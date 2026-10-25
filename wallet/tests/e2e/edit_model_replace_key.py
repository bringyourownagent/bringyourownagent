"""Replacing the key of a saved model behind an API key ("Edit" in Sources), on a hidden desktop (UI Automation only).

The model sits behind a fake service on a loopback port that accepts one key only. The wallet starts with no key saved. In the Edit window:
Test with a wrong key fails, Test with the right key passes (it used to test the saved key, so a key that had stopped working could never be
replaced), Save keeps the new key; Edit again with the key box left alone, Test passes with the saved key.

    <python with uiautomation, pillow, aiohttp> edit_model_replace_key.py --exe <Byoa.Wallet.exe> --home <empty folder in the scratchpad> [--port 5893]
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
ap.add_argument("--port", type=int, default=5893)
ap.add_argument("--desktop", default="byoa-edit-key")
a = ap.parse_args()
assert a.port != 9931
os.makedirs(a.home, exist_ok=True)
desk = hd.HiddenDesktop(a.desktop)

from aiohttp import web  # noqa: E402

GOOD = "good-key-123"
calls = []
failed = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def run_service():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)

    async def chat(request):
        auth = request.headers.get("Authorization", "")
        calls.append(auth)
        if auth != f"Bearer {GOOD}":
            return web.json_response({"error": {"code": "1113", "message": "Insufficient balance or no resource package"}}, status=429)
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": "wallet test ok"}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 5, "completion_tokens": 3}})

    async def models(_):
        return web.json_response({"data": [{"id": "m"}]})

    app = web.Application()
    app.router.add_post("/v1/chat/completions", chat)
    app.router.add_get("/v1/models", models)
    runner = web.AppRunner(app, access_log=None)
    loop.run_until_complete(runner.setup())
    loop.run_until_complete(web.TCPSite(runner, "127.0.0.1", a.port).start())
    loop.run_forever()


threading.Thread(target=run_service, daemon=True).start()
time.sleep(0.8)

endpoint = f"http://127.0.0.1:{a.port}/v1"
source_id = f"api:custom:m@{endpoint}"
source = {"id": source_id, "enabled": True, "kind": "api", "label": "m", "endpoint": endpoint, "model": "m", "skills": ["text.generate"], "languages": [],
          "secretRef": "api-key:custom:127.0.0.1", "endpointConfirmed": True, "labelFromAgent": False, "addedByAgent": False, "service": "custom",
          "modelId": None, "offer": True, "confirmedAddresses": ["127.0.0.1"], "confirmedAddressClass": "loopback"}
with open(os.path.join(a.home, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "sources": [source],
               "chains": [{"skill": "text.generate", "language": None, "stops": [{"sourceId": source_id, "enabled": True, "askBeforePaid": True}]}]}, f)

import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

wallet = desk.start([a.exe], env=dict(os.environ, BYOA_WALLET_HOME=a.home))
ui.ONLY_PIDS = {wallet.pid}


def walk(c, depth=0, out=None):
    out = [] if out is None else out
    try:
        for ch in c.GetChildren():
            out.append(ch)
            if depth < 30:
                walk(ch, depth + 1, out)
    except Exception:  # noqa: BLE001
        pass
    return out


def texts(window):
    return [c.Name for c in walk(window) if c.ControlTypeName == "TextControl" and c.Name]


def box_after(window, label_part):
    """The first edit box after the text that holds label_part, in the order of the window."""
    seen = False
    for c in walk(window):
        if c.ControlTypeName == "TextControl" and label_part in (c.Name or ""):
            seen = True
        elif seen and c.ControlTypeName == "EditControl":
            return c
    raise LookupError(label_part)


def on_sources(action, tries=8):
    last = None
    for _ in range(tries):
        try:
            w = ui.find_window("Agent wallet", 10)
            ui.select(w, "Tab-sources")
            time.sleep(1.2)
            return action(w)
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(0.8)
    raise last


def open_edit():
    def press_edit(w):
        ui.open_row(w, "AddedSourceExpand-" + source_id)                # Edit is among the settings of the row
        ui.invoke(w, "AddedSourceEdit-" + source_id)

    on_sources(press_edit)
    win = ui.find_window("Edit model source", 20)
    time.sleep(1.5)
    return ui.find_window("Edit model source", 10)


def press_test(win):
    before = set(texts(win))
    ui.invoke(win, "SourceTest")
    started = time.time()
    while time.time() - started < 40:
        time.sleep(0.8)
        win = ui.find_window("Edit model source", 5)
        fresh = [t for t in texts(win) if t not in before and "Testing" not in t]
        if fresh:
            return win, " | ".join(fresh)
    return win, "(nothing)"


try:
    win = open_edit()
    key_box = box_after(win, "API key")
    key_box.GetPattern(auto.PatternId.ValuePattern).SetValue("wrong-key")
    time.sleep(1.0)
    win = ui.find_window("Edit model source", 5)
    win, said = press_test(win)
    check("a wrong key fails the test", "Test passed" not in said, said[:110])
    check("... and the typed key (not a saved one) was what reached the service", calls[-1:] == ["Bearer wrong-key"], str(calls[-1:]))
    check("Save stays dark after a failed test", not ui.control(win, "SourceSave", 5).IsEnabled)

    box_after(win, "API key").GetPattern(auto.PatternId.ValuePattern).SetValue(GOOD)
    time.sleep(1.0)
    win = ui.find_window("Edit model source", 5)
    win, said = press_test(win)
    check("the right key passes the test while editing", "Test passed" in said, said[:110])
    check("... with the typed key", calls[-1:] == [f"Bearer {GOOD}"])
    check("Save lights up", ui.control(win, "SourceSave", 5).IsEnabled)
    ui.invoke(win, "SourceSave")
    time.sleep(2.5)
    check("the Edit window closes after Save", ui.find_window("Edit model source", 3) is None)

    # open it again and leave the key alone: the saved key must be the right one now
    before = len(calls)
    win = open_edit()
    check("the key box says a key is saved", any("key saved" in t for t in texts(win)))
    win, said = press_test(win)
    check("the saved key passes the test", "Test passed" in said, said[:110])
    check("... and it is the key typed before", len(calls) == before + 1 and calls[-1] == f"Bearer {GOOD}", str(calls[-1:]))
finally:
    desk.terminate(wallet)

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
