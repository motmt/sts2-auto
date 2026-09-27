import os, time, sys, datetime, subprocess

log = os.path.join(os.environ['APPDATA'], 'SlayTheSpire2', 'logs', 'godot.log')
base = os.path.getmtime(log) if os.path.exists(log) else 0
print('baseline godot.log mtime:', datetime.datetime.fromtimestamp(base), flush=True)
print('now:', datetime.datetime.now(), flush=True)

deadline = time.time() + 150
started = False
while time.time() < deadline:
    r = subprocess.run(['tasklist'], capture_output=True)
    running = 'SlayTheSpire2' in r.stdout.decode('gbk', 'ignore')
    if os.path.exists(log) and os.path.getmtime(log) > base + 1:
        started = True
        print('GAME_BOOTED at', datetime.datetime.fromtimestamp(os.path.getmtime(log)), flush=True)
        break
    if running:
        print('  ...process alive, waiting for log rewrite', datetime.datetime.now().strftime('%H:%M:%S'), flush=True)
    else:
        print('  ...process NOT running', datetime.datetime.now().strftime('%H:%M:%S'), flush=True)
    time.sleep(6)

if not started:
    print('TIMEOUT', flush=True)
    sys.exit(1)

time.sleep(2)
txt = open(log, encoding='utf-8', errors='ignore').read()
keep = []
for ln in txt.splitlines():
    if any(k in ln for k in ('Loading assembly DLL', 'CombatSolver', 'AutoPilot', 'Finished mod initialization',
                             'Calling initializer', 'applied,', 'ModEntry', 'SETTINGS_LOADED', '求解')):
        keep.append(ln)
print('---- relevant lines ({}) ----'.format(len(keep)), flush=True)
for ln in keep[-70:]:
    print(ln, flush=True)
