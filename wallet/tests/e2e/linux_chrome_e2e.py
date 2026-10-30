#!/usr/bin/env python3
"""Linux: a real Chrome with the unpacked extension talks to the real packaged wallet through the real native host.

    python3 linux_chrome_e2e.py --app <folder with Byoa.Wallet, byoa-host and extension/> --work <a new folder> [--chrome <program>]

Run it inside a display and a session bus of its own (the test machines use
                            xvfb-run -a -s "-screen 0 1280x800x24" dbus-run-session -- python3 ...),
with a data folder, a Chrome profile and a temporary folder of its own under the work folder: the person's wallet,
browsers and keyring are not touched. The Chrome profile is <work>/support/.config/google-chrome, which is also the
user-data-dir Chrome is given, so Chrome looks for native hosts in <profile>/NativeMessagingHosts, where the wallet
(told by BYOA_NATIVE_HOST_ROOT) writes its manifest.

Checks: the extension loads with the ID the wallet's manifest allows; its popup says "Wallet connected" through the
host; the wallet is stopped and the popup says "Wallet not running"; the popup's "Open the wallet" button makes the
host start it again, and the popup says connected again.

Python, not node: the T480 has the browsers and no node; the DevTools protocol is the same over the pipe Chrome is
given on file descriptors 3 and 4.
"""
import base64
import fcntl
import hashlib
import json
import os
import select
import signal
import subprocess
import sys
import time

ARGS = {}
rest = sys.argv[1:]
while rest:
    if rest[0].startswith("--") and len(rest) >= 2:
        ARGS[rest[0][2:]] = rest[1]
        rest = rest[2:]
    else:
        print("usage: --app <folder> --work <a new folder> [--chrome <program>]", file=sys.stderr)
        sys.exit(2)

APP = os.path.abspath(ARGS.get("app", ""))
WORK = os.path.abspath(ARGS.get("work", ""))
CHROME = ARGS.get("chrome", "/usr/bin/google-chrome")
if not ARGS.get("app") or not ARGS.get("work"):
    print("usage: --app <folder> --work <a new folder> [--chrome <program>]", file=sys.stderr)
    sys.exit(2)
if os.path.exists(WORK):
    print("the work folder exists: choose a new one (this test deletes nothing)", file=sys.stderr)
    sys.exit(2)

HOME = os.path.join(WORK, "home")
SUPPORT = os.path.join(WORK, "support")
PROFILE = os.path.join(SUPPORT, ".config", "google-chrome")
RUNTIME = os.path.join(WORK, "run")
os.makedirs(os.path.join(PROFILE, "NativeMessagingHosts"))
os.makedirs(os.path.join(HOME, ".config"))
os.makedirs(RUNTIME)
os.chmod(RUNTIME, 0o700)
ENV = dict(os.environ,
           HOME=HOME,
           XDG_DATA_HOME=os.path.join(HOME, ".local", "share"),
           XDG_CONFIG_HOME=os.path.join(HOME, ".config"),
           XDG_RUNTIME_DIR=RUNTIME,
           BYOA_WALLET_HOME=os.path.join(WORK, "wallet"),
           BYOA_NATIVE_HOST_ROOT=SUPPORT,
           AVALONIA_TELEMETRY_OPTOUT="1")

FAILED = []


def check(label, ok, extra=""):
    print(("ok   " if ok else "FAIL ") + label + (f"  [{extra}]" if extra else ""))
    if not ok:
        FAILED.append(label)


def sleep(seconds):
    time.sleep(seconds)


def until(fn, milliseconds, step=0.25):
    end = time.monotonic() + milliseconds / 1000
    while time.monotonic() < end:
        try:
            value = fn()
            if value:
                return value
        except Exception:
            pass
        time.sleep(step)
    return None


# the extension's ID, from the key in its manifest (the same rule as install.ps1 and NativeHostManifests.ExtensionId)
with open(os.path.join(APP, "extension", "manifest.json"), encoding="utf-8") as f:
    EXTENSION_MANIFEST = json.load(f)
