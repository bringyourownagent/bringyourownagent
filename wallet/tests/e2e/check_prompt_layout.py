"""Check that wallet prompt actions are inside the visible window bounds.

Run this while the isolated wallet has a prompt open, for example:
    python check_prompt_layout.py --title "Connect an AI agent" --button AgentAllow --button AgentNotNow --button AgentBlock

The script uses UI Automation bounds rather than a screen capture, so it never reads or saves the owner's desktop.
Set the process DPI awareness before looking at bounds; the same check can be repeated at 100%, 125% and 150%.
"""
import argparse
import ctypes
import sys

import uiautomation as auto

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

auto.SetGlobalSearchTimeout(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--title", required=True)
    parser.add_argument("--button", action="append", required=True)
    args = parser.parse_args()

    window = auto.WindowControl(searchDepth=1, Name=args.title)
    if not window.Exists(1, 0.1):
        print(f"window not found: {args.title}", file=sys.stderr)
        return 2

    outer = window.BoundingRectangle
    failures = []
    for automation_id in args.button:
        button = window.Control(AutomationId=automation_id, searchDepth=30)
        if not button.Exists(1, 0.1):
            failures.append(f"button not found: {automation_id}")
            continue
        bounds = button.BoundingRectangle
        if (bounds.left < outer.left or bounds.top < outer.top or
                bounds.right > outer.right or bounds.bottom > outer.bottom):
            failures.append(
                f"{automation_id} {bounds} is outside window {outer}")

    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1
    print(f"{args.title}: {len(args.button)} action rows are inside the window")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
