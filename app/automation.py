# -*- coding: utf-8 -*-
"""Windows 剪贴板与键盘模拟（ctypes SendInput），仅用于本机自动化。

说明：
- 文本输入统一走「剪贴板 + Ctrl+V」，避免 SendKeys 无法可靠输入中文。
- 快捷键按下/抬起使用 SendInput 注入到当前前台窗口。
- 本模块仅在 Windows 上可用（项目本身即为 Windows 便携发行）。
"""

import ctypes
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

ULONG_PTR = ctypes.c_size_t
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

# SendInput 的 INPUT 联合体大小由最大的 MOUSEINPUT 决定（x64 下 32 字节），
# 因此必须同时声明三个成员，否则结构体大小错误会导致注入失败或内存越界。
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD),
    ]


class _INPUTUNION(ctypes.Union):
    _fields_ = [
        ("mi", MOUSEINPUT),
        ("ki", KEYBDINPUT),
        ("hi", HARDWAREINPUT),
    ]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [
        ("type", wintypes.DWORD),
        ("u", _INPUTUNION),
    ]


kernel32.GlobalAlloc.restype = ctypes.c_void_p
kernel32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
kernel32.GlobalUnlock.restype = wintypes.BOOL
kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
kernel32.GlobalFree.restype = ctypes.c_void_p
kernel32.GlobalFree.argtypes = [ctypes.c_void_p]

user32.OpenClipboard.restype = wintypes.BOOL
user32.OpenClipboard.argtypes = [wintypes.HWND]
user32.EmptyClipboard.restype = wintypes.BOOL
user32.SetClipboardData.restype = ctypes.c_void_p
user32.SetClipboardData.argtypes = [wintypes.UINT, ctypes.c_void_p]
user32.CloseClipboard.restype = wintypes.BOOL
user32.SendInput.restype = wintypes.UINT
user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.VkKeyScanW.restype = wintypes.SHORT
user32.VkKeyScanW.argtypes = [wintypes.WCHAR]

# 窗口定位与前置（避免依赖 Ctrl+Alt+W 的显示/隐藏 toggle 语义）
SW_RESTORE = 9
_WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetForegroundWindow.argtypes = []
user32.IsWindowVisible.restype = wintypes.BOOL
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.ShowWindow.restype = wintypes.BOOL
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.BringWindowToTop.restype = wintypes.BOOL
user32.BringWindowToTop.argtypes = [wintypes.HWND]
user32.EnumWindows.restype = wintypes.BOOL
user32.EnumWindows.argtypes = [_WNDENUMPROC, wintypes.LPARAM]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]


VK_MAP = {
    "backspace": 0x08,
    "tab": 0x09,
    "enter": 0x0D,
    "return": 0x0D,
    "shift": 0x10,
    "ctrl": 0x11,
    "control": 0x11,
    "alt": 0x12,
    "esc": 0x1B,
    "escape": 0x1B,
    "space": 0x20,
    "pageup": 0x21,
    "pagedown": 0x22,
    "end": 0x23,
    "home": 0x24,
    "left": 0x25,
    "up": 0x26,
    "right": 0x27,
    "down": 0x28,
    "insert": 0x2D,
    "delete": 0x2E,
    "win": 0x5B,
    "lwin": 0x5B,
    "rwin": 0x5C,
    "apps": 0x5D,
}
for _i in range(1, 13):
    VK_MAP["f%d" % _i] = 0x70 + _i - 1

_MODIFIERS = {"ctrl", "control", "alt", "shift", "win", "lwin", "rwin"}

STEP_DELAYS = {
    "open": 0.8,
    "search": 0.5,
    "select": 0.8,
    "paste": 0.25,
    "send": 0.4,
}


def _vk_for(key: str) -> int:
    key = str(key).strip().lower()
    if not key:
        raise ValueError("空按键名")
    if key in VK_MAP:
        return VK_MAP[key]
    if len(key) == 1:
        if "a" <= key <= "z":
            return ord(key.upper())
        if "0" <= key <= "9":
            return ord(key)
        return user32.VkKeyScanW(ord(key)) & 0xFF
    return user32.VkKeyScanW(ord(key[0])) & 0xFF


def _send_input(vk: int, keyup: bool = False) -> None:
    flags = KEYEVENTF_KEYUP if keyup else 0
    inp = INPUT()
    inp.type = INPUT_KEYBOARD
    inp.ki = KEYBDINPUT(vk, 0, flags, 0, 0)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(inp))


def press_hotkey(hotkey: str) -> None:
    """按下并释放形如 ``ctrl+alt+w`` 的快捷键。"""
    parts = [p.strip() for p in str(hotkey).lower().split("+") if p.strip()]
    if not parts:
        raise ValueError("无效快捷键：%s" % hotkey)
    modifiers = [p for p in parts if p in _MODIFIERS]
    main_keys = [p for p in parts if p not in _MODIFIERS]
    if not main_keys:
        raise ValueError("快捷键缺少主键：%s" % hotkey)

    for key in modifiers + main_keys:
        _send_input(_vk_for(key), keyup=False)
    for key in reversed(main_keys):
        _send_input(_vk_for(key), keyup=True)
    for key in reversed(modifiers):
        _send_input(_vk_for(key), keyup=True)


