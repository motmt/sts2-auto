import ctypes, ctypes.wintypes as wt, sys, time

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# find game window
hwnds = []
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
def cb(h, l):
    if not user32.IsWindowVisible(h):
        return True
    n = user32.GetWindowTextLengthW(h)
    if n == 0:
        return True
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(h, buf, n + 1)
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(h, cls, 256)
    if 'Slay' in buf.value or 'Spire' in buf.value or 'Godot' in cls.value:
        r = wt.RECT()
        user32.GetWindowRect(h, ctypes.byref(r))
        hwnds.append((h, buf.value, cls.value, (r.left, r.top, r.right, r.bottom)))
    return True

user32.EnumWindows(WNDENUMPROC(cb), 0)
print('windows found:', len(hwnds))
for h, t, c, r in hwnds:
    print(f'  hwnd={h} title={t!r} class={c!r} rect={r}')

if not hwnds:
    # fallback: biggest visible window
    print('no match; dumping all visible titled windows:')
    def cb2(h, l):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, buf, n + 1)
                r = wt.RECT(); user32.GetWindowRect(h, ctypes.byref(r))
                w = r.right - r.left; hh = r.bottom - r.top
                if w > 400 and hh > 300:
                    print(f'   hwnd={h} {buf.value!r} {w}x{hh}')
        return True
    user32.EnumWindows(WNDENUMPROC(cb2), 0)
    sys.exit(1)

h, title, cls, (l, t, r, b) = hwnds[0]
w, hh = r - l, b - t
print('capturing', w, 'x', hh)

# bring to front
user32.SetForegroundWindow(h)
time.sleep(0.4)

hdc = user32.GetWindowDC(h)
memdc = gdi32.CreateCompatibleDC(hdc)
bmp = gdi32.CreateCompatibleBitmap(hdc, w, hh)
gdi32.SelectObject(memdc, bmp)
# PW_RENDERFULLCONTENT = 0x2
ok = user32.PrintWindow(h, memdc, 2)
print('PrintWindow ok=', ok)

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long), ('biHeight', ctypes.c_long),
                ('biPlanes', wt.WORD), ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD), ('biClrImportant', wt.DWORD)]

bi = BITMAPINFOHEADER()
bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bi.biWidth = w
bi.biHeight = -hh  # top-down
bi.biPlanes = 1
bi.biBitCount = 32
bi.biCompression = 0

bufsize = w * hh * 4
buf = ctypes.create_string_buffer(bufsize)
got = gdi32.GetDIBits(memdc, bmp, 0, hh, buf, ctypes.byref(bi), 0)
print('GetDIBits rows=', got)

# save as BMP
import struct
out = open(r'D:\尖塔模组\game_shot.bmp', 'wb')
filesize = 14 + 40 + bufsize
out.write(b'BM')
out.write(struct.pack('<IHHI', filesize, 0, 0, 54))
out.write(struct.pack('<IiiHHIIiiII', 40, w, hh, 1, 32, 0, bufsize, 0, 0, 0, 0))
out.write(buf.raw)
out.close()
print('saved D:\\尖塔模组\\game_shot.bmp')

gdi32.DeleteObject(bmp)
gdi32.DeleteDC(memdc)
user32.ReleaseDC(h, hdc)

# convert to png
try:
    from PIL import Image
    im = Image.open(r'D:\尖塔模组\game_shot.bmp')
    im.save(r'D:\尖塔模组\game_shot.png')
    print('saved game_shot.png', im.size)
except Exception as e:
    print('PIL not available:', e)
