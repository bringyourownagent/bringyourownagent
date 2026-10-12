"""Small UI Automation fallback for environments without the optional uiautomation package."""
import ctypes
import os
import subprocess
import time

from PIL import ImageGrab


class Window:
    def __init__(self, title):
        self.title = title


def _ps(script):
    result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, text=True, encoding="utf-8")
    return result.stdout.strip()


def _quote(value):
    return "'" + value.replace("'", "''") + "'"


def _find(title, automation_id=None):
    target = _quote(title)
    if automation_id is None:
        script = f"""
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, {target})
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
if ($null -ne $w) {{ 'yes' }}
"""
    else:
        aid = _quote(automation_id)
        script = f"""
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, {target})
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
if ($null -eq $w) {{ exit 2 }}
$bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, {aid})
$b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bc)
if ($null -ne $b) {{ 'yes' }}
"""
    return _ps(script) == "yes"


def find_window(title, timeout=10.0):
    end = time.time() + timeout
    while time.time() < end:
        if _find(title):
            return Window(title)
        time.sleep(0.2)
    return None


def window_gone(title, timeout=5.0):
    end = time.time() + timeout
    while time.time() < end:
        if not _find(title):
            return True
        time.sleep(0.2)
    return False


def invoke(window, automation_id):
    title = _quote(window.title)
    aid = _quote(automation_id)
    script = f"""
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, {title})
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
if ($null -eq $w) {{ exit 2 }}
$bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, {aid})
$b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bc)
if ($null -eq $b) {{ exit 3 }}
$p = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$p.Invoke()
"""
    result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True)
    if result.returncode != 0:
        raise RuntimeError("could not invoke " + automation_id.decode() if isinstance(automation_id, bytes) else "could not invoke " + automation_id)
    return window


def click(window, automation_id):
    return invoke(window, automation_id)


def _bounds(window):
    title = _quote(window.title)
    script = f"""
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, {title})
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
if ($null -eq $w) {{ exit 2 }}
$r = $w.Current.BoundingRectangle
('{0},{1},{2},{3}' -f $r.Left, $r.Top, $r.Right, $r.Bottom)
"""
    values = _ps(script).replace(".", ".").split(",")
    if len(values) != 4:
        raise RuntimeError("could not read the window rectangle")
    return tuple(int(float(value)) for value in values)


def shot(window, path, pad=0):
    left, top, right, bottom = _bounds(window)
    ImageGrab.grab(bbox=(left - pad, top - pad, right + pad, bottom + pad), all_screens=True).save(path)
    return path
