"""Driving the Android wallet on a phone through adb, the way a person would: look at the screen (a uiautomator dump and a screenshot), find a
control by what it says, tap it, type into it, scroll.

Avalonia 11.3 on Android exposes its controls to accessibility: `uiautomator dump` shows every control as a node whose `class` is the Avalonia
control's name ("Button", "CheckBox", "TextBox", "TextBlock"...), whose `text` is what it says (a button's text, a tick box's text, a text
block's text) and whose `bounds` are where it is. It does NOT pass AutomationProperties.AutomationId (the node has no resource-id), so controls
are found by class and text, and the bounds of the node give the place to tap: never a fixed coordinate.

Only the app of the wallet (org.bringyourownagent.wallet) is ever touched: install, start, stop, input events on its screen, screenshots,
the dump, logcat, `adb reverse`. No setting of the phone is changed. The phone's screen goes off after 20 seconds without a touch and shows a swipe
lock (no PIN): every look and every touch first makes sure it is awake (a key event, as a finger would be, and a swipe over the lock).
"""
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET

PACKAGE = "org.bringyourownagent.wallet"
ACTIVITY = PACKAGE + "/" + PACKAGE + ".MainActivity"
CREATE_NO_WINDOW = 0x08000000

# awk, on the phone, keeps the records that begin with a NotificationRecord line naming the wallet's package (the program has no quote of the kind that
# Windows' adb would change)
NOTIFICATION_FILTER = r"{source} | awk '/NotificationRecord\(/{{p=/pkg=" + PACKAGE.replace(".", r"\.") + r"/}} p'"


class Node:
    def __init__(self, element):
        a = element.attrib
        self.cls = a.get("class", "")
        self.pkg = a.get("package", "")
        self.rid = a.get("resource-id", "")
        self.text = a.get("text", "")
        self.desc = a.get("content-desc", "")
        self.clickable = a.get("clickable") == "true"
        self.checkable = a.get("checkable") == "true"
        self.checked = a.get("checked") == "true"
        self.enabled = a.get("enabled") == "true"
        self.focused = a.get("focused") == "true"
        self.scrollable = a.get("scrollable") == "true"
        m = re.match(r"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", a.get("bounds", "[0,0][0,0]"))
        self.left, self.top, self.right, self.bottom = (int(x) for x in m.groups())

    @property
    def center(self):
        return (self.left + self.right) // 2, (self.top + self.bottom) // 2

    @property
    def label(self):
        return self.text or self.desc

    def __repr__(self):
        flags = "".join(c for c, on in (("C", self.clickable), ("k", self.checkable), ("v", self.checked), ("-", not self.enabled)) if on)
        return f"{self.cls}[{flags}] {self.label!r} ({self.left},{self.top})-({self.right},{self.bottom})"


