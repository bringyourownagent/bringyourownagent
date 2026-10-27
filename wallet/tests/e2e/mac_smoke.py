"""macOS smoke test of the real, packaged wallet (python 3 standard library only). Run it inside the desktop session of the person who owns the Mac
(launchctl asuser), on a data folder of its own: nothing of the person's wallet, browsers or Keychain is touched.

    python3 mac_smoke.py --app "<Agent Wallet.app or a folder holding Byoa.Wallet and byoa-host>" --work <a new folder> [--bundle-links]

What it checks, with the real programs:
  1. the wallet starts, stays up, makes its data folder and listens on its socket (a Unix socket in the temp folder, because a data folder of its own);
  2. it tells every browser folder it finds where the native host is (the manifest names the host, allows only the extension's ID) and keeps a copy of the extension;
  3. the real native host (byoa-host), speaking Chrome's framing on stdin and stdout, reaches the wallet: "status" says it is running, "open" is taken;
     the real MCP bridge (byoa-mcp) is recognised by the wallet (the system tells it which program is on the other end of the socket and which
     program started that), and an agent that was approved before can list the tools and call one;
  4. a second wallet process with --url hands the link over and ends; a second plain start ends at once (one wallet per data folder);
  5. with --bundle-links: `open -a <app> bringyourownagent://...` reaches the running wallet as a macOS event (the wallet records the link);
  6. after a crash (kill -9) the socket file is stale, and a new wallet still starts and is reached by the host again;
  7. SIGTERM ends the wallet and takes its socket away.
"""
import argparse
import ctypes
import hashlib
import json
import os
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import time

ap = argparse.ArgumentParser()
ap.add_argument("--app", required=True)
ap.add_argument("--work", required=True, help="a new folder for this run")
ap.add_argument("--bundle-links", action="store_true", help="also test links delivered by macOS (needs the .app)")
a = ap.parse_args()

if os.path.exists(a.work):
    sys.exit("the work folder exists: choose a new one (this test deletes nothing)")
os.makedirs(a.work)
HOME = os.path.join(a.work, "home")
SUPPORT = os.path.join(a.work, "support")
os.makedirs(os.path.join(SUPPORT, "Google", "Chrome"))
os.makedirs(os.path.join(SUPPORT, "BraveSoftware", "Brave-Browser"))
if a.app.endswith(".app"):
    MACOS = os.path.join(a.app, "Contents", "MacOS")
    BUNDLE = a.app
else:
    MACOS = a.app
    BUNDLE = None
WALLET = os.path.join(MACOS, "Byoa.Wallet")
HOST = os.path.join(MACOS, "byoa-host")

def proc_path(pid):
    """The image path the system reports for a process (what the wallet sees for the program that starts the MCP bridge)."""
    libc = ctypes.CDLL("/usr/lib/libSystem.B.dylib")
    buf = ctypes.create_string_buffer(4096)
    n = libc.proc_pidpath(pid, buf, 4096)
    return buf.raw[:n].decode() if n > 0 else None


def agent_id(path):
    """AgentIdentityFactory: the first 12 bytes of the SHA-256 of the upper-cased parent path, in lower-case hex."""
    return hashlib.sha256(path.upper().encode()).digest()[:12].hex()


MY_PATH = proc_path(os.getpid())
os.makedirs(HOME)
with open(os.path.join(HOME, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "agents": [{"id": agent_id(MY_PATH), "name": "smoke agent", "executablePath": MY_PATH, "status": "allowed", "fullAccess": False}]}, f)

env = dict(os.environ, BYOA_WALLET_HOME=HOME, BYOA_NATIVE_HOST_ROOT=SUPPORT, AVALONIA_TELEMETRY_OPTOUT="1")
failed = []
children = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def socket_path():
    """The wallet's socket for a data folder of its own: <temp>/byoa-<10 hex of the folder's hash>-wallet.sock (see WalletPaths.SocketPath)."""
    tag = hashlib.sha256(HOME.rstrip("/").lower().encode()).hexdigest()[:16][:10]
    tmp = env.get("TMPDIR") or "/tmp/"
    return os.path.join(tmp, f"byoa-{tag}-wallet.sock")


