"""Check the installed-layout bridge path without BYOA_WALLET_MCP_PATH.

This test copies the two Debug app folders into a new published-style folder under --work, starts only the
wallet and bridge processes it created, and looks for the wallet's first-connection window. It deliberately leaves
the folder behind for inspection. A headed Windows desktop with UI Automation is required.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


def fail(message: str) -> int:
    print("bridge_installed_layout: " + message, file=sys.stderr)
    return 2


def main() -> int:
    if os.name != "nt":
        return fail("Windows is required.")
    try:
        import uiautomation as auto
    except ImportError:
        return fail("uiautomation is not installed; run this on a headed Windows desktop.")

    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True, help="A writable work-codex folder; the test never removes it.")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    work = Path(args.work).resolve()
    desktop = repo / "desktop" / "Byoa.Wallet.Desktop" / "bin" / "Debug" / "net10.0" / "win-x64"
    mcp = repo / "mcp" / "Byoa.Wallet.Mcp" / "bin" / "Debug" / "net10.0"
    if not (desktop / "Byoa.Wallet.exe").is_file() or not (mcp / "byoa-mcp.exe").is_file():
        return fail("build the desktop and MCP projects first.")
    work.mkdir(parents=True, exist_ok=True)
    target = work / ("bridge-installed-layout-" + time.strftime("%Y%m%d-%H%M%S") + "-" + str(os.getpid()))
    target.mkdir()
    shutil.copytree(desktop, target, dirs_exist_ok=True)
    for item in mcp.iterdir():
        destination = target / item.name
        if item.is_dir():
            shutil.copytree(item, destination, dirs_exist_ok=True)
        else:
            shutil.copy2(item, destination)

    home = target / "home"
    env = os.environ.copy()
    env["BYOA_WALLET_HOME"] = str(home)
    env.pop("BYOA_WALLET_MCP_PATH", None)
    env["AVALONIA_TELEMETRY_OPTOUT"] = "1"
    wallet = subprocess.Popen([str(target / "Byoa.Wallet.exe")], cwd=target, env=env)
    bridge = None
    try:
        bridge = subprocess.Popen(
            [str(target / "byoa-mcp.exe")], cwd=target, env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8",
        )
        bridge.stdin.write(json.dumps({
            "jsonrpc": "2.0", "id": "installed-layout", "method": "initialize",
            "params": {"clientInfo": {"name": "installed-layout-test"}},
        }) + "\n")
        bridge.stdin.flush()
        deadline = time.monotonic() + 30
        window = None
        while time.monotonic() < deadline:
            if wallet.poll() is not None:
                return fail("the copied wallet exited before showing the agent consent window.")
            window = auto.WindowControl(searchDepth=3, Name="Connect an AI agent")
            if window.Exists(0, 0):
                print("installed bridge accepted; consent window: " + str(target))
                return 0
            time.sleep(0.2)
        return fail("the wallet did not show Connect an AI agent; inspect " + str(target))
    finally:
        if bridge is not None and bridge.poll() is None:
            bridge.terminate()
            bridge.wait(timeout=10)
        if wallet.poll() is None:
            wallet.terminate()
            wallet.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
