"""The installer and "Check for updates", end to end, on a desktop of its own (nothing appears on the screen somebody works at).

Needs two Velopack releases of a TEST copy of the wallet (make-package.ps1 -Installer -PackId AgentWalletTest -Shortcuts None, once with
-Version 0.30.0 and once, with -WithDeltas, with -Version 0.30.1) and the second release folder served over HTTP, for example
`python -I -m http.server 8099 --bind 127.0.0.1 -d <release folder of 0.30.1>`. The test copy lives in %LOCALAPPDATA%\\<pack id>, beside the
real wallet; it has its own data folder, so it leaves the browsers' registry keys alone (the hooks skip them for a test wallet).

  1. Setup.exe of the OLD version installs it (silent).
  2. The wallet from the install folder opens; the settings show "Version <old>"; "Check for updates" says a newer version is ready.
  3. A program of the wallet that a browser or an agent would have running (byoa-host.exe, byoa-mcp.exe) stays open meanwhile.
  4. "Update and restart": the old wallet closes, the install folder holds the new version, and the wallet is back as the new version.
  5. "Check again" says it is the newest.
  6. The registry keys of the real wallet were not touched.

    <python with uiautomation, pillow> installer_update_ui.py --setup <Setup.exe of the old version> --feed http://127.0.0.1:8099 \\
        --home <empty data folder> --old 0.30.0 --new 0.30.1 [--pack-id AgentWalletTest]
"""
import argparse
import os
import subprocess
import sys
import time
import winreg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hidden_desktop as hd  # noqa: E402  (before anything that starts COM)

ap = argparse.ArgumentParser()
ap.add_argument("--setup", required=True)
ap.add_argument("--feed", required=True)
ap.add_argument("--home", required=True)
ap.add_argument("--old", required=True)
ap.add_argument("--new", required=True)
ap.add_argument("--pack-id", default="AgentWalletTest")
ap.add_argument("--desktop", default="byoa-installer-update")
a = ap.parse_args()
os.makedirs(a.home, exist_ok=True)

desk = hd.HiddenDesktop(a.desktop)
import uiautomation as auto  # noqa: E402
import walletui as ui  # noqa: E402

root = os.path.join(os.environ["LOCALAPPDATA"], a.pack_id)
current = os.path.join(root, "current")
wallet_exe = os.path.join(current, "Byoa.Wallet.exe")
env = dict(os.environ, BYOA_WALLET_HOME=a.home, BYOA_UPDATE_FEED=a.feed)
failed = []
started = []                                             # processes this test started, to be stopped by id at the end


def check(ok, text):
    print(("ok   " if ok else "FAIL ") + text, flush=True)
    if not ok:
        failed.append(text)


def registry_text():
    out = []
    for key in (r"Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet", r"Software\Classes\bringyourownagent\shell\open\command"):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as k:
                out.append(winreg.QueryValueEx(k, "")[0])
        except OSError:
            out.append(None)
    return out


def version_of(path):
    r = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Item -LiteralPath '{path}').VersionInfo.ProductVersion"], capture_output=True, text=True)
    return r.stdout.strip().split("+")[0]


def test_wallet_pids():
    """Wallet processes that run from the test install folder (the visitor's own wallet runs from elsewhere)."""
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        f"Get-Process Byoa.Wallet -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{root}\\*' }} | ForEach-Object {{ $_.Id }}"],
                       capture_output=True, text=True)
    return [int(x) for x in r.stdout.split()]


def wait_for(predicate, seconds, step=0.5):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(step)
    return predicate()


def text_on(window, part, seconds=20):
    """True when a text of the window contains `part`."""
    def seen():
        try:
            return auto.TextControl(searchFromControl=window, SubName=part, searchDepth=30).Exists(0.5)
        except Exception:  # noqa: BLE001
            return False
    return wait_for(seen, seconds, 0.5)


registry_before = registry_text()
try:
    # 1. install the old version
    setup = desk.start([a.setup, "--silent"], env=env)
    check(wait_for(lambda: not setup.alive(), 180), "Setup.exe finished")
    check(os.path.exists(wallet_exe), f"the wallet is installed in {current}")
    check(os.path.exists(os.path.join(root, "Update.exe")), "Update.exe is beside it")
    check(version_of(wallet_exe) == a.old, f"the installed version is {a.old} (is {version_of(wallet_exe)})")
    # the wallet that Setup started (if it did) is closed through its own pid list; a fresh one is started below
    for pid in test_wallet_pids():
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
        time.sleep(1)

    # 2. open the wallet, check for updates
    wallet = desk.start([wallet_exe], env=env)
    started.append(wallet.pid)
    window = ui.find_window("Agent wallet", 60)
    check(text_on(window, f"Version {a.old}"), f"the settings show Version {a.old}")
    ui.invoke(window, "UpdateCheck")
    check(text_on(window, f"Version {a.new} is ready"), f"'Version {a.new} is ready.' after Check for updates")
    check(ui.control(window, "UpdateInstall", 5) is not None, "the button 'Update and restart' is there")

    # 3. programs that a browser or an agent keeps open in the install folder
    held = []
    for name in ("byoa-host.exe", "byoa-mcp.exe"):
        p = subprocess.Popen([os.path.join(current, name)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             env=env, creationflags=0x08000000)
        held.append(p)
        started.append(p.pid)
    time.sleep(1.5)
    check(all(p.poll() is None for p in held), "byoa-host.exe and byoa-mcp.exe are running from the install folder")

    # 4. update
    ui.invoke(window, "UpdateInstall")
    check(wait_for(lambda: not wallet.alive(), 90), "the old wallet closed itself")
    check(wait_for(lambda: version_of(wallet_exe) == a.new, 120), f"the install folder holds {a.new}")
    check(wait_for(lambda: len(test_wallet_pids()) >= 1, 60), "the wallet is running again")
    started.extend(test_wallet_pids())
    window = ui.find_window("Agent wallet", 60)
    check(text_on(window, f"Version {a.new}"), f"the settings show Version {a.new}")
    print("     held programs after the update:", [("running" if p.poll() is None else f"ended {p.poll()}") for p in held])

    # 5. the newest
    ui.invoke(window, "UpdateCheck")
    check(text_on(window, "This is the newest version"), "'This is the newest version.' after the update")
finally:
    for p in locals().get("held", []):
        if p.poll() is None:
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True)
    for pid in set(test_wallet_pids()):
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)

check(registry_text() == registry_before, "the registry keys of the real wallet were not touched")
print(f"\n{'FAILED: ' + str(len(failed)) if failed else 'all ok'}")
sys.exit(1 if failed else 0)