class Phone:
    def __init__(self, adb, serial, shots=None):
        self.adb, self.serial, self.shots = adb, serial, shots
        self.step = 0
        self.size = None
        self._poked = 0.0

    # ---------------------------------------------------------------------------------------- adb
    def run(self, *args, timeout=120, check=False):
        r = subprocess.run([self.adb, "-s", self.serial, *args], capture_output=True, timeout=timeout, creationflags=CREATE_NO_WINDOW)
        if check and r.returncode != 0:
            raise RuntimeError(f"adb {' '.join(args)} failed: {r.stderr.decode(errors='replace')}")
        return r

    def shell(self, command, timeout=60):
        """One command of the phone's shell, as a string; its output as text."""
        return self.run("shell", command, timeout=timeout).stdout.decode("utf-8", errors="replace")

    def screen_size(self):
        if self.size is None:
            m = re.search(r"(\d+)x(\d+)", self.shell("wm size"))
            self.size = (int(m.group(1)), int(m.group(2)))
        return self.size

    # ---------------------------------------------------------------------------------------- the screen stays on
    def wake(self):
        """Wakes the screen and swipes the lock away (when it shows). A key event is what a finger would be: it keeps the screen on for 20 seconds.
        Whether the lock shows is read from the screen itself (the controls of the phone's keyguard) and from the phone's own word, which comes late."""
        self.shell("input keyevent KEYCODE_WAKEUP")
        self._poked = time.time()
        time.sleep(1.5)
        for _ in range(6):
            locked = "isKeyguardShowing=true" in self.shell("dumpsys window | grep -m1 isKeyguardShowing") or any("keyguard" in n.rid for n in self._dump_once() or [])
            if not locked:
                return
            w, h = self.screen_size()
            self.shell(f"input swipe {w // 2} {int(h * 0.9)} {w // 2} {int(h * 0.25)} 300")
            self._poked = time.time()
            time.sleep(1.2)

    def ensure_awake(self):
        """Before every look and every touch: the screen goes off after 20 seconds without one."""
        if time.time() - self._poked > 6:
            self.wake()

    def _touched(self):
        self._poked = time.time()

    # ---------------------------------------------------------------------------------------- the app
    def start_app(self):
        self.ensure_awake()
        self.shell(f"am start -n {ACTIVITY}")

    def stop_app(self):
        self.shell(f"am force-stop {PACKAGE}")

    def open_link(self, link):
        """The way Chrome hands a link to an app: a VIEW intent with the link as its data, for the wallet only."""
        self.ensure_awake()
        quoted = "'" + link.replace("'", "'\\''") + "'"
        return self.shell(f"am start -a android.intent.action.VIEW -p {PACKAGE} -d {quoted}")

    def open_address(self, url, explicit=False):
        """A web address as a camera, a browser or a messenger hands it on: a VIEW intent with the address as its data and no package, so that the system
        picks the app (the wallet, when Android has verified the address for it). With `explicit` the wallet's own activity is named: for a build that the
        system has not verified for the address (a Debug build, signed with another key)."""
        self.ensure_awake()
        quoted = "'" + url.replace("'", "'\\''") + "'"
        target = f"-n {ACTIVITY} " if explicit else ""
        return self.shell(f"am start -a android.intent.action.VIEW {target}-d {quoted}")

    def app_links(self):
        """What Android says about the addresses it opens in the wallet (`pm get-app-links`): the domain and whether it is verified."""
        return self.shell(f"pm get-app-links {PACKAGE}")

    def reverify_app_links(self):
        """Asks Android to check the wallet's addresses again (it fetches the domain's assetlinks.json itself)."""
        return self.shell(f"pm verify-app-links --re-verify {PACKAGE}")

    def focus(self):
        """The window that has the focus now (its package and activity), from the window manager."""
        text = self.shell("dumpsys window | grep -a mCurrentFocus")
        return text.strip()

    def press_stop_in_notification(self):
        """What a tap on the notification's Stop does: its PendingIntent starts the wallet's service with the STOP action, as the app itself. A shell cannot
        start a service that is not exported, so the same start is made with the app's own identity (`run-as`, a Debug build); nothing of the screen is touched."""
        return self.shell(f"run-as {PACKAGE} am startservice --user 0 -n {PACKAGE}/{PACKAGE}.ServingService -a {PACKAGE}.STOP_SERVING")

    def debug_recreate(self):
        """Tests only (a Debug build): the activity is destroyed and made again, as a rotation does."""
        self.ensure_awake()
        return self.shell(f"am start -n {ACTIVITY} -a {PACKAGE}.DEBUG_RECREATE")

    def copy_to_clipboard(self, text):
        """Tests only (a Debug build): the text is put on the phone's clipboard, where a visitor has a link he copied; he then presses Paste."""
        self.ensure_awake()
        quoted = "'" + text.replace("'", "'\\''") + "'"
        return self.shell(f"am start -n {ACTIVITY} -a {PACKAGE}.DEBUG_COPY --es text {quoted}")

    # ---------------------------------------------------------------------------------------- looking
    def _dump_once(self, windows=False):
        """One uiautomator dump of the screen (of every window with `windows`: the notification shade is a window of the system's own)."""
        args = ["exec-out", "uiautomator", "dump"] + (["--windows"] if windows else []) + ["/dev/tty"]
        out = self.run(*args, timeout=60).stdout.decode("utf-8", errors="replace")
        start = out.find("<?xml")
        closing = "</displays>" if windows else "</hierarchy>"          # with --windows the windows come inside <displays><display><window>
        end = out.rfind(closing)
        if start >= 0 and end > start:
            root = ET.fromstring(out[start:end + len(closing)])
            return [Node(e) for e in root.iter("node")]
        return None

    def dump(self, tries=6):
        self.ensure_awake()
        for _ in range(tries):
            nodes = self._dump_once()
            if nodes is not None:
                return nodes
            time.sleep(0.7)
        raise RuntimeError("uiautomator gave no dump")

    # ---------------------------------------------------------------------------------------- the notification shade
    def _tap_raw(self, node):
        """A tap that does not wake the phone first: with the shade open, waking would only be in the way."""
        x, y = node.center
        self.shell(f"input tap {x} {y}")
        self._touched()

    def open_shade(self):
        """Pulls the notification shade down, as a person's finger does (the system's own command for it)."""
        self.ensure_awake()
        self.shell("cmd statusbar expand-notifications")
        self._touched()
        time.sleep(1.8)

    def collapse_shade(self):
        self.shell("cmd statusbar collapse")
        time.sleep(1.0)

    def shade_dump(self):
        """The nodes of every window, with the shade open. A caller looks at the wallet's own notification only: nothing else of it is read or kept."""
        for _ in range(6):
            nodes = self._dump_once(windows=True)
            if nodes:
                return nodes
            time.sleep(0.7)
        raise RuntimeError("uiautomator gave no dump of the shade")

    def tap_in_shade(self, title, action=None):
        """Opens the shade, finds the wallet's notification by (part of) its title, and taps it, or its action button `action`; closes the shade when it
        could not find it. Returns True when it tapped."""
        self.open_shade()
        for _ in range(5):                                  # the notifications are laid out a moment after the shade opens
            nodes = self.shade_dump()
            heads = [n for n in nodes if n.pkg == "com.android.systemui" and title in n.label]
            if heads:
                break
            time.sleep(1.0)
        # what the shade held of the wallet's own notification (only its words and its Stop), for a test that fails here to say what it saw; nothing else is kept
        self.last_shade = [(n.label, n.top) for n in nodes if n.pkg == "com.android.systemui" and n.label and (title in n.label or n.label == "Agent wallet" or n.label == action)][:20]
        if not heads:
            self.collapse_shade()
            return False
        head = heads[0]
        if action is None:
            self._tap_raw(head)
            time.sleep(1.2)
            return True
        # the actions show when the notification is expanded: the buttons nearest under the title that say `action`
        found = [n for n in nodes if n.pkg == "com.android.systemui" and n.label == action and n.top >= head.top - 20]
        if not found:
            expand = [n for n in nodes if n.pkg == "com.android.systemui" and "xpand" in (n.desc or "") and abs(n.center[1] - head.center[1]) < 160]
            if expand:
                self._tap_raw(expand[0])
                time.sleep(1.0)
                nodes = self.shade_dump()
                heads = [n for n in nodes if n.pkg == "com.android.systemui" and title in n.label] or heads
                head = heads[0]
                found = [n for n in nodes if n.pkg == "com.android.systemui" and n.label == action and n.top >= head.top - 20]
        if not found:
            self.collapse_shade()
            return False
        self._tap_raw(min(found, key=lambda n: abs(n.top - head.top)))
        time.sleep(1.2)
        return True

    def channel(self, channel_id):
        """What the system holds of one of the wallet's notification channels: its importance, whether it has a sound, whether it vibrates. None when the
        system lists no such channel (a test must then fail, not pass for lack of anything to read)."""
        text = self.shell(f"dumpsys notification --noredact | grep -a \"mId='{channel_id}'\"")
        line = next((l for l in text.splitlines() if f"mId='{channel_id}'" in l), None)
        if line is None:
            return None
        importance = re.search(r"mImportance=(-?\d+)", line)
        sound = re.search(r"mSound=([^,}]*)", line)
        vibration_on = re.search(r"mVibrationEnabled=(true|false)", line)
        vibration = re.search(r"mVibration=([^,}]*)", line)
        return {"importance": int(importance.group(1)) if importance else None, "sound": sound.group(1).strip() if sound else None,
                "vibration_enabled": (vibration_on.group(1) == "true") if vibration_on else None, "vibration": vibration.group(1).strip() if vibration else None,
                "raw": line.strip()[:400]}

    def screenshot(self, name):
        """A PNG of the screen in the shots folder, numbered in the order they were taken. Returns the path."""
        self.ensure_awake()
        self.step += 1
        path = os.path.join(self.shots, f"{self.step:02d}-{name}.png")
        with open(path, "wb") as f:
            f.write(self.run("exec-out", "screencap", "-p", timeout=60).stdout)
        return path

    def texts(self, nodes=None):
        return [n.label for n in (nodes if nodes is not None else self.dump()) if n.label]

    @staticmethod
    def find(nodes, text=None, cls=None, contains=None, clickable=None, startswith=None):
        found = []
        for n in nodes:
            if cls is not None and n.cls != cls:
                continue
            if text is not None and n.label != text:
                continue
            if contains is not None and contains not in n.label:
                continue
            if startswith is not None and not n.label.startswith(startswith):
                continue
            if clickable is not None and n.clickable != clickable:
                continue
            found.append(n)
        return found

    def wait_for(self, predicate, seconds=30, step=0.8):
        """Dumps the screen until predicate(nodes) is truthy; returns what it returned, or None on time out."""
        end = time.time() + seconds
        last = None
        while time.time() < end:
            nodes = self.dump()
            last = predicate(nodes)
            if last:
                return last
            time.sleep(step)
        return None

    def wait_text(self, text=None, contains=None, cls=None, seconds=30):
        got = self.wait_for(lambda nodes: self.find(nodes, text=text, contains=contains, cls=cls), seconds)
        return got[0] if got else None

    # ---------------------------------------------------------------------------------------- touching
    def tap(self, node):
        self.ensure_awake()
        x, y = node.center
        self.shell(f"input tap {x} {y}")
        self._touched()
        time.sleep(0.5)

    def tap_text(self, text=None, contains=None, cls=None, seconds=15):
        node = self.wait_text(text=text, contains=contains, cls=cls, seconds=seconds)
        if node is None:
            raise LookupError(f"nothing on the screen says {text or contains!r}: {self.texts()[:40]}")
        self.tap(node)
        return node

    def type_text(self, node, text):
        """Taps the field, then types; `input text` takes %s for a space."""
        self.tap(node)
        time.sleep(0.4)
        self.shell("input text '" + text.replace(" ", "%s").replace("'", "") + "'")
        self._touched()
        time.sleep(0.4)

    def keyboard_shown(self):
        return "mInputShown=true" in self.shell("dumpsys input_method | grep -a -m1 mInputShown")

    def hide_keyboard(self):
        """The Back key, when the keyboard is up: it takes the keyboard down and goes no further (what a person does after typing)."""
        if self.keyboard_shown():
            self.shell("input keyevent KEYCODE_BACK")
            self._touched()
            time.sleep(0.8)

    def back(self):
        self.ensure_awake()
        self.shell("input keyevent KEYCODE_BACK")
        self._touched()
        time.sleep(0.6)

    def home_key(self):
        """The Home key: the app goes to the background, as when the visitor leaves it."""
        self.ensure_awake()
        self.shell("input keyevent KEYCODE_HOME")
        self._touched()
        time.sleep(0.8)

    def swipe_up(self, frame=None):
        """Scrolls the page content up (the finger moves up) inside the frame (left, top, right, bottom), or in the middle of the screen."""
        w, h = self.screen_size()
        left, top, right, bottom = frame or (0, int(h * 0.15), w, int(h * 0.85))
        x = (left + right) // 2
        self.ensure_awake()
        self.shell(f"input swipe {x} {bottom - 80} {x} {top + 80} 350")
        self._touched()
        time.sleep(0.7)

    def swipe_down(self, frame=None):
        w, h = self.screen_size()
        left, top, right, bottom = frame or (0, int(h * 0.15), w, int(h * 0.85))
        x = (left + right) // 2
        self.ensure_awake()
        self.shell(f"input swipe {x} {top + 80} {x} {bottom - 80} 350")
        self._touched()
        time.sleep(0.7)

    def nudge(self, frame, towards_end, fraction=0.4):
        """One short, slow swipe inside the frame: the page moves by about a third of what the frame shows (a long, quick swipe flings the page past a small control)."""
        left, top, right, bottom = frame
        x = (left + right) // 2
        middle = (top + bottom) // 2
        half = int((bottom - top) * fraction / 2)
        start, end = (middle + half, middle - half) if towards_end else (middle - half, middle + half)
        self.ensure_awake()
        self.shell(f"input swipe {x} {start} {x} {end} 700")
        self._touched()
        time.sleep(0.9)

    def scroll_into_view(self, find, frame, max_swipes=40):
        """Scrolls the page inside `frame` until find(nodes) gives a node whose centre is inside the frame; returns it (or None).
        The dump holds only what is on the screen, so a control that is not there is looked for towards the end of the page first, then, when the page
        stops moving, back towards its start; the swipes are short so that no control is flung past."""
        left, top, right, bottom = frame
        towards_end = True
        before = None
        stops = 0
        for _ in range(max_swipes + 1):
            nodes = self.dump()
            node = find(nodes)
            if node is not None and top <= node.center[1] <= bottom:
                return node
            if node is not None:
                towards_end = node.center[1] > bottom
            seen = tuple((n.label, n.center) for n in nodes if n.label)
            if seen == before:                  # the last swipe moved nothing: this end of the page
                stops += 1
                if stops >= 2:
                    return None
                towards_end = not towards_end
            before = seen
            self.nudge(frame, towards_end)
        return None

    # ---------------------------------------------------------------------------------------- the app's own files and what it posted
    def app_file(self, relative):
        """A file of the app's own data folder (the Debug build lets `run-as` read it), as bytes; None when there is none."""
        r = self.run("exec-out", "run-as", PACKAGE, "cat", relative, timeout=60)
        return r.stdout if r.returncode == 0 and not r.stdout.startswith(b"cat:") else None

    def app_files(self, relative):
        """The names in a folder of the app's own data."""
        text = self.shell(f"run-as {PACKAGE} ls {relative}")
        return [line.strip() for line in text.splitlines() if line.strip() and "No such file" not in line]

    def crash_log(self):
        """The system's crash buffer (what the phone logged of crashes since it was cleared): the whole run, not the last lines of the log."""
        return self.run("logcat", "-d", "-b", "crash", timeout=60).stdout.decode("utf-8", errors="replace")

    def logcat(self, lines=400):
        return self.run("logcat", "-d", "-t", str(lines), timeout=60).stdout.decode("utf-8", errors="replace")

    def app_notifications(self):
        """What the app has posted (the active notifications of its own package, asked of the phone with a filter on the package, so that nothing of
        any other app is read): a list of dicts with id, title, text, actions (their titles), importance, sound, vibrate and the raw text."""
        # the filter runs on the phone and keeps only the records of the app's own package, so that no line of any other app's notification comes here
        text = self.shell(NOTIFICATION_FILTER.format(source="dumpsys notification --noredact"))
        found = []
        for block in re.split(r"(?=NotificationRecord\()", text):
            m = re.search(r"NotificationRecord\(.*?pkg=" + re.escape(PACKAGE) + r".*?id=(-?\d+)", block)
            if not m:
                continue
            title = re.search(r"android\.title=(?:String )?\(?(.*)", block)
            body = re.search(r"android\.text=(?:String )?\(?(.*)", block)
            importance = re.search(r"importance=(\d)", block)
            sound = re.search(r"^\s*sound=(.*)$", block, re.M)
            vibrate = re.search(r"^\s*vibrate=(.*)$", block, re.M)
            found.append({
                "id": int(m.group(1)),
                "title": (title.group(1) if title else "").strip().rstrip(")"),
                "text": (body.group(1) if body else "").strip().rstrip(")"),
                "actions": re.findall(r'\[\d+\]\s+"([^"]+)"\s*->', block),
                "importance": int(importance.group(1)) if importance else None,
                "sound": sound.group(1).strip() if sound else None,
                "vibrate": vibrate.group(1).strip() if vibrate else None,
                "raw": block,
            })
        return found

    def service_running(self):
        """True while the wallet's foreground service is among the services of the app's own package."""
        return "ServingService" in self.shell(f"dumpsys activity services {PACKAGE}")
