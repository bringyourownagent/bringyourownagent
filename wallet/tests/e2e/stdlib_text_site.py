"""A dependency-free fake text site for the isolated round-three run.

It uses only the Python standard library so the MCP/direct-link part can run on a clean Windows test machine.  The
wire protocol is the small subset of WebSocket needed by the direct transport and the extension's fake page.
"""
import base64
import hashlib
import json
import socket
import socketserver
import threading
import time
import urllib.parse

_ports = iter(range(5301, 5321))


def _frame(payload, opcode=1):
    data = payload.encode("utf-8") if isinstance(payload, str) else payload
    size = len(data)
    if size < 126:
        header = bytes([0x80 | opcode, size])
    elif size < 65536:
        header = bytes([0x80 | opcode, 126]) + size.to_bytes(2, "big")
    else:
        header = bytes([0x80 | opcode, 127]) + size.to_bytes(8, "big")
    return header + data


def _read_frame(stream):
    header = stream.recv(2)
    if len(header) < 2:
        return None, None
    length = header[1] & 0x7F
    if length == 126:
        length = int.from_bytes(stream.recv(2), "big")
    elif length == 127:
        length = int.from_bytes(stream.recv(8), "big")
    mask = stream.recv(4) if header[1] & 0x80 else b""
    data = bytearray()
    while len(data) < length:
        part = stream.recv(length - len(data))
        if not part:
            return None, None
        data.extend(part)
    if mask:
        data = bytearray(value ^ mask[index % 4] for index, value in enumerate(data))
    return header[0] & 0x0F, bytes(data)


class _Handler(socketserver.StreamRequestHandler):
    def handle(self):
        request = b""
        while b"\r\n\r\n" not in request:
            part = self.connection.recv(4096)
            if not part:
                return
            request += part
        first, *headers = request.decode("latin1").split("\r\n")
        values = {}
        for line in headers:
            if ":" in line:
                key, value = line.split(":", 1)
                values[key.lower().strip()] = value.strip()
        path = urllib.parse.urlsplit(first.split(" ")[1]).path
        if path == "/byoa/ws":
            self.server.site.websocket(self.connection, values.get("sec-websocket-key"))
            return
        self.server.site.page(self.connection)


class _Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, address, site):
        self.site = site
        super().__init__(address, _Handler)


class FakeSite:
    def __init__(self, name, manifest):
        self.name = name
        self.manifest = manifest
        self.port = None
        self.hellos = []
        self.calls = []
        self.live = None
        self._call_event = threading.Event()
        self._server = None

    @property
    def origin(self):
        return f"http://localhost:{self.port}"

    @property
    def manifest64(self):
        raw = json.dumps(self.manifest, separators=(",", ":")).encode()
        return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

    def start(self):
        try:
            port = next(_ports)
            self._server = _Server(("127.0.0.1", port), self)
        except OSError:
            self._server = _Server(("127.0.0.1", 0), self)
        self.port = self._server.server_address[1]
        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        return self

    def page(self, connection):
        manifest = json.dumps(self.manifest, separators=(",", ":"))
        body = ("<!doctype html><meta charset=utf-8><title>Fake text site</title>"
                "<button id='connect'>Use my AI</button><pre id='result'></pre>"
                f"<script>window.MANIFEST={manifest};window.generate=async()=>{{window.grant=await window.byoa.request(window.MANIFEST);"
                "if(!window.grant)return null;window.lastResult=await window.byoa.call(window.grant.grantId,'text.generate',"
                "{messages:[{role:'user',content:'Answer the question with yes or no: is two plus two four?'}]},"
                "{choices:['yes','no']});return window.lastResult;};"
                "connect.onclick=async()=>result.textContent=JSON.stringify(await generate());</script>").encode()
        connection.sendall((f"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {len(body)}\r\n"
                            "Connection: close\r\n\r\n").encode() + body)

    def new_link(self):
        query = urllib.parse.urlencode({"v": "0.1", "endpoint": f"ws://localhost:{self.port}/byoa/ws",
                                         "manifest": self.manifest64, "session": "round3-test-session-0123456789"})
        return "bringyourownagent://connect?" + query

    def websocket(self, connection, key):
        if not key:
            return
        accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
        connection.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                             f"Sec-WebSocket-Accept: {accept}\r\n\r\n").encode())
        self.live = connection
        try:
            while True:
                opcode, data = _read_frame(connection)
                if opcode is None:
                    return
                if opcode == 8:
                    return
                if opcode == 9:
                    connection.sendall(_frame(data, 10))
                    continue
                message = json.loads(data.decode())
                if message.get("type") == "hello":
                    self.hellos.append(message)
                    connection.sendall(_frame(json.dumps({"type": "welcome", "pairing": "round3-pairing",
                                                           "manifest": self.manifest64})))
                elif message.get("type") == "call":
                    result = {"type": "result", "id": message.get("id"), "ok": True,
                              "output": {"text": "yes"}, "receipt": {"cost": "0.000 USD", "provider": "local"}}
                    connection.sendall(_frame(json.dumps(result)))
                elif message.get("type") == "result":
                    self.calls.append((message, message))
                    self._call_event.set()
        except (ConnectionResetError, BrokenPipeError, OSError):
            # The wallet may close its socket immediately after the scripted call has finished.
            return
        finally:
            if self.live is connection:
                self.live = None
            try:
                connection.close()
            except OSError:
                pass

    def wait_hello(self, seconds=30):
        end = time.time() + seconds
        while time.time() < end:
            if self.hellos and self.live is not None:
                return True
            time.sleep(0.05)
        return bool(self.hellos and self.live is not None)

    def call_text(self):
        if self.live is None:
            raise RuntimeError("site is not connected")
        # The direct caller uses the same framing as the wallet site would use.
        ident = len(self.calls) + 1
        self._call_event.clear()
        self.live.sendall(_frame(json.dumps({"type": "call", "id": ident, "skill": "text.generate",
                                             "input": {"messages": [{"role": "user", "content": "is two plus two four?"}]},
                                             "params": {"choices": ["yes", "no"]}})))
        started = time.time()
        if not self._call_event.wait(30):
            raise TimeoutError("the wallet did not answer the text call")
        _, result = self.calls[-1]
        result = dict(result)
        result["ms"] = int((time.time() - started) * 1000)
        return result
