"""Round-three text.generate integration run.

This is deliberately separate from the speech E2E: it uses the real tray wallet, the real MCP stdio executable,
LM Studio on its local port, a direct-link fake site, and a Chromium extension fake site.  It writes only screenshots
and the isolated wallet home below the supplied work folder.
"""
import argparse
import glob
import json
import os
import struct
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
try:
    import walletui as ui  # noqa: E402
except ModuleNotFoundError:
    import fallback_ui as ui  # noqa: E402
try:
    from fake_site import FakeSite  # noqa: E402
except ModuleNotFoundError:
    from stdlib_text_site import FakeSite  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--app", required=True)
ap.add_argument("--mcp", required=True)
ap.add_argument("--work", required=True)
ap.add_argument("--shots", required=True)
a = ap.parse_args()

os.makedirs(a.work, exist_ok=True)
os.makedirs(a.shots, exist_ok=True)
home = os.path.join(a.work, "home")
env = dict(os.environ, BYOA_WALLET_HOME=home, BYOA_WALLET_DEV_LOCAL="1")
wallet = None
mcp = None
link_process = None
host_process = None

TEXT_MANIFEST = {
    "byoa": "0.1-draft",
    "purpose": {"summary": "Answer a small yes or no question", "limits": {"maxTextChars": 20000, "maxCallsPerMinute": 10}},
    "skills": {"text.generate": {"required": True}},
    "privacy": "personal-voice",
    "expectedCost": {"perSession": "0.05 USD"},
}


def wait(predicate, seconds=30):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(0.1)
    return predicate()


def mcp_send(method, params=None, ident=None):
    mcp_send.counter += 1
    ident = mcp_send.counter if ident is None else ident
    mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": ident, "method": method, "params": params or {}}) + "\n")
    mcp.stdin.flush()
    while True:
        line = mcp.stdout.readline()
        if not line:
            raise RuntimeError("the MCP process closed stdout")
        response = json.loads(line)
        if response.get("id") == ident:
            return response


mcp_send.counter = 0


def tool(name, arguments=None):
    response = mcp_send("tools/call", {"name": name, "arguments": arguments or {}})
    result = response["result"]
    structured = result.get("structuredContent", {})
    if result.get("isError"):
        raise RuntimeError(structured.get("error") or result.get("content", [{}])[0].get("text", "tool failed"))
    return structured


def activity():
    entries = []
    for path in glob.glob(os.path.join(home, "activity-*.jsonl")):
        with open(path, encoding="utf-8") as stream:
            entries.extend(json.loads(line) for line in stream if line.strip())
    return entries


def shot(window, path):
    try:
        ui.shot(window, path)
        print("screenshot:", path)
    except OSError as error:
        # A non-interactive sandbox may expose UI Automation but not a desktop framebuffer. Keep the E2E useful;
        # the headed owner run uses the same call and saves the real screenshot.
        print("screenshot unavailable in this session:", error)


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


def native_send(process, message):
    native_write(process, message)
    return native_read(process)