DIGEST = hashlib.sha256(base64.b64decode(EXTENSION_MANIFEST["key"])).hexdigest()[:32]
EXT_ID = "".join(chr(97 + int(c, 16)) for c in DIGEST)

WALLET = os.path.join(APP, "Byoa.Wallet")
HOST_MANIFEST = os.path.join(PROFILE, "NativeMessagingHosts", "org.bringyourownagent.wallet.json")
CHILDREN = []


class Cdp:
    """The DevTools protocol over the pipe Chrome is given on descriptors 3 and 4; messages are JSON ended by NUL."""

    def __init__(self, to_chrome, from_chrome):
        self.to_chrome = to_chrome
        self.from_chrome = from_chrome
        self.serial = 0
        self.buffer = b""

    def send(self, method, params=None, session=None, timeout=30):
        self.serial += 1
        mid = self.serial
        message = {"id": mid, "method": method, "params": params or {}}
        if session:
            message["sessionId"] = session
        os.write(self.to_chrome, json.dumps(message).encode() + b"\0")
        end = time.monotonic() + timeout
        while True:
            while b"\0" not in self.buffer:
                left = end - time.monotonic()
                if left <= 0:
                    raise TimeoutError(method)
                ready, _, _ = select.select([self.from_chrome], [], [], left)
                if not ready:
                    raise TimeoutError(method)
                chunk = os.read(self.from_chrome, 65536)
                if not chunk:
                    raise EOFError("Chrome closed the DevTools pipe")
                self.buffer += chunk
            raw, self.buffer = self.buffer.split(b"\0", 1)
            reply = json.loads(raw)
            if reply.get("id") == mid:
                return reply


def spawn_chrome():
    """Chrome reads the DevTools protocol on descriptor 3 and writes it on 4.

    The two ends are made fds 3 and 4 in this process before the fork and passed with pass_fds, so the child keeps
    them across the exec; a preexec dup would be closed again by subprocess's own close-fds step.
    """
    child_in, to_child = os.pipe()          # the child reads child_in; this process writes to_child
    from_child, child_out = os.pipe()       # the child writes child_out; this process reads from_child
    high = []
    for fd in (child_in, child_out, to_child, from_child):
        high.append(fcntl.fcntl(fd, fcntl.F_DUPFD, 50))
        os.close(fd)
    high_in, high_out, high_write, high_read = high
    os.dup2(high_in, 3)
    os.dup2(high_out, 4)
    os.close(high_in)
    os.close(high_out)

    log = open(os.path.join(WORK, "chrome.log"), "wb")
    process = subprocess.Popen(
        [CHROME, f"--user-data-dir={PROFILE}", "--remote-debugging-pipe", "--enable-unsafe-extension-debugging",
         "--no-first-run", "--no-default-browser-check", "--password-store=basic", "--disable-gpu",
         "--disable-dev-shm-usage", "--window-size=1280,800", "about:blank"],
        env=ENV, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
        pass_fds=(3, 4), start_new_session=True)
    os.close(3)
    os.close(4)
    return process, high_write, high_read


def stop_process(process):
    if process is None or process.poll() is not None:
        return
    try:
        os.killpg(os.getpgid(process.pid), signal.SIGTERM)
    except (ProcessLookupError, PermissionError):
        try:
            process.terminate()
        except ProcessLookupError:
            pass


