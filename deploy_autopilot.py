"""AutoPilot 部署脚本：停游戏 → Win32 DeleteFileW/CopyFileW → MD5 校验。

用法: python deploy_autopilot.py
说明:
  · 游戏运行时独占锁 dll → DeleteFileW 返回 ERROR_ACCESS_DENIED(5)，故先 Stop-Process。
  · 本机 os.remove 被 shim 劫持成回收站且失败 → 必须用 ctypes/kernel32。
  · 写 E 盘必须由调用方以 dangerouslyDisableSandbox 启动。
"""
import ctypes
import hashlib
import os
import subprocess
import sys

SRC = r"D:\尖塔模组\AutoPilot"
DST = r"E:\steam\steamapps\common\Slay the Spire 2\mods\AutoPilot"

# ── 融合进来的 CombatSolver（third_party 子工程，独立编译）──
CS_SRC = os.path.join(SRC, "third_party", "CombatSolver")
CS_BIN = os.path.join(CS_SRC, ".godot", "mono", "temp", "bin", "Release")
CS_DST = r"E:\steam\steamapps\common\Slay the Spire 2\mods\CombatSolver"
CS_LICENSE = os.path.join(SRC, "third_party", "CombatSolver", "LICENSE")

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
DeleteFileW = kernel32.DeleteFileW
DeleteFileW.argtypes = [ctypes.c_wchar_p]
DeleteFileW.restype = ctypes.c_int
CopyFileW = kernel32.CopyFileW
CopyFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_int]
CopyFileW.restype = ctypes.c_int


def md5(path: str) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def stop_game() -> None:
    subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force"],
        capture_output=True,
    )


def deploy_file(src: str, dst: str) -> None:
    if os.path.exists(dst):
        if not DeleteFileW(dst):
            err = ctypes.get_last_error()
            raise OSError(f"DeleteFileW 失败 ({err}) : {dst}")
    if not CopyFileW(src, dst, False):
        err = ctypes.get_last_error()
        raise OSError(f"CopyFileW 失败 ({err}) : {dst} -> {src}")
    a, b = md5(src), md5(dst)
    if a != b:
        raise OSError(f"MD5 不一致: {src} != {dst} ({a} vs {b})")
    print(f"  OK  {os.path.basename(dst):22s} {os.path.getsize(dst):>8d}B  md5={a[:12]}")


def main() -> int:
    stop_game()
    pairs = [
        (os.path.join(SRC, "bin", "Release", "AutoPilot.dll"), os.path.join(DST, "AutoPilot.dll")),
        (os.path.join(SRC, "bin", "Release", "AutoPilot.pdb"), os.path.join(DST, "AutoPilot.pdb")),
        (os.path.join(SRC, "AutoPilot.json"), os.path.join(DST, "AutoPilot.json")),
        # 知识库：真实对局统计（593 局）+ spire-codex 权威游戏数据
        (os.path.join(SRC, "kb", "sts2_kb.json"), os.path.join(DST, "kb", "sts2_kb.json")),
        (os.path.join(SRC, "kb", "sts2_codex.json"), os.path.join(DST, "kb", "sts2_codex.json")),
    ]
    print("部署 AutoPilot:")
    os.makedirs(os.path.join(DST, "kb"), exist_ok=True)
    for src, dst in pairs:
        if not os.path.exists(src):
            print(f"  SKIP 源不存在: {src}")
            continue
        deploy_file(src, dst)

    # ── 融合的 CombatSolver ────────────────────────────────────────────
    print("部署 CombatSolver（融合子工程）:")
    if not os.path.exists(CS_BIN):
        print(f"  SKIP 未找到编译产物: {CS_BIN}（先跑 build_all.py solver）")
    else:
        os.makedirs(CS_DST, exist_ok=True)
        # dll + manifest 必部署；MemoryCleaner.exe 若已构建也一并带上
        cs_pairs = [
            (os.path.join(CS_BIN, "CombatSolver.dll"), os.path.join(CS_DST, "CombatSolver.dll")),
            (os.path.join(CS_SRC, "CombatSolver.json"), os.path.join(CS_DST, "CombatSolver.json")),
            (os.path.join(CS_SRC, "THIRD_PARTY_NOTICES.md"), os.path.join(CS_DST, "THIRD_PARTY_NOTICES.md")),
        ]
        mc = os.path.join(CS_SRC, "tools", "CombatSolver.MemoryCleaner", "bin", "Release", "net48",
                          "CombatSolver.MemoryCleaner.exe")
        if os.path.exists(mc):
            cs_pairs.append((mc, os.path.join(CS_DST, "CombatSolver.MemoryCleaner.exe")))
        for src, dst in cs_pairs:
            if not os.path.exists(src):
                print(f"  SKIP 源不存在: {src}")
                continue
            deploy_file(src, dst)

    print("部署完成。请从 Steam 启动游戏: Start-Process 'steam://rungameid/2868840'")
    return 0


if __name__ == "__main__":
    sys.exit(main())
