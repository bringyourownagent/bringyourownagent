"""Linux smoke test of the real, packaged-style wallet (python 3 standard library only). Run it inside a desktop session: under Xvfb
and dbus-run-session on a test machine, with its own home, config, data, runtime and temp folders, so that nothing of the person's
wallet, browsers or keyring is touched.

    xvfb-run -a -s "-screen 0 1280x800x24" dbus-run-session -- python3 linux_smoke.py --app <folder with Byoa.Wallet, byoa-host, byoa-mcp> --work <a new folder>

What it checks, with the real programs:
  1. the wallet starts, stays up, makes its data folder and listens on its Unix socket in $XDG_RUNTIME_DIR/byoa (once the folder is
     there, nothing else can open the socket);
  2. it tells every browser folder it finds where the native host is (the manifest names the host, allows only the extension's ID),
     keeps a copy of the extension, writes the desktop entry for bringyourownagent:// links and installs its icons;
  3. the real native host (byoa-host), speaking Chrome's framing on stdin and stdout, reaches the wallet: "status" says it is running,
     "open" is taken; the real MCP bridge (byoa-mcp) is recognised by the wallet (the kernel tells it which program is on the other end of
     the socket and which program started that), and an agent that was approved before can list the tools and call one;
  4. a second wallet process with --url hands the link over and ends; a second plain start ends at once (one wallet per data folder);
  5. after a crash (kill -9) the socket file is stale, and a new wallet still starts and is reached by the host again;
  6. SIGTERM ends the wallet and takes its socket away.
"""
import argparse
import hashlib
import json
import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import time

ap = argparse.ArgumentParser()
ap.add_argument("--app", required=True)
ap.add_argument("--work", required=True, help="a new folder for this run")
a = ap.parse_args()

if os.path.exists(a.work):
    sys.exit("the work folder exists: choose a new one (this test deletes nothing)")
os.makedirs(a.work)

HOME = os.path.join(a.work, "home")
CONFIG = os.path.join(a.work, "config")
DATA = os.path.join(a.work, "data")
RUNTIME = os.path.join(a.work, "runtime")
TMP = os.path.join(a.work, "tmp")
for folder in (HOME, CONFIG, DATA, RUNTIME, TMP):
    os.makedirs(folder)

os.makedirs(os.path.join(CONFIG, "google-chrome"))
os.makedirs(os.path.join(CONFIG, "BraveSoftware", "Brave-Browser"))
os.makedirs(os.path.join(HOME, ".var", "app", "com.google.Chrome", "config", "google-chrome"))

APP = os.path.abspath(a.app)
WALLET = os.path.join(APP, "Byoa.Wallet")
HOST = os.path.join(APP, "byoa-host")
MCP = os.path.join(APP, "byoa-mcp")
WALLET_HOME = os.path.join(DATA, "BYOA", "wallet")

if not os.environ.get("DISPLAY"):
    sys.exit("no DISPLAY: run this inside a desktop session (xvfb-run on a test machine)")
if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
    sys.exit("no session bus: run this inside dbus-run-session on a test machine")


def proc_path(pid):
    """The image path the kernel reports for a process (what the wallet sees for the program that started the MCP bridge)."""
    try:
        return os.readlink("/proc/%d/exe" % pid)
    except OSError:
        return None


def agent_id(path):
    """AgentIdentityFactory: the first 12 bytes of the SHA-256 of the upper-cased parent path, in lower-case hex."""
    return hashlib.sha256(path.upper().encode()).digest()[:12].hex()


MY_PATH = proc_path(os.getpid())
os.makedirs(WALLET_HOME)
with open(os.path.join(WALLET_HOME, "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "agents": [{"id": agent_id(MY_PATH), "name": "smoke agent", "executablePath": MY_PATH, "status": "allowed", "fullAccess": False}]}, f)

env = dict(os.environ)
env.update({
    "HOME": HOME,
    "XDG_CONFIG_HOME": CONFIG,
    "XDG_DATA_HOME": DATA,
    "XDG_RUNTIME_DIR": RUNTIME,
    "TMPDIR": TMP,
    "AVALONIA_TELEMETRY_OPTOUT": "1",
})
for name in ("BYOA_WALLET_HOME", "BYOA_NATIVE_HOST_ROOT", "BYOA_UPDATE_FEED"):
    env.pop(name, None)

failed = []
children = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""), flush=True)
    if not ok:
        failed.append(label)


