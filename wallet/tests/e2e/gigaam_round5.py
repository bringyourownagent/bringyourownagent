"""Run GigaAM v3 through a direct link and the extension transport.

The script uses only the standard library for the fake WebSocket site. Point --model-folder at a visitor-owned
GigaAM folder in an isolated BYOA_WALLET_HOME; it does not download or alter model files.
"""
import argparse
import base64
import glob
import hashlib
import json
import os
import struct
import subprocess
import sys
import threading
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
try:
    import walletui as ui
except ModuleNotFoundError:
    import fallback_ui as ui
from stdlib_text_site import _Server, _frame, _read_frame

sys.stdout.reconfigure(encoding="utf-8")


class SpeechSite:
    def __init__(self, manifest):
        self.manifest = manifest
        self.port = None
        self.hellos = []
        self.results = []
        self.live = None
        self._event = threading.Event()
        self._server = None
        self._next_id = 0

    @property
    def manifest64(self):
        raw = json.dumps(self.manifest, separators=(",", ":")).encode()
        return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

    def start(self):
        self._server = _Server(("127.0.0.1", 0), self)
        self.port = self._server.server_address[1]
        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        return self

    def page(self, connection):
        connection.close()

    def new_link(self):
        query = urllib.parse.urlencode({
            "v": "0.1",
            "endpoint": f"ws://localhost:{self.port}/byoa/ws",
            "manifest": self.manifest64,
            "session": "gigaam-round5-session-0123456789",
        })
        return "bringyourownagent://connect?" + query

    def websocket(self, connection, key):
        if not key:
            return
        accept = base64.b64encode(hashlib.sha1(
            (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
        connection.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                            f"Sec-WebSocket-Accept: {accept}\r\n\r\n").encode())
        self.live = connection
        try:
            while True:
                opcode, data = _read_frame(connection)
                if opcode is None or opcode == 8:
                    return
                if opcode == 9:
                    connection.sendall(_frame(data, 10))
                    continue
                message = json.loads(data.decode())
                if message.get("type") == "hello":
                    self.hellos.append(message)
                    connection.sendall(_frame(json.dumps({
                        "type": "welcome", "pairing": "gigaam-round5-pairing", "manifest": self.manifest64
                    })))
                elif message.get("type") == "result":
                    self.results.append(message)
                    self._event.set()
        except (ConnectionResetError, BrokenPipeError, OSError):
            return
        finally:
            if self.live is connection:
                self.live = None
            try:
                connection.close()
            except OSError:
                pass

    def wait_hello(self, seconds=45):
        end = time.time() + seconds
        while time.time() < end:
            if self.hellos and self.live is not None:
                return True
            time.sleep(0.05)
        return bool(self.hellos and self.live is not None)

    def call(self, wav, seconds=100):
        if self.live is None:
            raise RuntimeError("the fake site is not connected")
        self._next_id += 1
        self._event.clear()
        started = time.time()
        self.live.sendall(_frame(json.dumps({
            "type": "call", "id": self._next_id, "skill": "audio.transcribe",
            "input": {"audio": {"mime": "audio/wav", "base64": base64.b64encode(wav).decode()}},
            "params": {"language": "ru"},
        })))
        if not self._event.wait(seconds):
            raise TimeoutError("the wallet did not answer the speech call")
        result = dict(self.results[-1])
        result["client_ms"] = int((time.time() - started) * 1000)
        return result


def wait(predicate, seconds):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(0.1)
    return predicate()


def native_read(process):
    header = process.stdout.read(4)
    if len(header) != 4:
        raise RuntimeError("the native host closed before answering")
    length = struct.unpack("<I", header)[0]
    body = process.stdout.read(length)
    if len(body) != length:
        raise RuntimeError("the native host returned a short frame")
    return json.loads(body.decode("utf-8"))


def native_write(process, message):
    body = json.dumps(message, separators=(",", ":")).encode("utf-8")
    process.stdin.write(struct.pack("<I", len(body)) + body)
    process.stdin.flush()


def activity(home):
    result = []
    for path in glob.glob(os.path.join(home, "activity-*.jsonl")):
        result.extend(json.loads(line) for line in open(path, encoding="utf-8") if line.strip())
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", required=True)
    parser.add_argument("--mcp", required=True)
    parser.add_argument("--host", required=True)
    parser.add_argument("--home", required=True)
    parser.add_argument("--model-folder", required=True)
    parser.add_argument("--sample", required=True)
    args = parser.parse_args()
    os.makedirs(args.home, exist_ok=True)
    settings = {"modelFolders": [os.path.abspath(args.model_folder)]}
    with open(os.path.join(args.home, "settings.json"), "w", encoding="utf-8") as stream:
        json.dump(settings, stream)

    env = dict(os.environ, BYOA_WALLET_HOME=os.path.abspath(args.home), BYOA_WALLET_DEV_LOCAL="1",
               AVALONIA_TELEMETRY_OPTOUT="1")
    manifest = {
        "byoa": "0.1-draft",
        "purpose": {"summary": "Recognise one Russian country name",
                     "limits": {"maxClipSeconds": 15, "maxCallsPerMinute": 10}},
        "skills": {"audio.transcribe": {"languages": ["ru"], "required": True}},
        "privacy": "personal-voice",
        "expectedCost": {"perSession": "0.05 USD"},
    }
    wav = open(args.sample, "rb").read()
    wallet = mcp = link_process = host = None
    try:
        wallet = subprocess.Popen([args.app, "--background"], env=env)
        if not wait(lambda: os.path.exists(os.path.join(args.home, "settings.json")), 20):
            raise RuntimeError("the isolated wallet did not start")
        if not wait(lambda: wallet.poll() is None, 2):
            raise RuntimeError("the isolated wallet exited during startup")
        # An existing isolated settings file is not a readiness signal. Give the tray time to create its pipe.
        time.sleep(2)

        mcp = subprocess.Popen([args.mcp], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                                    "params": {"protocolVersion": "2025-06-18",
                                               "clientInfo": {"name": "Round 5 GigaAM E2E", "version": "1"}}}) + "\n")
        mcp.stdin.flush()
        consent = ui.find_window("Connect an AI agent", 15)
        if consent is not None:
            ui.invoke(consent, "AgentAllow")
        init = json.loads(mcp.stdout.readline())
        if "result" not in init:
            raise RuntimeError("MCP initialize failed: " + json.dumps(init))
        mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                                    "params": {"name": "list_downloadable_models", "arguments": {}}}) + "\n")
        mcp.stdin.flush()
        listed = json.loads(mcp.stdout.readline())
        structured = listed.get("result", {}).get("structuredContent") or {}
        if listed.get("result", {}).get("isError"):
            raise RuntimeError("MCP list_downloadable_models failed: " + json.dumps(structured))
        models = structured.get("models", [])
        giga = next((model for model in models if model.get("id") == "gigaam:gigaam-v3"), None)
        if giga is None:
            raise RuntimeError("MCP catalog did not contain GigaAM v3: " + json.dumps(listed))
        print("MCP GigaAM catalog:", json.dumps(giga, ensure_ascii=False))

        site = SpeechSite(manifest).start()
        link_process = subprocess.Popen([args.app, "--url", site.new_link()], env=env)
        notice = ui.find_window("Agent wallet notice", 20)
        if notice is None:
            raise RuntimeError("the direct link did not show a notice")
        ui.invoke(notice, "NoticeShow")
        consent = ui.find_window("Agent wallet consent", 10)
        if consent is None:
            raise RuntimeError("the direct link did not show consent")
        ui.invoke(consent, "ConsentAllow")
        allowed_at = time.time()
        if not site.wait_hello():
            raise RuntimeError("the direct link did not connect")
        print("Direct link preparing-to-hello-ms:", int((time.time() - allowed_at) * 1000))
        print("Direct GigaAM:", json.dumps(site.call(wav), ensure_ascii=False))

        host = subprocess.Popen([args.host], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE)
        origin = "https://gigaam-extension-round5.example"
        native_write(host, {"id": 201, "type": "request", "origin": origin, "userActivation": True,
                            "payload": {"manifest": manifest}})
        notice = ui.find_window("Agent wallet notice", 5)
        if notice is not None:
            ui.invoke(notice, "NoticeShow")
            consent = ui.find_window("Agent wallet consent", 10)
            if consent is None:
                raise RuntimeError("the extension did not show consent")
            ui.invoke(consent, "ConsentAllow")
        grant_frame = native_read(host)
        grant = grant_frame.get("result")
        if not grant or not grant.get("grantId"):
            raise RuntimeError("the extension did not return a grant: " + json.dumps(grant_frame))
        native_write(host, {"id": 202, "type": "call", "origin": origin, "userActivation": False,
                            "payload": {"grantId": grant["grantId"], "skill": "audio.transcribe",
                                        "input": {"audio": {"mime": "audio/wav",
                                                              "base64": base64.b64encode(wav).decode()}},
                                        "params": {"language": "ru"}}})
        print("Extension GigaAM:", json.dumps(native_read(host), ensure_ascii=False))
        print("Speech activity:")
        for entry in [entry for entry in activity(args.home) if entry.get("kind") == "call"]:
            print(json.dumps({"via": entry.get("via") or "extension", "source": entry.get("source"),
                              "ms": entry.get("milliseconds"), "note": entry.get("note")}, ensure_ascii=False))
    finally:
        for process in (host, link_process, mcp, wallet):
            if process is not None and process.poll() is None:
                try:
                    process.terminate()
                    process.wait(timeout=15)
                except Exception:
                    process.kill()
                    process.wait(timeout=5)


if __name__ == "__main__":
    main()
