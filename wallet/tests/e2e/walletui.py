"""Helpers for driving the wallet's desktop windows in tests: UI Automation (find windows and controls, press
buttons, tick boxes) and screenshots of a window's rectangle. Windows only. Needs: uiautomation, pillow.

The wallet's windows are titled "Agent wallet notice", "Agent wallet consent", "Agent wallet paid fallback",
"Agent wallet message", "Agent wallet download", "Agent wallet preparing" and "Agent wallet" (the settings); its controls carry
AutomationIds such as NoticeShow, NoticeDecline, NoticeBlock, ConsentAllow, ConsentDecline, ConsentAuto,
PaidOnce, PaidNo, Tab-sites, SourceEnabled-gigaam-v3.
"""
import hashlib
import ctypes
import os
import time

import uiautomation as auto
from PIL import ImageGrab

auto.SetGlobalSearchTimeout(1)

# A test sets EXCLUDE_PIDS to the wallet processes that were running before it started (the visitor's own wallet, maybe
# showing its settings): their windows are never touched, whatever a window is called. ONLY_PIDS (a set) does the
# opposite when the test knows the process ids of the wallet it started.
EXCLUDE_PIDS = frozenset()
ONLY_PIDS = None


def _mine(control, depth):
    if ONLY_PIDS is not None:
        return control.ProcessId in ONLY_PIDS
    return control.ProcessId not in EXCLUDE_PIDS

# UI runs use an isolated wallet home. Inherit this Windows error mode into the wallet child so a
# deliberate managed crash writes errors.log without opening a native crash dialog on the owner's desktop.
if os.name == "nt":
    ctypes.windll.kernel32.SetErrorMode(0x8003)


def find_window(title, timeout=10.0):
    """The top-level window with this exact title, or None."""
    end = time.time() + timeout
    while time.time() < end:
        w = auto.WindowControl(searchDepth=1, Name=title, Compare=_mine)
        if w.Exists(0.2, 0.1):
            return w
        time.sleep(0.2)
    return None


def window_gone(title, timeout=5.0):
    end = time.time() + timeout
    while time.time() < end:
        if not auto.WindowControl(searchDepth=1, Name=title, Compare=_mine).Exists(0, 0):
            return True
        time.sleep(0.2)
    return False


def control(window, automation_id, timeout=5.0):
    end = time.time() + timeout
    while time.time() < end:
        try:
            c = window.Control(AutomationId=automation_id, searchDepth=30)
            if c.Exists(0.2, 0.1):
                return c
        except Exception as e:  # noqa: BLE001  (a window that is being drawn again answers "Unspecified error" to a walk: look again)
            if type(e).__name__ != "COMError":
                raise
        time.sleep(0.2)
    raise LookupError(f"no control {automation_id!r} in {window.Name!r}")


def invoke(window, automation_id):
    """Presses a button through UI Automation (no mouse movement)."""
    c = control(window, automation_id)
    c.GetPattern(auto.PatternId.InvokePattern).Invoke()
    return c


def click(window, automation_id):
    """Presses a button with a real mouse click at its centre (real input). With BYOA_E2E_NO_REAL_INPUT=1 it presses it
    through UI Automation instead, so a run on a computer that somebody is using does not move the mouse."""
    if os.environ.get("BYOA_E2E_NO_REAL_INPUT") == "1":
        return invoke(window, automation_id)
    c = control(window, automation_id)
    c.Click(simulateMove=False, waitTime=0.2)
    return c


def toggle(window, automation_id, on=True):
    c = control(window, automation_id)
    pattern = c.GetPattern(auto.PatternId.TogglePattern)
    if (pattern.ToggleState == 1) != on:
        pattern.Toggle()
    return c


def select(window, automation_id):
    c = control(window, automation_id)
    c.GetPattern(auto.PatternId.SelectionItemPattern).Select()
    return c


def shot(window, path, pad=0):
    """Saves a screenshot of the window's rectangle (what is on screen there)."""
    r = window.BoundingRectangle
    img = ImageGrab.grab(bbox=(r.left - pad, r.top - pad, r.right + pad, r.bottom + pad), all_screens=True)
    img.save(path)
    return path


def shot_rect(rect, path):
    ImageGrab.grab(bbox=rect, all_screens=True).save(path)
    return path


def pipe_name(home=None):
    """The wallet's pipe name, computed as WalletPaths.PipeName does."""
    def h(text):
        return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]
    account = (os.environ.get("USERDOMAIN", "") + "\\" + os.environ.get("USERNAME", "")).lower()
    name = "byoa-wallet-" + h(account)
    if home:
        name += "-" + h(os.path.abspath(home).rstrip("\\/").lower())
    return name


def find_protocol_prompt(timeout=15.0):
    """Chromium's own question before it opens an app for a link ("Open <app>?" with Open and Cancel): the window
    and its Open button, found by the dialog's text (Chromium has other buttons named "Open ...")."""
    end = time.time() + timeout
    while time.time() < end:
        for top in auto.GetRootControl().GetChildren():
            if top.ClassName != "Chrome_WidgetWin_1":
                continue
            text = top.TextControl(SubName="wants to open this application", searchDepth=14)
            if not text.Exists(0.2, 0.1):
                continue
            cancel = top.ButtonControl(Name="Cancel", searchDepth=14)
            for button in (cancel.GetParentControl().GetChildren() if cancel.Exists(0.2, 0.1) else []):
                if button.ControlTypeName == "ButtonControl" and button.Name.startswith("Open "):
                    return top, button
            button = top.ButtonControl(RegexName=r"^Open (?!tab)", searchDepth=14)
            if button.Exists(0.2, 0.1):
                return top, button
        time.sleep(0.3)
    return None
