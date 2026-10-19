"""A desktop of its own for tests that drive real windows, so nothing appears on the screen somebody is working at. Windows only.

Windows created by a process that was started on another desktop (CreateDesktop) never show on the visitor's desktop, and a thread
that is attached to that desktop (SetThreadDesktop) sees them through UI Automation and can draw them into a bitmap (PrintWindow).
Real mouse and keyboard input cannot reach such windows: press buttons through UI Automation (walletui.invoke).

    import hidden_desktop as hd
    desk = hd.HiddenDesktop("byoa-test")      # FIRST, before anything that starts COM (uiautomation): the thread must have no windows yet
    proc = desk.start([exe, "--url", link], env=env)
    import uiautomation as auto               # only now
    ...
    desk.shot(window_control, "shot.png")
    desk.terminate(proc)                      # only processes you started
"""
import ctypes
import ctypes.wintypes as wt
import os

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_ALL = 0x10000000
CREATE_UNICODE_ENVIRONMENT = 0x00000400
CREATE_NEW_PROCESS_GROUP = 0x00000200
PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR), ("lpTitle", wt.LPWSTR),
        ("dwX", wt.DWORD), ("dwY", wt.DWORD), ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD),
        ("dwXCountChars", wt.DWORD), ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD), ("dwFlags", wt.DWORD),
        ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD), ("lpReserved2", ctypes.c_void_p),
        ("hStdInput", wt.HANDLE), ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE), ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class Started:
    """A process started on the hidden desktop."""

    def __init__(self, info):
        self.pid = info.dwProcessId
        self.handle = info.hProcess
        kernel32.CloseHandle(info.hThread)

    def alive(self):
        return kernel32.WaitForSingleObject(self.handle, 0) == 0x102  # WAIT_TIMEOUT


class HiddenDesktop:
    def __init__(self, name):
        self.name = name
        # Real mouse input would land on the visitor's desktop, whatever window it was aimed at: never allowed from here on
        # (walletui.click then presses buttons through UI Automation).
        os.environ["BYOA_E2E_NO_REAL_INPUT"] = "1"
        user32.CreateDesktopW.restype = wt.HANDLE
        self.handle = user32.CreateDesktopW(name, None, None, 0, GENERIC_ALL, None)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "CreateDesktop failed")
        if not user32.SetThreadDesktop(self.handle):
            raise OSError(ctypes.get_last_error(), "SetThreadDesktop failed: this thread already has windows (call HiddenDesktop before COM starts)")

    def start(self, args, env=None, cwd=None):
        """Starts a program on this desktop. `args` is a list; `env` a dict (default: this process's environment)."""
        line = ctypes.create_unicode_buffer(subprocess_list2cmdline(args))
        si = STARTUPINFOW()
        si.cb = ctypes.sizeof(si)
        si.lpDesktop = "winsta0\\" + self.name
        pi = PROCESS_INFORMATION()
        block = None
        if env is not None:
            text = "".join(f"{k}={v}\0" for k, v in env.items()) + "\0"
            block = ctypes.create_unicode_buffer(text, len(text))
        kernel32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL, wt.DWORD,
                                            ctypes.c_void_p, wt.LPCWSTR, ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]
        ok = kernel32.CreateProcessW(None, line, None, None, False, CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP,
                                     ctypes.cast(block, ctypes.c_void_p) if block is not None else None, cwd, ctypes.byref(si), ctypes.byref(pi))
        if not ok:
            raise OSError(ctypes.get_last_error(), "CreateProcess failed: " + line.value)
        return Started(pi)

    def terminate(self, started):
        """Ends a process this script started (never anybody else's)."""
        kernel32.TerminateProcess(started.handle, 1)
        kernel32.CloseHandle(started.handle)

    def shot(self, control, path):
        """Draws a window (a UI Automation control with a NativeWindowHandle) into a PNG, wherever it is."""
        from PIL import Image
        gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
        vp = ctypes.c_void_p
        user32.GetWindowDC.argtypes, user32.GetWindowDC.restype = [vp], vp
        user32.ReleaseDC.argtypes = [vp, vp]
        user32.PrintWindow.argtypes = [vp, vp, wt.UINT]
        user32.GetWindowRect.argtypes = [vp, ctypes.POINTER(wt.RECT)]
        gdi32.CreateCompatibleDC.argtypes, gdi32.CreateCompatibleDC.restype = [vp], vp
        gdi32.CreateCompatibleBitmap.argtypes, gdi32.CreateCompatibleBitmap.restype = [vp, ctypes.c_int, ctypes.c_int], vp
        gdi32.SelectObject.argtypes, gdi32.SelectObject.restype = [vp, vp], vp
        gdi32.GetDIBits.argtypes = [vp, vp, wt.UINT, wt.UINT, vp, vp, wt.UINT]
        gdi32.DeleteObject.argtypes = [vp]
        gdi32.DeleteDC.argtypes = [vp]
        hwnd = vp(control.NativeWindowHandle)
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        hdc = user32.GetWindowDC(hwnd)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
        gdi32.SelectObject(mem, bmp)
        user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
        bi = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
        buf = ctypes.create_string_buffer(w * h * 4)
        gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(hwnd, hdc)
        Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB").save(path)
        return path


def subprocess_list2cmdline(args):
    import subprocess
    return subprocess.list2cmdline([str(a) for a in args])
