"""更可靠的控制台按键发送 v3：
- 先把游戏窗口强制前台
- 用 Esc 关闭可能已打开的旧控制台（清场）
- 再按 ` 打开控制台 -> 输入 -> Enter

用法: python send_cmd3.py "ap start"
"""
import ctypes, time, sys
from ctypes import wintypes

u32 = ctypes.WinDLL('user32', use_last_error=True)
k32 = ctypes.WinDLL('kernel32', use_last_error=True)

u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowW.restype = wintypes.HWND
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.SetForegroundWindow.restype = wintypes.BOOL
u32.ShowWindow.argtypes = [wintypes.HWND, wintypes.INT]
u32.BringWindowToTop.argtypes = [wintypes.HWND]
u32.GetForegroundWindow.restype = wintypes.HWND
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowThreadProcessId.restype = wintypes.DWORD
u32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
u32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.c_void_p]

cmd = sys.argv[1] if len(sys.argv) > 1 else "ap status"
close_first = "--clean" in sys.argv

hwnd = u32.FindWindowW(None, 'Slay the Spire 2')
if not hwnd:
    print("window not found"); sys.exit(1)

cur_fg = u32.GetForegroundWindow()
tid_fg = u32.GetWindowThreadProcessId(cur_fg, None)
tid_me = k32.GetCurrentThreadId()
u32.AttachThreadInput(tid_me, tid_fg, True)
u32.ShowWindow(hwnd, 9)
u32.BringWindowToTop(hwnd)
ok = u32.SetForegroundWindow(hwnd)
u32.AttachThreadInput(tid_me, tid_fg, False)
time.sleep(0.8)
print("foreground ok =", bool(ok), "hwnd =", hex(hwnd))

VK = {'`': 0xC0, ' ': 0x20, '-': 0xBD, ',': 0xBC, '.': 0xBE, '=': 0xBB, '/': 0xBF}
for ch in 'abcdefghijklmnopqrstuvwxyz':
    VK[ch] = 0x41 + (ord(ch) - ord('a'))
for ch in '0123456789':
    VK[ch] = 0x30 + (ord(ch) - ord('0'))

VK_ESC = 0x1B
VK_SHIFT = 0x10


def tap(vk, hold=0.05, gap=0.10):
    u32.keybd_event(vk, 0, 0, 0)
    time.sleep(hold)
    u32.keybd_event(vk, 0, 2, 0)
    time.sleep(gap)


def shift_tap(vk):
    u32.keybd_event(VK_SHIFT, 0, 0, 0)
    time.sleep(0.02)
    u32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    u32.keybd_event(vk, 0, 2, 0)
    time.sleep(0.02)
    u32.keybd_event(VK_SHIFT, 0, 2, 0)
    time.sleep(0.10)


# 清场：ESC 关掉任何已打开的浮层/控制台
if close_first:
    tap(VK_ESC, 0.06, 0.4)
    tap(VK_ESC, 0.06, 0.4)
    time.sleep(0.6)

tap(0xC0, 0.08, 0.7)   # 打开控制台
time.sleep(1.0)
for ch in cmd:
    if ch == ' ':
        tap(VK[' '])
    elif ch.isupper():
        shift_tap(VK[ch.lower()])
    else:
        vk = VK.get(ch)
        if vk:
            tap(vk)
time.sleep(0.5)
tap(0x0D)              # Enter
print("sent:", cmd)