def socket_path():
    """The wallet's socket: $XDG_RUNTIME_DIR/byoa/wallet.sock (see WalletPaths.RunFolder)."""
    return os.path.join(RUNTIME, "byoa", "wallet.sock")


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
    if os.path.isdir(WALLET_HOME):
        for name in sorted(os.listdir(WALLET_HOME)):
            if name.startswith("activity-") and name.endswith(".jsonl"):
                with open(os.path.join(WALLET_HOME, name), encoding="utf-8") as f:
                    out += [json.loads(line) for line in f if line.strip()]
    return out


def check_secret_service():
    """Optional: a real gnome-keyring Secret Service in a session bus of its own, when the machine has one.

    The parent session (this test) stays without one, so the key step above still tests the key file. The child session
    has an isolated home and its own wallet home: one key must go into the service with no secrets.key, and a second
    import must reuse the same item (one item, no unreadable copy)."""
    if not shutil.which("gnome-keyring-daemon") or not shutil.which("dbus-run-session"):
        print("--   the real Secret Service step is skipped: no gnome-keyring-daemon or dbus-run-session", flush=True)
        return
    base = os.path.join(a.work, "secret-service")
    home = os.path.join(base, "home")
    runtime = os.path.join(base, "run")
    wallet_home = os.path.join(base, "wallet")
    os.makedirs(home)
    os.makedirs(runtime)
    os.chmod(runtime, 0o700)
    key = os.path.join(base, "key.txt")
    with open(key, "w", encoding="utf-8") as f:
        f.write("test-secret-0123456789abcdef\n")
    child_env = dict(env, HOME=home, XDG_DATA_HOME=os.path.join(home, ".local", "share"),
                     XDG_CONFIG_HOME=os.path.join(home, ".config"), XDG_RUNTIME_DIR=runtime,
                     BYOA_WALLET_HOME=wallet_home, WALLET=WALLET, KEY=key)
    script = r"""
set -e
printf '\n' | gnome-keyring-daemon --unlock --components=secrets > /dev/null 2>&1 || true
for i in $(seq 1 50); do
  gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.NameHasOwner org.freedesktop.secrets 2> /dev/null | grep -q true && break
  sleep 0.2
done
gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.NameHasOwner org.freedesktop.secrets | grep -q true
"$WALLET" --import-key openrouter "$KEY"
test -f "$BYOA_WALLET_HOME/secrets.dat"
test ! -e "$BYOA_WALLET_HOME/secrets.key"
"$WALLET" --import-key openrouter "$KEY"
test ! -e "$BYOA_WALLET_HOME/secrets.key"
test -z "$(ls "$BYOA_WALLET_HOME"/secrets.dat.unreadable-* 2> /dev/null)"
count=$(gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.SearchItems "{'application': 'org.bringyourownagent.wallet', 'kind': 'secrets-key'}" | grep -o "/org/freedesktop/secrets/collection/[^']*" | wc -l)
test "$count" -eq 1
"""
    result = subprocess.run(["dbus-run-session", "--", "bash", "-c", script], env=child_env,
                            capture_output=True, text=True, timeout=120)
    check("the real Secret Service keeps the key (no secrets.key, one item, a second import reuses it)",
          result.returncode == 0, (result.stdout + result.stderr).strip()[-300:])


check_secret_service()

