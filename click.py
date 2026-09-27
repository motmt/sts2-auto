import ctypes, ctypes.wintypes as wt, sys, time

u = ctypes.windll.user32
# find game window
target = [None]
def cb(h, l):
    if u.IsWindowVisible(h):
        n = u.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            if b.value == 'Slay the Spire 2':
                target[0] = h
    return True
u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(cb), 0)
h = target[0]
print('hwnd', h)
if not h:
    sys.exit(1)

# foreground
u.SetForegroundWindow(h)
time.sleep(0.2)

# click at normalized coords (x,y in 0..1 of client area)
pts = sys.argv[1:]
r = wt.RECT()
u.GetClientRect(h, ctypes.byref(r))
cw, ch = r.right, r.bottom
print('client', cw, ch)

SCREEN = (0, 0, 1538, 864)

def click(nx, ny):
    sx = int(nx * cw)
    sy = int(ny * ch)
    # client -> screen
    pt = wt.POINT(sx, sy)
    u.ClientToScreen(h, ctypes.byref(pt))
    print('click norm(%.3f,%.3f) client(%d,%d) screen(%d,%d)' % (nx, ny, sx, sy, pt.x, pt.y))
    u.SetCursorPos(pt.x, pt.y)
    time.sleep(0.15)
    u.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.05)
    u.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
    time.sleep(0.3)

for arg in pts:
    parts = arg.split(',')
    click(float(parts[0]), float(parts[1]))
print('done')