try:
    wallet = subprocess.Popen([a.app, "--background"], env=env)
    if not wait(lambda: os.path.exists(os.path.join(home, "settings.json")), 15):
        raise RuntimeError("the isolated wallet did not create settings.json")

    mcp = subprocess.Popen([a.mcp], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, encoding="utf-8", bufsize=1)
    mcp.stdin.write(json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2025-06-18", "clientInfo": {"name": "Round 3 local agent", "version": "1"}},
    }) + "\n")
    mcp.stdin.flush()
    consent = ui.find_window("Connect an AI agent", 15)
    if consent is None:
        raise RuntimeError("the wallet did not show the first-agent consent")
    shot(consent, os.path.join(a.shots, "agent-consent-round3.png"))
    ui.click(consent, "AgentAllow")
    init = json.loads(mcp.stdout.readline())
    if "result" not in init:
        raise RuntimeError("MCP initialize failed: " + json.dumps(init))

    listed = mcp_send("tools/list")
    help_text = tool("help")["message"]
    runtimes = tool("list_runtimes")
    runtime = next((r for r in runtimes["runtimes"] if r["kind"] == "lmstudio" and r["running"]), None)
    if runtime is None or not any(m["id"] == "qwen/qwen3.5-4b" for m in runtime["models"]):
        raise RuntimeError("LM Studio qwen/qwen3.5-4b was not found by list_runtimes: " + json.dumps(runtimes))
    print("MCP tools:", len(listed["result"]["tools"]))
    print("MCP help:", help_text.splitlines()[0])
    print("LM Studio:", json.dumps(runtime, ensure_ascii=False))

    added = tool("add_source", {"kind": "lmstudio", "model": "qwen/qwen3.5-4b",
                                 "endpoint": "http://127.0.0.1:1234/v1", "skills": ["text.generate"]})
    source_id = added["id"]
    tested = tool("test_source", {"source_id": source_id})
    print("MCP test_source:", json.dumps(tested, ensure_ascii=False))
    if not tested.get("ok"):
        raise RuntimeError("the real LM Studio source test failed: " + json.dumps(tested))
    ordered = tool("set_order", {"skill": "text.generate", "source_ids": [source_id]})
    print("MCP set_order:", ordered.get("message"))

    link_site = FakeSite("text-link", TEXT_MANIFEST).start()
    link_process = subprocess.Popen([a.app, "--url", link_site.new_link()], env=env)
    notice = ui.find_window("Agent wallet notice", 20)
    if notice is None:
        raise RuntimeError("the direct text link did not show a notice")
    ui.invoke(notice, "NoticeShow")
    consent = ui.find_window("Agent wallet consent", 10)
    if consent is None:
        raise RuntimeError("the direct text link did not show consent")
    shot(consent, os.path.join(a.shots, "consent-text-link-round3.png"))
    ui.invoke(consent, "ConsentAllow")
    if not link_site.wait_hello(45):
        raise RuntimeError("the direct text link did not connect")
    link_call = link_site.call_text()
    print("Direct link answer:", json.dumps(link_call, ensure_ascii=False))
    if not link_call.get("ok") or link_call.get("output", {}).get("text") not in ("yes", "no"):
        raise RuntimeError("the direct text call did not return yes/no")
    link_process.wait(timeout=10)

    extension_site = FakeSite("text-extension", TEXT_MANIFEST).start()
    try:
        from patchright.sync_api import sync_playwright
    except ModuleNotFoundError as error:
        # A clean Windows checkout may not have the optional browser-driving package. Exercise the real native
        # messaging host and wallet pipe directly instead; the frame is exactly what bridge.js sends to Chrome.
        print("Browser automation not installed; exercising the real extension transport through byoa-host.exe:", error)
        host_path = os.path.abspath(os.path.join(HERE, "..", "..", "host", "Byoa.Wallet.Host", "bin", "Debug", "net10.0", "win-x64", "byoa-host.exe"))
        if not os.path.exists(host_path):
            raise RuntimeError("the native host was not built: " + host_path)
        host_process = subprocess.Popen([host_path], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        extension_origin = "https://text-extension.example"
        request = {
            "id": 101, "type": "request", "origin": extension_origin, "userActivation": True,
            "payload": {"manifest": TEXT_MANIFEST},
        }
        native_write(host_process, request)
        notice = ui.find_window("Agent wallet notice", 20)
        if notice is None:
            raise RuntimeError("the extension text request did not show a notice")
        ui.invoke(notice, "NoticeShow")
        consent = ui.find_window("Agent wallet consent", 10)
        if consent is None:
            raise RuntimeError("the extension text request did not show consent")
        shot(consent, os.path.join(a.shots, "consent-text-extension-round3.png"))
        ui.invoke(consent, "ConsentAllow")
        extension_result = native_read(host_process)
        grant = extension_result.get("result")
        if not grant or not grant.get("grantId"):
            raise RuntimeError("the extension request did not return a grant: " + json.dumps(extension_result))
        extension_call = native_send(host_process, {
            "id": 102, "type": "call", "origin": extension_origin, "userActivation": False,
            "payload": {"grantId": grant["grantId"], "skill": "text.generate",
                         "input": {"messages": [{"role": "user", "content": "Answer the question with yes or no: is two plus two four?"}]},
                         "params": {"choices": ["yes", "no"]}},
        }).get("result")
        print("Extension answer:", json.dumps(extension_call, ensure_ascii=False))
        if not extension_call.get("ok") or extension_call.get("output", {}).get("text") not in ("yes", "no"):
            raise RuntimeError("the extension text call did not return yes/no")
    else:
        with sync_playwright() as playwright:
            context = playwright.chromium.launch_persistent_context(
                os.path.join(a.work, "browser-profile"), channel="chromium", headless=True, env=env,
                viewport={"width": 1000, "height": 700},
                args=["--disable-extensions-except=" + os.path.abspath(os.path.join(HERE, "..", "..", "extension")),
                      "--load-extension=" + os.path.abspath(os.path.join(HERE, "..", "..", "extension")),
                      "--disable-features=LocalNetworkAccessChecks"],
            )
            page = context.new_page()
            page.goto(extension_site.origin + "/")
            page.wait_for_function("window.byoa !== undefined", timeout=15000)
            page.click("#connect")
            notice = ui.find_window("Agent wallet notice", 20)
            if notice is None:
                raise RuntimeError("the extension text request did not show a notice")
            ui.invoke(notice, "NoticeShow")
            consent = ui.find_window("Agent wallet consent", 10)
            if consent is None:
                raise RuntimeError("the extension text request did not show consent")
            shot(consent, os.path.join(a.shots, "consent-text-extension-round3.png"))
            ui.invoke(consent, "ConsentAllow")
            if not wait(lambda: page.evaluate("window.lastResult !== undefined"), 100):
                raise RuntimeError("the extension text call did not finish")
            extension_call = page.evaluate("window.lastResult")
            print("Extension answer:", json.dumps(extension_call, ensure_ascii=False))
            if not extension_call.get("ok") or extension_call.get("output", {}).get("text") not in ("yes", "no"):
                raise RuntimeError("the extension text call did not return yes/no")
            context.close()

    calls = [entry for entry in activity() if entry.get("kind") == "call" and entry.get("skill") == "text.generate"]
    print("Text activity:")
    for entry in calls:
        print(json.dumps({"via": entry.get("via") or "extension", "answer": entry.get("note"),
                          "source": entry.get("source"), "ms": entry.get("milliseconds")}, ensure_ascii=False))
    if not any(entry.get("via") == "link" for entry in calls) or not any(not entry.get("via") for entry in calls):
        raise RuntimeError("activity did not contain both link and extension text calls")
finally:
    if host_process is not None and host_process.poll() is None:
        host_process.terminate()
        host_process.wait(timeout=10)
    if link_process is not None and link_process.poll() is None:
        link_process.terminate()
        link_process.wait(timeout=10)
    if mcp is not None:
        try:
            mcp.stdin.close()
        except Exception:
            pass
        if mcp.poll() is None:
            mcp.terminate()
            mcp.wait(timeout=10)
    if wallet is not None and wallet.poll() is None:
        wallet.terminate()
        wallet.wait(timeout=15)
