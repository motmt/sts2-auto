"""更稳的控制台按键发送：逐字符、长间隔、支持退格修正。

用法: python send_cmd.py "ap status"
"""
import ctypes, time, sys
from ctypes import wintypes

u32 = ctypes.WinDLL('user32', use_last_error=True)
u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowW.restype = wintypes.HWND
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.SetForegroundWindow.restype = wintypes.BOOL
u32.ShowWindow.argtypes = [wintypes.HWND, wintypes.INT]
u32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.c_void_p]

cmd = sys.argv[1] if len(sys.argv) > 1 else "ap status"

hwnd = u32.FindWindowW(None, 'Slay the Spire 2')
if not hwnd:
    print("window not found"); sys.exit(1)

u32.ShowWindow(hwnd, 9)          # SW_RESTORE
time.sleep(0.5)
u32.SetForegroundWindow(hwnd)
time.sleep(1.2)

VK = {
    '`': 0xC0, '~': 0xC0, ' ': 0x20, '-': 0xBD, '_': 0xBD, ',': 0xBC, '.': 0xBE,
    'a': 0x41, 'b': 0x42, 'c': 0x43, 'd': 0x44, 'e': 0x45, 'f': 0x46, 'g': 0x47,
    'h': 0x48, 'i': 0x49, 'j': 0x4A, 'k': 0x4B, 'l': 0x4C, 'm': 0x4D, 'n': 0x4E,
    'o': 0x4F, 'p': 0x50, 'q': 0x51, 'r': 0x52, 's': 0x53, 't': 0x54, 'u': 0x55,
    'v': 0x56, 'w': 0x57, 'x': 0x58, 'y': 0x59, 'z': 0x5A,
    '0': 0x30, '1': 0x31, '2': 0x32, '3': 0x33, '4': 0x34,
    '5': 0x35, '6': 0x36, '7': 0x37, '8': 0x38, '9': 0x39,
}


def tap(vk, hold=0.06, gap=0.14):
    u32.keybd_event(vk, 0, 0, 0)
    time.sleep(hold)
    u32.keybd_event(vk, 0, 2, 0)
    time.sleep(gap)


# 打开控制台
tap(0xC0, 0.08, 0.5)
time.sleep(0.9)

for ch in cmd:
    vk = VK.get(ch)
    if vk:
        tap(vk)

time.sleep(0.5)
tap(0x0D)  # Enter
print("sent:", cmd)