def processes_of_this_copy():
    """The wallet the host started is not our child: find it, and the hosts, by the path of the copy under test."""
    found = []
    try:
        listing = subprocess.run(["ps", "-eo", "pid=,args="], capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return found
    for line in listing.splitlines():
        pid, _, command = line.strip().partition(" ")
        if pid.isdigit() and int(pid) != os.getpid() and (WALLET in command or os.path.join(APP, "byoa-host") in command):
            found.append((int(pid), command))
    return found


chrome = None
try:
    wallet = subprocess.Popen([WALLET, "--background"], env=ENV,
                              stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              start_new_session=True)
    CHILDREN.append(wallet)
    sleep(5)
    check("the wallet is running", wallet.poll() is None, str(wallet.poll()))
    check("the wallet wrote the host manifest into the Chrome profile of the test",
          until(lambda: os.path.exists(HOST_MANIFEST), 20000) is not None)
    check("and it names the extension this test loads",
          os.path.exists(HOST_MANIFEST) and f"chrome-extension://{EXT_ID}/" in open(HOST_MANIFEST, encoding="utf-8").read(), EXT_ID)

    chrome, to_chrome, from_chrome = spawn_chrome()
    CHILDREN.append(chrome)
    cdp = Cdp(to_chrome, from_chrome)
    version = until(lambda: cdp.send("Browser.getVersion").get("result"), 30000)
    check("a Chrome of its own started", bool(version), (version or {}).get("product", ""))
    loaded = cdp.send("Extensions.loadUnpacked", {"path": os.path.join(APP, "extension")})
    check("the extension is loaded into it with the expected ID",
          loaded.get("result", {}).get("id") == EXT_ID, loaded.get("result", {}).get("id") or json.dumps(loaded.get("error", loaded)))
    worker = until(lambda: next((t for t in cdp.send("Target.getTargets")["result"]["targetInfos"]
                                 if t["url"].startswith(f"chrome-extension://{EXT_ID}/")), None), 20000)
    check("its service worker runs", bool(worker), (worker or {}).get("url", "no target of the extension"))

    def popup_chip():
        target = cdp.send("Target.createTarget", {"url": f"chrome-extension://{EXT_ID}/popup.html"})["result"]
        attached = cdp.send("Target.attachToTarget", {"targetId": target["targetId"], "flatten": True})["result"]
        session = attached["sessionId"]

        def read():
            reply = cdp.send("Runtime.evaluate", {"expression": 'document.getElementById("chip").textContent',
                                                  "returnByValue": True}, session)
            value = reply.get("result", {}).get("result", {}).get("value")
            return value if value and value != "Checking…" else None

        return until(read, 20000), session

    first, _ = popup_chip()
    check("the popup, through the real native host, says the wallet is connected", first == "Wallet connected", str(first))

    wallet.terminate()
    until(lambda: wallet.poll() is not None, 20000)
    check("the wallet was stopped", wallet.poll() is not None)
    second, second_session = popup_chip()
    check("the popup now says the wallet is not running", second == "Wallet not running", str(second))
    cdp.send("Runtime.evaluate", {"expression": 'document.getElementById("open").click()', "returnByValue": True}, second_session)
    connected = until(lambda: (lambda reply: reply.get("result", {}).get("result", {}).get("value"))(
        cdp.send("Runtime.evaluate", {"expression": 'document.getElementById("chip").textContent',
                                      "returnByValue": True}, second_session)) == "Wallet connected", 40000, 0.5)
    check('"Open the wallet" makes the host start the wallet, and the popup says connected', bool(connected))
    if not connected:
        shown = cdp.send("Runtime.evaluate", {"expression": 'document.getElementById("chip").textContent + " | " + document.getElementById("note").textContent',
                                              "returnByValue": True}, second_session)
        print("  diag: popup shows: " + str(shown.get("result", {}).get("result", {}).get("value")))
        print("  diag: processes of this copy: " + str(processes_of_this_copy()))
        print("  diag: sockets in TMPDIR: " + str([n for n in os.listdir(os.environ.get("TMPDIR", "/tmp")) if n.startswith("byoa-")]))
except Exception as e:
    check("no exception", False, str(e))
finally:
    stop_process(chrome)
    for child in CHILDREN:
        if child.poll() is None:
            try:
                os.killpg(os.getpgid(child.pid), signal.SIGTERM)
            except (ProcessLookupError, PermissionError):
                pass
    for pid, _ in processes_of_this_copy():
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass

print("\nALL OK" if not FAILED else f"\n{len(FAILED)} FAILED: {'; '.join(FAILED)}")
sys.exit(1 if FAILED else 0)