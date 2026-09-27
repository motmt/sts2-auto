"""融合构建：一次编译 AutoPilot + CombatSolver 两个工程。

用法:
    python build_all.py            # 构建两个工程
    python build_all.py autopilot  # 只构建 AutoPilot
    python build_all.py solver     # 只构建 CombatSolver

背景:
    CombatSolver 已融合进本仓库（AutoPilot/third_party/CombatSolver）。
    它必须用 Godot.NET.Sdk + Krafs.Publicizer 独立编译（公开化 sts2.dll 内部成员），
    因此不能并入 AutoPilot.csproj，只能作为同级子工程编译。
"""
import os
import subprocess
import sys

WS = r"D:\尖塔模组"
DOTNET = os.path.join(WS, ".tools", "dotnet9", "dotnet.exe")
AP_DIR = os.path.join(WS, "AutoPilot")
CS_DIR = os.path.join(AP_DIR, "third_party", "CombatSolver")


def build(proj_dir, tag):
    log = os.path.join(WS, f"build_{tag}.log")
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.join(WS, ".tools", "dotnet9")
    cmd = [DOTNET, "build", "-c", "Release", "-nodeReuse:false", "-m:1", "-v:m"]
    print(f"[{tag}] building {proj_dir} ...")
    with open(log, "w", encoding="utf-8", errors="replace") as f:
        p = subprocess.run(cmd, cwd=proj_dir, env=env, stdout=f, stderr=subprocess.STDOUT)
    text = open(log, "r", encoding="utf-8", errors="replace").read()
    errs = [l for l in text.splitlines() if " error " in l or l.strip().startswith("error")]
    warns = [l for l in text.splitlines() if " warning " in l]
    ok = "Build succeeded" in text or ".dll" in text and not errs
    print(f"[{tag}] exit={p.returncode} errors={len(errs)} warnings={len(warns)} log={log}")
    for l in errs[:25]:
        print("   ERR", l.strip()[:200])
    return p.returncode == 0 and not errs


def main():
    which = sys.argv[1].lower() if len(sys.argv) > 1 else "all"
    results = {}
    if which in ("all", "autopilot", "ap"):
        results["autopilot"] = build(AP_DIR, "autopilot")
    if which in ("all", "solver", "cs"):
        results["solver"] = build(CS_DIR, "solver")
    print("\n=== RESULT ===")
    for k, v in results.items():
        print(f"  {k}: {'OK' if v else 'FAIL'}")
    sys.exit(0 if all(results.values()) else 1)


if __name__ == "__main__":
    main()