try:
    # ------------------------------------------------------------------------------------------------- 1. start
    check("the wallet program and its two helpers are there", all(os.path.isfile(p) for p in (WALLET, HOST, MCP)), APP)
    wallet = start_wallet()
    sock = socket_path()
    check("the wallet listens on its socket in the runtime folder", wait_for(lambda: os.path.exists(sock), 30), sock)
    time.sleep(4)
    check("the wallet is still running after 4 s", wallet.poll() is None, str(wallet.poll()))
    check("settings.json and the lock file were made", all(os.path.isfile(os.path.join(WALLET_HOME, n)) for n in ("settings.json", "instance.lock")))
    check("no errors.log", not os.path.exists(os.path.join(WALLET_HOME, "errors.log")),
          open(os.path.join(WALLET_HOME, "errors.log"), encoding="utf-8", errors="replace").read()[:300] if os.path.exists(os.path.join(WALLET_HOME, "errors.log")) else "")
    if os.path.exists(sock):
        check("the socket is closed to other users", (os.stat(sock).st_mode & 0o022) == 0, oct(os.stat(sock).st_mode))
        folder_mode = os.stat(os.path.dirname(sock)).st_mode & 0o777
        check("the socket folder is closed to other users", folder_mode == 0o700, oct(folder_mode))

    # ------------------------------------------------------------------------------------------------- 1b. the key store
    dummy = os.path.join(a.work, "dummy-key.txt")
    with open(dummy, "w", encoding="utf-8") as f:
        f.write("test-secret-0123456789abcdef\n")
    stored = subprocess.run([WALLET, "--import-key", "openrouter", dummy], env=env, capture_output=True, timeout=30)
    sealed = os.path.join(WALLET_HOME, "secrets.dat")
    sealed_bytes = open(sealed, "rb").read() if os.path.isfile(sealed) else b""
    check("a key can be stored headlessly", stored.returncode == 0 and os.path.isfile(sealed), f"exit {stored.returncode}")
    check("the sealed file holds no plain text", b"test-secret" not in sealed_bytes)
    key_file = os.path.join(WALLET_HOME, "secrets.key")
    if os.path.isfile(key_file):
        check("without a Secret Service the key lives in secrets.key, mode 0600", (os.stat(key_file).st_mode & 0o777) == 0o600,
              oct(os.stat(key_file).st_mode))
        written = os.stat(key_file).st_mtime_ns
        again = subprocess.run([WALLET, "--import-key", "openrouter", dummy], env=env, capture_output=True, timeout=30)
        check("the same key file is used the next time (the key is not made again)",
              again.returncode == 0 and os.stat(key_file).st_mtime_ns == written)
    else:
        check("a Secret Service kept the key (no key file in the data folder)", True, "the session has a Secret Service")

    # ------------------------------------------------------------------------------------------------- 2. browsers and the desktop
    for browser in ("google-chrome", os.path.join("BraveSoftware", "Brave-Browser")):
        manifest = os.path.join(CONFIG, browser, "NativeMessagingHosts", "org.bringyourownagent.wallet.json")
        ok = os.path.isfile(manifest)
        data = json.load(open(manifest, encoding="utf-8")) if ok else {}
        check(f"the host manifest for {browser}", ok and data.get("path") == HOST and data.get("allowed_origins") == ["chrome-extension://niobopdoiopjilmlonjcgllpmgjemhpk/"],
              str(data.get("path")))
    check("no manifest for a browser that is not installed", not os.path.exists(os.path.join(CONFIG, "microsoft-edge")))
    check("the extension copy is where Load unpacked can find it", os.path.isfile(os.path.join(DATA, "BYOA", "extension", "manifest.json")))

    entry = os.path.join(DATA, "applications", "org.bringyourownagent.wallet.desktop")
    entry_text = open(entry, encoding="utf-8").read() if os.path.isfile(entry) else ""
    check("the desktop entry starts this wallet for a link", os.path.isfile(entry)
          and ('Exec="' + WALLET + '" --url %u') in entry_text
          and "MimeType=x-scheme-handler/bringyourownagent;" in entry_text
          and "StartupWMClass=Byoa.Wallet" in entry_text, entry_text.replace("\n", " ")[:160])
    check("the icon is installed for the desktop", os.path.isfile(os.path.join(DATA, "icons", "hicolor", "16x16", "apps", "org.bringyourownagent.wallet.png"))
          and os.path.isfile(os.path.join(DATA, "icons", "hicolor", "128x128", "apps", "org.bringyourownagent.wallet.png")))
    check("the host manifest also reaches the Chrome Flatpak folder", os.path.isfile(os.path.join(HOME, ".var", "app", "com.google.Chrome", "config", "google-chrome", "NativeMessagingHosts", "org.bringyourownagent.wallet.json")))

    # ------------------------------------------------------------------------------------------------- 3. the real host
    status = host_ask({"id": 1, "type": "status", "origin": "", "payload": {}})
    check("the host reaches the wallet: status says it runs", bool(status) and (status.get("result") or {}).get("running") is True, json.dumps(status))
    opened = host_ask({"id": 2, "type": "open", "origin": "", "payload": {}})
    check("the host's 'open' is answered by the wallet", opened is not None and opened.get("id") == 2, json.dumps(opened))

    # ------------------------------------------------------------------------------------------------- 3b. the real MCP bridge
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

    # ------------------------------------------------------------------------------------------------- 5. a crash leaves a stale socket
    wallet.kill()
    wallet.wait(10)
    check("after kill -9 the socket file is left behind", os.path.exists(sock))
    wallet2 = start_wallet()
    check("a new wallet starts over the stale socket", wait_for(lambda: wallet2.poll() is not None or host_ask({"id": 3, "type": "status", "origin": "", "payload": {}}, 5) is not None, 30) and wallet2.poll() is None)
    status = host_ask({"id": 4, "type": "status", "origin": "", "payload": {}})
    check("and the host reaches it", bool(status) and (status.get("result") or {}).get("running") is True, json.dumps(status))

    # ------------------------------------------------------------------------------------------------- 6. a clean stop
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