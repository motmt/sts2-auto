import ctypes, time, sys
from ctypes import wintypes

u32 = ctypes.WinDLL('user32', use_last_error=True)
g32 = ctypes.WinDLL('gdi32', use_last_error=True)

u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowW.restype = wintypes.HWND
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.SetForegroundWindow.restype = wintypes.BOOL
u32.ShowWindow.argtypes = [wintypes.HWND, wintypes.INT]
u32.ShowWindow.restype = wintypes.BOOL
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]

hwnd = u32.FindWindowW(None, 'Slay the Spire 2')
if not hwnd:
    print("window not found")
    sys.exit(1)

u32.ShowWindow(hwnd, 9)
time.sleep(0.4)
u32.SetForegroundWindow(hwnd)
time.sleep(1.2)

rect = wintypes.RECT()
u32.GetWindowRect(hwnd, ctypes.byref(rect))
w = rect.right - rect.left
h = rect.bottom - rect.top
print("window rect", rect.left, rect.top, w, h)

# 用 GDI BitBlt 抓窗口客户区
hdc_win = u32.GetDC(hwnd)
hdc_mem = g32.CreateCompatibleDC(hdc_win)
hbm = g32.CreateCompatibleBitmap(hdc_win, w, h)
g32.SelectObject(hdc_mem, hbm)

# PW_RENDERFULLCONTENT = 2（抓 DirectX/GPU 渲染内容，Godot 必需）
ok = u32.PrintWindow(hwnd, hdc_mem, 2)
if not ok:
    g32.BitBlt(hdc_mem, 0, 0, w, h, hdc_win, 0, 0, 0x00CC0020)

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]

bi = BITMAPINFOHEADER()
bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bi.biWidth = w
bi.biHeight = -h   # top-down
bi.biPlanes = 1
bi.biBitCount = 32
bi.biCompression = 0

buf = ctypes.create_string_buffer(w * h * 4)
got = g32.GetDIBits(hdc_mem, hbm, 0, h, buf, ctypes.byref(bi), 0)

out = sys.argv[1] if len(sys.argv) > 1 else r'D:\尖塔模组\game_shot.png'
# 写 PNG（无第三方库时用 BMP 兜底）
try:
    from PIL import Image
    img = Image.frombuffer('RGBA', (w, h), buf, 'raw', 'BGRA', 0, 1)
    img = img.convert('RGB')
    img.save(out)
    print('saved png', out)
except ImportError:
    bmp = out.rsplit('.', 1)[0] + '.bmp'
    # BMP 文件头
    row = w * 4
    size = row * h
    hdr = b'BM' + (54 + size).to_bytes(4, 'little') + b'\x00\x00\x00\x00' + (54).to_bytes(4, 'little')
    dib = (40).to_bytes(4, 'little') + w.to_bytes(4, 'little', signed=True) + h.to_bytes(4, 'little', signed=True) \
          + (1).to_bytes(2, 'little') + (32).to_bytes(2, 'little') + (0).to_bytes(4, 'little') \
          + size.to_bytes(4, 'little') + (2835).to_bytes(4, 'little') + (2835).to_bytes(4, 'little') \
          + (0).to_bytes(4, 'little') + (0).to_bytes(4, 'little')
    with open(bmp, 'wb') as f:
        f.write(hdr + dib + buf.raw)
    print('saved bmp (no PIL)', bmp)

g32.DeleteObject(hbm)
g32.DeleteDC(hdc_mem)
u32.ReleaseDC(hwnd, hdc_win)
