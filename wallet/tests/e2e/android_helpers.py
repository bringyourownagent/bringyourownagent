"""The two helper programs of android_e2e.py, and the test's side of talking to them.

They run on this computer, each in a process of its own started with CREATE_NO_WINDOW (so that no console window ever opens on the
person's screen) and log to a file. The phone reaches them through `adb reverse`, which makes their ports "localhost" for the phone.

    python android_helpers.py model --port 18234 --log model.jsonl     # a fake OpenAI-compatible model server
    python android_helpers.py site  --manifest m.json --log site.log   # fake_site.FakeSite, commands as JSON lines on stdin

The model server answers /v1/models and /v1/chat/completions (a JSON answer, or a stream when the request asks for one) and writes one JSON line for
every chat request it got: its messages, and whether the call was a stream. The site runner answers {"cmd": ...} lines on stdout:
link, state, wait_hello, wait_closed, drop, call_text, quit. Needs: aiohttp (the site and the model are built on it, like fake_site.py).
"""
import argparse
import asyncio
import json
import os
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
CREATE_NO_WINDOW = 0x08000000

MODEL_ID = "fake-gpt"
MODEL_ANSWER = "yes"
OPENROUTER_COST = 0.045         # what a call of the fake paid model reports it cost, in dollars


# ---------------------------------------------------------------------------------------------- the model server (its own process)
def run_model(port, log):
    from aiohttp import web

    async def models(_):
        return web.json_response({"object": "list", "data": [{"id": MODEL_ID, "object": "model"}]})

    delay = [0.0]

    async def control_delay(request):
        """The test asks the server to answer chat calls late (a model that is still getting ready), or on time again."""
        delay[0] = float(request.query.get("seconds", "0"))
        return web.json_response({"delay": delay[0]})

    async def chat(request):
        body = await request.json()
        if delay[0] > 0:
            await asyncio.sleep(delay[0])
        with open(log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"at": time.time(), "model": body.get("model"), "stream": bool(body.get("stream")),
                                "messages": body.get("messages"), "has_schema": "response_format" in body}) + "\n")
        if body.get("stream"):
            resp = web.StreamResponse(headers={"Content-Type": "text/event-stream"})
            await resp.prepare(request)
            for piece in ("The fake ", "model says ", MODEL_ANSWER):
                await resp.write(("data: " + json.dumps({"choices": [{"delta": {"content": piece}}]}) + "\n\n").encode())
            await resp.write(("data: " + json.dumps({"choices": [{"delta": {}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 5, "completion_tokens": 3}}) + "\n\n").encode())
            await resp.write(b"data: [DONE]\n\n")
            return resp
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": MODEL_ANSWER}, "finish_reason": "stop"}],
                                  "usage": {"prompt_tokens": 5, "completion_tokens": 1}})

    # --- the same server also plays OpenRouter, for a Debug build told (by the file debug-openrouter.txt) to look for OpenRouter here: its public list of
    # models, and chat calls that report a price. The "paid" model costs OPENROUTER_COST a call. No real money, no real key: the key a test saves is made up,
    # and the server writes down what key came in the Authorization header, so that the test can see the key goes to its own provider and nowhere else.
    async def openrouter_models(_):
        return web.json_response({"data": [
            {"id": "fakeco/paid-one", "name": "FakeCo: Paid One", "context_length": 32000, "created": 1760000003,
             "pricing": {"prompt": "0.000004", "completion": "0.000016"}, "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]}},
            {"id": "fakeco/cheap-two", "name": "FakeCo: Cheap Two", "context_length": 64000, "created": 1760000002,
             "pricing": {"prompt": "0.0000001", "completion": "0.0000004"}, "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]}},
            {"id": "fakeco/free-three:free", "name": "FakeCo: Free Three (free)", "context_length": 8000, "created": 1760000001,
             "pricing": {"prompt": "0", "completion": "0"}, "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]}},
            {"id": "otherco/gemma-fake", "name": "OtherCo: Gemma Fake", "context_length": 128000, "created": 1760000000,
             "pricing": {"prompt": "0.0000005", "completion": "0.000002"}, "architecture": {"input_modalities": ["text", "image"], "output_modalities": ["text"]}},
        ]})

    async def openrouter_chat(request):
        body = await request.json()
        with open(log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"at": time.time(), "route": "openrouter", "model": body.get("model"), "stream": bool(body.get("stream")),
                                "auth": request.headers.get("Authorization"), "messages": body.get("messages")}) + "\n")
        usage = {"prompt_tokens": 5, "completion_tokens": 1, "cost": OPENROUTER_COST}
        if body.get("stream"):
            resp = web.StreamResponse(headers={"Content-Type": "text/event-stream"})
            await resp.prepare(request)
            await resp.write(("data: " + json.dumps({"choices": [{"delta": {"content": MODEL_ANSWER}}]}) + "\n\n").encode())
            await resp.write(("data: " + json.dumps({"choices": [{"delta": {}, "finish_reason": "stop"}], "usage": usage}) + "\n\n").encode())
            await resp.write(b"data: [DONE]\n\n")
            return resp
        return web.json_response({"choices": [{"message": {"role": "assistant", "content": MODEL_ANSWER}, "finish_reason": "stop"}], "usage": usage})

    app = web.Application()
    app.router.add_get("/v1/models", models)
    app.router.add_post("/v1/chat/completions", chat)
    app.router.add_get("/control/delay", control_delay)
    app.router.add_get("/api/v1/models", openrouter_models)
    app.router.add_post("/api/v1/chat/completions", openrouter_chat)
    web.run_app(app, host="127.0.0.1", port=port, access_log=None, print=None)


# ---------------------------------------------------------------------------------------------- the site (its own process)
def run_site(manifest_path, log):
    sys.path.insert(0, HERE)
    from fake_site import FakeSite

    manifest = json.load(open(manifest_path, encoding="utf-8"))
    site = FakeSite("android", manifest).start()
    site.code_ttl = 1800        # a slow consent on the phone must not turn the link into a bad-session
    sessions = {}

    def say(obj):
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

    def note(text):
        with open(log, "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%H:%M:%S')} {text}\n")

    say({"ready": True, "port": site.port, "origin": site.origin})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        cmd = json.loads(line)
        note("cmd " + line)
        try:
            kind = cmd["cmd"]
            if kind == "link":
                link = site.new_link()
                session = link.rsplit("session=", 1)[1]
                sessions[link] = session
                say({"link": link, "session": session})
            elif kind == "state":
                say({"requests": site.requests, "manifest_gets": site.manifest_gets, "upgrades": site.upgrades, "hellos": len(site.hellos),
                     "live": site.live is not None, "byes": site.byes, "closed": site.closed, "errors": site.errors_sent,
                     "last_hello": site.hellos[-1] if site.hellos else None})
            elif kind == "wait_hello":
                say({"ok": site.wait_hello(cmd.get("seconds", 30), cmd.get("count"))})
            elif kind == "wait_closed":
                end = time.time() + cmd.get("seconds", 30)
                while time.time() < end and site.live is not None:
                    time.sleep(0.1)
                say({"ok": site.live is None})
            elif kind == "drop":
                # the site ends the connection (as a site that went away would): the wallet's side learns of it by the close
                asyncio.run_coroutine_threadsafe(site.live.close(), site._loop).result(10)
                say({"ok": True})
            elif kind == "call_text":
                result = site.call_text(choices=cmd.get("choices", ("yes", "no")), seconds=cmd.get("seconds", 60), text=cmd.get("text"))
                say(result)
            elif kind == "quit":
                say({"bye": True})
                break
            else:
                say({"error": "unknown command " + kind})
        except Exception as e:  # noqa: BLE001  (the test reads the error from the line; the log has the rest)
            note("error " + repr(e))
            say({"error": repr(e)})
    os._exit(0)


# ---------------------------------------------------------------------------------------------- the test's side
class Helper:
    """A helper process, hidden, with its log; the test stops it by its own process id."""

    def __init__(self, args, stderr_log, with_stdin=False):
        self.log = open(stderr_log, "ab")
        self.proc = subprocess.Popen([sys.executable, os.path.abspath(__file__), *args],
                                     stdin=subprocess.PIPE if with_stdin else subprocess.DEVNULL,
                                     stdout=subprocess.PIPE if with_stdin else self.log, stderr=self.log,
                                     creationflags=CREATE_NO_WINDOW, text=with_stdin, bufsize=1 if with_stdin else -1)
        self._lock = threading.Lock()

    @property
    def pid(self):
        return self.proc.pid

    def ask(self, **cmd):
        """One command to a site runner, and its one line of answer."""
        with self._lock:
            self.proc.stdin.write(json.dumps(cmd) + "\n")
            self.proc.stdin.flush()
            line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("the helper closed its output")
        return json.loads(line)

    def stop(self):
        """Ends the helper: only the process this object started, by its process id. A venv's python.exe starts the real interpreter as its
        child, so the tree of that one id goes."""
        if self.proc.poll() is None:
            try:
                if self.proc.stdin:
                    self.ask(cmd="quit")
            except Exception:  # noqa: BLE001
                pass
            try:
                self.proc.wait(5)
            except subprocess.TimeoutExpired:
                pass
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(self.proc.pid), "/T", "/F"], capture_output=True, creationflags=CREATE_NO_WINDOW)
        elif self.proc.poll() is None:
            self.proc.terminate()
        self.log.close()


def start_model(port, requests_log, stderr_log):
    return Helper(["model", "--port", str(port), "--log", requests_log], stderr_log)


def start_site(manifest_path, log, stderr_log):
    helper = Helper(["site", "--manifest", manifest_path, "--log", log], stderr_log, with_stdin=True)
    ready = json.loads(helper.proc.stdout.readline())
    helper.port, helper.origin = ready["port"], ready["origin"]
    return helper


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("what", choices=["model", "site"])
    ap.add_argument("--port", type=int)
    ap.add_argument("--manifest")
    ap.add_argument("--log", required=True)
    a = ap.parse_args()
    if a.what == "model":
        run_model(a.port, a.log)
    else:
        run_site(a.manifest, a.log)
