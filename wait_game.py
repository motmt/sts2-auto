import os, time, sys, datetime

log = os.path.join(os.environ['APPDATA'], 'SlayTheSpire2', 'logs', 'godot.log')
base = os.path.getmtime(log) if os.path.exists(log) else 0
print('baseline godot.log mtime:', datetime.datetime.fromtimestamp(base))

deadline = time.time() + 180
started = False
while time.time() < deadline:
    if os.path.exists(log):
        m = os.path.getmtime(log)
        if m > base + 1:  # file rewritten -> game booted
            started = True
            print('GAME_BOOTED at', datetime.datetime.fromtimestamp(m))
            break
    time.sleep(3)

if not started:
    print('TIMEOUT: godot.log not rewritten within 180s')
    sys.exit(1)

# dump the mod-loading lines
txt = open(log, encoding='utf-8', errors='ignore').read()
lines = txt.splitlines()
keep = []
for ln in lines:
    if any(k in ln for k in ('Loading assembly DLL', 'CombatSolver', 'AutoPilot', 'Finished mod initialization', 'Calling initializer', 'applied,', 'failed')):
        keep.append(ln)
print('---- mod loading lines ({}) ----'.format(len(keep)))
for ln in keep[-60:]:
    print(ln)
