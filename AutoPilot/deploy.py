import ctypes, os, shutil
from ctypes import wintypes

SRC = r'D:\尖塔模组\AutoPilot'
DST = r'E:\steam\steamapps\common\Slay the Spire 2\mods\AutoPilot'

os.makedirs(DST, exist_ok=True)

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
DeleteFileW = k32.DeleteFileW
DeleteFileW.argtypes = [wintypes.LPCWSTR]
DeleteFileW.restype = wintypes.BOOL

CopyFileW = k32.CopyFileW
CopyFileW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.BOOL]
CopyFileW.restype = wintypes.BOOL


def deploy(src_file, dst_file):
    if os.path.exists(dst_file):
        ok = DeleteFileW(dst_file)
        if not ok:
            err = ctypes.get_last_error()
            raise OSError(f'DeleteFileW failed ({err}) on {dst_file}')
    ok = CopyFileW(src_file, dst_file, False)
    if not ok:
        err = ctypes.get_last_error()
        raise OSError(f'CopyFileW failed ({err}) -> {dst_file}')
    print(f'OK  {dst_file}  ({os.path.getsize(dst_file)} bytes)')


deploy(os.path.join(SRC, 'bin', 'Release', 'AutoPilot.dll'), os.path.join(DST, 'AutoPilot.dll'))
deploy(os.path.join(SRC, 'AutoPilot.json'), os.path.join(DST, 'AutoPilot.json'))

print('\n--- mods/AutoPilot ---')
for f in sorted(os.listdir(DST)):
    p = os.path.join(DST, f)
    print(f'  {f}  {os.path.getsize(p)}')
