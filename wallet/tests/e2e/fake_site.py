"""A small fake site for the direct transport's end-to-end test (CONTRACT-DIRECT.md with its §7): a page with a
bringyourownagent:// link that carries the manifest (base64url), single-use session codes, and the WebSocket endpoint
at /byoa/ws whose welcome echoes the manifest. It counts every HTTP request, so a test can prove that the wallet
contacted nothing before Allow. It runs on http://localhost:<port> (the wallet allows plain localhost only under
BYOA_WALLET_DEV_LOCAL=1).

    site = FakeSite("a"); site.start()
    link = site.new_link()            # a fresh session code, valid 120 s, single use
    site.wait_hello(30)               # the wallet's hello (after the visitor's Allow)
    site.call(wav_bytes, "ru")        # -> {"type": "result", "id": n, "ok": ..., ...}

Needs: aiohttp.
"""
import asyncio
import base64
import json
import secrets
import threading
import time
import urllib.parse

from aiohttp import WSMsgType, web

MANIFEST = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "Family geography game: recognise country names spoken aloud", "limits": {"maxClipSeconds": 15, "maxCallsPerMinute": 40}},
    "skills": {"audio.transcribe": {"languages": ["ru", "sk"], "required": True}},
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
}


class FakeSite:
    def __init__(self, name, manifest=None):
        self.name = name
        self.manifest = manifest or MANIFEST
        self.port = None
        self.codes = {}              # code -> (expiry, the manifest the link carried)
        self.requests = 0            # every HTTP request, the WebSocket upgrade included
        self.echo = None             # a manifest to echo instead of the link's (a lying site)
        self.pairings = set()
        self.manifest_gets = 0
        self.manifest_headers = []
        self.upgrades = 0
        self.hellos = []             # every hello frame, in order
        self.errors_sent = 0
        self.byes = 0
        self.closed = 0
        self.live = None             # the bound wallet connection
        self._loop = None
        self._next_id = 0
        self._waiting = {}
        self._hello_event = threading.Event()
        self._ready = threading.Event()

    # ------------------------------------------------------------------ the server, on its own thread
    def start(self):
        threading.Thread(target=self._run, daemon=True).start()
        self._ready.wait(10)
        return self

    def _run(self):
        self._loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self._loop)
        app = web.Application(client_max_size=1024 * 1024, middlewares=[self._count])
        app.router.add_get("/", self._page)
        app.router.add_get("/byoa/manifest.json", self._manifest)
        app.router.add_get("/byoa/ws", self._ws)
        runner = web.AppRunner(app, access_log=None)
        self._loop.run_until_complete(runner.setup())
        site = web.TCPSite(runner, "127.0.0.1", 0)
        self._loop.run_until_complete(site.start())
        self.port = site._server.sockets[0].getsockname()[1]
        self._ready.set()
        self._loop.run_forever()

    @web.middleware
    async def _count(self, request, handler):
        self.requests += 1
        return await handler(request)

    @property
    def manifest64(self):
        """The manifest as the link carries it: base64url of the JSON bytes, no padding."""
        return base64.urlsafe_b64encode(json.dumps(self.manifest, separators=(",", ":")).encode()).rstrip(b"=").decode()

    @property
    def origin(self):
        return f"http://localhost:{self.port}"

    def new_link(self, code=None):
        """A link with a fresh single-use code (or the given one, which may be unknown to the site)."""
        manifest = self.manifest64
        if code is None:
            code = secrets.token_urlsafe(24)
            self.codes[code] = (time.time() + 120, manifest)
        q = urllib.parse.quote
        return ("bringyourownagent://connect?v=0.1&endpoint=" + q(f"ws://localhost:{self.port}/byoa/ws", safe="")
                + "&manifest=" + manifest + "&session=" + code)

    async def _page(self, request):
        link = self.new_link()
        if "text.generate" in self.manifest.get("skills", {}):
            manifest = json.dumps(self.manifest, separators=(",", ":"))
            html = ("<!doctype html><meta charset=utf-8><title>Fake text site</title>"
                    "<body style='font:18px Segoe UI;margin:40px'><h1>Fake BYOA text site</h1>"
                    "<p><button id='connect' type='button'>Use my AI</button></p>"
                    "<pre id='result'>no request yet</pre>"
                    f"<script>window.MANIFEST={manifest};window.grant=undefined;"
                    "window.generate=async()=>{window.grant=await window.byoa.request(window.MANIFEST);"
                    "if(!window.grant)return null;window.lastResult=await window.byoa.call(window.grant.grantId,'text.generate',"
                    "{messages:[{role:'user',content:'Answer the question with yes or no: is two plus two four?'}]},"
                    "{choices:['yes','no']});return window.lastResult;};"
                    "document.getElementById('connect').onclick=async()=>{document.getElementById('result').textContent="
                    "JSON.stringify(await window.generate());};</script></body>")
        else:
            html = ("<!doctype html><meta charset=utf-8><title>Fake site</title>"
                    "<body style='font:18px Segoe UI;margin:40px'><h1>Fake BYOA site</h1>"
                    f"<p><a id='go' href='{link}'>Use my AI without the extension</a></p></body>")
        return web.Response(text=html, content_type="text/html")

    async def _manifest(self, request):
        self.manifest_gets += 1
        self.manifest_headers.append(dict(request.headers))
        return web.json_response(self.manifest)

    async def _ws(self, request):
        ws = web.WebSocketResponse(max_msg_size=4 * 1024 * 1024, autoping=True)
        await ws.prepare(request)
        self.upgrades += 1
        try:
            msg = await asyncio.wait_for(ws.receive(), 10)
            if msg.type != WSMsgType.TEXT:
                await ws.close()
                return ws
            hello = json.loads(msg.data)
            self.hellos.append(hello)
            code, pairing = hello.get("session"), hello.get("pairing")
            if hello.get("type") != "hello":
                await ws.close()
                return ws
            if code and self.codes.get(code, (0, None))[0] > time.time():
                _, echo = self.codes.pop(code)                        # single use; the welcome echoes the link's manifest
                token = secrets.token_urlsafe(24)
                self.pairings.add(token)
            elif pairing and pairing in self.pairings:
                token = pairing
                echo = self.manifest64                                # a reconnect: the site's current manifest
            else:
                self.errors_sent += 1
                await ws.send_str(json.dumps({"type": "error", "code": "bad-session"}))
                await ws.close()
                return ws
            await ws.send_str(json.dumps({"type": "welcome", "pairing": token, "manifest": self.echo or echo}))
            self.live = ws
            self._hello_event.set()
            async for msg in ws:
                if msg.type != WSMsgType.TEXT:
                    break
                frame = json.loads(msg.data)
                if frame.get("type") == "result" and frame.get("id") in self._waiting:
                    self._waiting.pop(frame["id"]).set_result(frame)
                elif frame.get("type") == "bye":
                    self.byes += 1
                    break
        finally:
            if self.live is ws:
                self.live = None
            self.closed += 1
        return ws

    # ------------------------------------------------------------------ the test's side
    def wait_hello(self, seconds, count=None):
        """Waits for a welcomed connection (or for <count> hellos in all)."""
        end = time.time() + seconds
        while time.time() < end:
            if (count is None and self.live is not None) or (count is not None and len(self.hellos) >= count):
                return True
            time.sleep(0.05)
        return False

    def call(self, wav_bytes, language, seconds=60):
        return asyncio.run_coroutine_threadsafe(self._call(wav_bytes, language), self._loop).result(seconds)

    async def _call(self, wav_bytes, language):
        self._next_id += 1
        call_id = self._next_id
        future = self._loop.create_future()
        self._waiting[call_id] = future
        started = time.time()
        await self.live.send_str(json.dumps({
            "type": "call", "id": call_id, "skill": "audio.transcribe",
            "input": {"audio": {"mime": "audio/wav", "base64": base64.b64encode(wav_bytes).decode()}},
            "params": {"language": language},
        }))
        result = await asyncio.wait_for(future, 50)
        result["ms"] = int((time.time() - started) * 1000)
        return result

    def call_text(self, choices=("yes", "no"), seconds=120, params=None):
        return asyncio.run_coroutine_threadsafe(self._call_text(choices, params), self._loop).result(seconds)

    async def _call_text(self, choices, extra=None):
        self._next_id += 1
        call_id = self._next_id
        future = self._loop.create_future()
        self._waiting[call_id] = future
        started = time.time()
        await self.live.send_str(json.dumps({
            "type": "call", "id": call_id, "skill": "text.generate",
            "input": {"messages": [{"role": "user", "content": "Answer the question with yes or no: is two plus two four?"}]},
            "params": {"choices": list(choices), **(extra or {})},
        }))
        result = await asyncio.wait_for(future, 100)
        result["ms"] = int((time.time() - started) * 1000)
        return result