def start_wallet(extra=()):
    log = open(os.path.join(a.work, f"wallet-{len(children)}.out"), "wb")
    p = subprocess.Popen([WALLET, "--background", *extra], env=env, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
    children.append(p)
    return p


def wait_for(predicate, seconds, step=0.25):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(step)
    return False


def frame(obj):
    body = json.dumps(obj).encode()
    return struct.pack("<I", len(body)) + body


def read_frame(stream, timeout):
    end = time.time() + timeout
    data = b""
    while len(data) < 4 and time.time() < end:
        chunk = stream.read(4 - len(data))
        if not chunk:
            return None
        data += chunk
    if len(data) < 4:
        return None
    (length,) = struct.unpack("<I", data)
    body = b""
    while len(body) < length:
        chunk = stream.read(length - len(body))
        if not chunk:
            return None
        body += chunk
    return json.loads(body)


def host_ask(message, timeout=20):
    """One message through the real native host, as the browser does it: Chrome's framing on stdin and stdout."""
    p = subprocess.Popen([HOST, "chrome-extension://niobopdoiopjilmlonjcgllpmgjemhpk/"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        p.stdin.write(frame(message))
        p.stdin.flush()
        answer = read_frame(p.stdout, timeout)
    finally:
        try:
            p.stdin.close()
        except OSError:
            pass
        try:
            p.wait(10)
        except subprocess.TimeoutExpired:
            p.kill()
    return answer


def activity_lines():
    out = []
    if os.path.isdir(HOME):
        for name in sorted(os.listdir(HOME)):
            if name.startswith("activity-") and name.endswith(".jsonl"):
                with open(os.path.join(HOME, name), encoding="utf-8") as f:
                    out += [json.loads(l) for l in f if l.strip()]
    return out


try:
    # ------------------------------------------------------------------------------------------------- 1. start
    check("the wallet program and the host are there", os.path.isfile(WALLET) and os.path.isfile(HOST), MACOS)
    wallet = start_wallet()
    sock = socket_path()
    check("the wallet listens on its socket", wait_for(lambda: os.path.exists(sock), 30), sock)
    time.sleep(4)
    check("the wallet is still running after 4 s", wallet.poll() is None, str(wallet.poll()))
    check("settings.json and the lock file were made", all(os.path.isfile(os.path.join(HOME, n)) for n in ("settings.json", "instance.lock")))
    check("no errors.log", not os.path.exists(os.path.join(HOME, "errors.log")), open(os.path.join(HOME, "errors.log")).read()[:300] if os.path.exists(os.path.join(HOME, "errors.log")) else "")
    if os.path.exists(sock):
        check("the socket is closed to other users", (os.stat(sock).st_mode & 0o022) == 0, oct(os.stat(sock).st_mode))

    # ------------------------------------------------------------------------------------------------- 2. browsers
    for browser in (os.path.join("Google", "Chrome"), os.path.join("BraveSoftware", "Brave-Browser")):
        manifest = os.path.join(SUPPORT, browser, "NativeMessagingHosts", "org.bringyourownagent.wallet.json")
        ok = os.path.isfile(manifest)
        data = json.load(open(manifest)) if ok else {}
        check(f"the host manifest for {browser}", ok and data.get("path") == HOST and data.get("allowed_origins") == ["chrome-extension://niobopdoiopjilmlonjcgllpmgjemhpk/"], str(data.get("path")))
    check("no manifest for a browser that is not installed", not os.path.exists(os.path.join(SUPPORT, "Microsoft Edge")))
    check("the extension copy is where Load unpacked can find it", os.path.isfile(os.path.join(SUPPORT, "BYOA", "extension", "manifest.json")))

    # ------------------------------------------------------------------------------------------------- 3. the real host
    status = host_ask({"id": 1, "type": "status", "origin": "", "payload": {}})
    check("the host reaches the wallet: status says it runs", bool(status) and (status.get("result") or {}).get("running") is True, json.dumps(status))
    opened = host_ask({"id": 2, "type": "open", "origin": "", "payload": {}})
    check("the host's 'open' is answered by the wallet", opened is not None and opened.get("id") == 2, json.dumps(opened))

    # ------------------------------------------------------------------------------------------------- 3b. the real MCP bridge
    MCP = os.path.join(MACOS, "byoa-mcp")
    bridge = subprocess.Popen([MCP], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    children.append(bridge)

    def rpc(message):
        bridge.stdin.write(json.dumps(message) + "\n")
        bridge.stdin.flush()
        return json.loads(bridge.stdout.readline())

    init = rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "smoke agent", "version": "1"}}})
    check("the MCP bridge answers initialize", "result" in init and "serverInfo" in init["result"], json.dumps(init)[:200])
    bridge.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    bridge.stdin.flush()
    tools = rpc({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    names = [t["name"] for t in tools.get("result", {}).get("tools", [])]
    check("it lists the wallet's tools", "status" in names and "help" in names, ", ".join(names))
    called = rpc({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "status", "arguments": {}}})
    check("an approved agent calls a tool and the wallet answers (it recognised the bridge and its parent)", "result" in called and not called["result"].get("isError", False), json.dumps(called)[:300])
    bridge.stdin.close()
    try:
        bridge.wait(10)
    except subprocess.TimeoutExpired:
        bridge.kill()

    # ------------------------------------------------------------------------------------------------- 4. second processes
    before = len(activity_lines())
    t = time.time()
    second = subprocess.run([WALLET, "--url", "bringyourownagent://not-a-valid-link"], env=env, capture_output=True, timeout=30)
    check("a second wallet with --url hands the link over and ends", second.returncode == 0 and time.time() - t < 15, f"exit {second.returncode} in {time.time() - t:.1f} s")
    check("the running wallet recorded the link as invalid", wait_for(lambda: any(e.get("outcome") == "invalid" for e in activity_lines()[before:]), 10), str(activity_lines()[before:]))
    t = time.time()
    plain = subprocess.run([WALLET], env=env, capture_output=True, timeout=30)
    check("a second plain start ends at once (one wallet per data folder)", plain.returncode == 0 and time.time() - t < 15, f"exit {plain.returncode} in {time.time() - t:.1f} s")
    check("the first wallet is still the only one", wallet.poll() is None)

    # ------------------------------------------------------------------------------------------------- 5. links from macOS
    if a.bundle_links and BUNDLE:
        before = len(activity_lines())
        r = subprocess.run(["/usr/bin/open", "-a", BUNDLE, "bringyourownagent://a-link-through-macos"], env=env, capture_output=True, timeout=30)
        check("macOS delivered a bringyourownagent:// link to the running wallet", r.returncode == 0 and wait_for(lambda: any(e.get("outcome") == "invalid" for e in activity_lines()[before:]), 15),
              (r.stderr or b"").decode()[:200] + " " + str(activity_lines()[before:]))
        check("and it was not a second process", wallet.poll() is None)

    # ------------------------------------------------------------------------------------------------- 6. a crash leaves a stale socket
    wallet.kill()
    wallet.wait(10)
    check("after kill -9 the socket file is left behind", os.path.exists(sock))
    wallet2 = start_wallet()
    check("a new wallet starts over the stale socket", wait_for(lambda: wallet2.poll() is not None or host_ask({"id": 3, "type": "status", "origin": "", "payload": {}}, 5) is not None, 30) and wallet2.poll() is None)
    status = host_ask({"id": 4, "type": "status", "origin": "", "payload": {}})
    check("and the host reaches it", bool(status) and (status.get("result") or {}).get("running") is True, json.dumps(status))

    # ------------------------------------------------------------------------------------------------- 7. a clean stop
    wallet2.send_signal(signal.SIGTERM)
    try:
        wallet2.wait(20)
    except subprocess.TimeoutExpired:
        pass
    check("SIGTERM ends the wallet", wallet2.poll() is not None, str(wallet2.poll()))
    check("and its socket is gone", wait_for(lambda: not os.path.exists(sock), 5))
    status = host_ask({"id": 5, "type": "status", "origin": "", "payload": {}}, 15)
    check("the host then says the wallet is not running (and starts nothing for a status)", bool(status) and (status.get("result") or {}).get("running") is False, json.dumps(status))
finally:
    for p in children:
        if p.poll() is None:
            p.kill()

print("\nALL OK" if not failed else f"\n{len(failed)} FAILED: " + "; ".join(failed))
sys.exit(1 if failed else 0)