def set_clipboard_text(text: str) -> None:
    """把文本写入系统剪贴板（UTF-16，兼容中文）。"""
    text = str(text)
    if not text:
        return
    data = text.encode("utf-16-le") + b"\x00\x00"

    opened = False
    for _ in range(5):
        if user32.OpenClipboard(None):
            opened = True
            break
        time.sleep(0.05)
    if not opened:
        raise RuntimeError("无法打开剪贴板（可能被其它程序占用）")

    try:
        user32.EmptyClipboard()
        handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        if not handle:
            raise RuntimeError("无法分配剪贴板内存")
        ptr = kernel32.GlobalLock(handle)
        if not ptr:
            kernel32.GlobalFree(handle)
            raise RuntimeError("无法锁定剪贴板内存")
        try:
            ctypes.memmove(ptr, data, len(data))
        finally:
            kernel32.GlobalUnlock(handle)
        if not user32.SetClipboardData(CF_UNICODETEXT, handle):
            kernel32.GlobalFree(handle)
            raise RuntimeError("写入剪贴板失败")
    finally:
        user32.CloseClipboard()


def _enum_wechat_windows():
    """枚举可见顶层窗口，返回与微信相关的 [(hwnd, class_name, title), ...]。"""
    found = []

    def callback(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        title_len = user32.GetWindowTextLengthW(hwnd)
        title = ""
        if title_len > 0:
            buffer = ctypes.create_unicode_buffer(title_len + 1)
            user32.GetWindowTextW(hwnd, buffer, title_len + 1)
            title = buffer.value
        class_buffer = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, class_buffer, 256)
        class_name = class_buffer.value
        if "WeChat" in class_name or "微信" in title:
            found.append((hwnd, class_name, title))
        return True

    callback_ref = _WNDENUMPROC(callback)
    user32.EnumWindows(callback_ref, 0)
    return found


def find_wechat_window():
    """定位微信主窗口句柄；找不到返回 None。"""
    hwnd = user32.FindWindowW("WeChatMainWndForPC", None)
    if hwnd:
        return hwnd
    windows = _enum_wechat_windows()
    if not windows:
        return None
    for candidate, _class, title in windows:
        if "微信" in title:
            return candidate
    return windows[0][0]


def _force_foreground(hwnd) -> bool:
    """幂等地把微信窗口前置，不依赖显示/隐藏 toggle。"""
    if not user32.IsWindowVisible(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)
        time.sleep(0.2)
    user32.BringWindowToTop(hwnd)
    if user32.SetForegroundWindow(hwnd):
        return True
    # Windows 前台锁：先发一个 ALT 键释放前台锁定，再重试。
    _send_input(VK_MAP["alt"], keyup=False)
    _send_input(VK_MAP["alt"], keyup=True)
    user32.SetForegroundWindow(hwnd)
    return user32.GetForegroundWindow() == hwnd


def ensure_wechat_foreground(open_hotkey: str) -> None:
    """确保微信在前台；仅当找不到窗口时才回退到唤起快捷键。"""
    hwnd = find_wechat_window()
    if hwnd is None:
        press_hotkey(open_hotkey)
        return
    if user32.GetForegroundWindow() == hwnd:
        return
    _force_foreground(hwnd)


def send_wechat_message(target_name: str, text: str, hotkeys: dict, delays: dict = None,
                        active_chat_name: str = None) -> str:
    """执行「前置微信 → (必要时)搜索会话 → 粘贴回复 → 发送」流程。

    返回执行后微信前台所处的会话名，供下次调用跳过重复搜索。
    """
    delays = dict(STEP_DELAYS, **(delays or {}))

    open_hk = str(hotkeys.get("open_wechat") or "ctrl+alt+w")
    search_hk = str(hotkeys.get("search") or "ctrl+f")
    paste_hk = str(hotkeys.get("paste") or "ctrl+v")
    send_hk = str(hotkeys.get("send") or "enter")

    ensure_wechat_foreground(open_hk)
    time.sleep(delays["open"])

    # 只有当目标会话与当前前台会话不一致时才搜索，避免每次重复搜索。
    if active_chat_name != target_name:
        press_hotkey(search_hk)
        time.sleep(delays["search"])
        set_clipboard_text(target_name)
        press_hotkey(paste_hk)
        time.sleep(delays["select"])
        press_hotkey("enter")
        time.sleep(delays["select"])
        active_chat_name = target_name

    set_clipboard_text(text)
    press_hotkey(paste_hk)
    time.sleep(delays["paste"])
    press_hotkey(send_hk)
    time.sleep(delays["send"])
    return active_chat_name