# -*- coding: utf-8 -*-
import urllib.request, json, ssl, os, base64, tarfile, io

ctx = ssl.create_default_context(); ctx.check_hostname=False; ctx.verify_mode=ssl.CERT_NONE

def fetch(url, tries=3):
    last=None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={'User-Agent':'Mozilla/5.0'})
            return urllib.request.urlopen(req, context=ctx, timeout=90).read()
        except Exception as e:
            last=e
    raise last

base = r'D:\尖塔模组\.refs'
for repo in ['hotwords123/StS2.RandomForeseer', 'Torch1230/CombatSolver']:
    name = repo.split('/')[1]
    root = os.path.join(base, name)
    os.makedirs(root, exist_ok=True)
    ok=False
    for br in ['main','master']:
        try:
            data = fetch(f'https://codeload.github.com/{repo}/tar.gz/refs/heads/{br}')
            tf = tarfile.open(fileobj=io.BytesIO(data), mode='r:gz')
            n=0
            for m in tf.getmembers():
                if not m.isfile(): continue
                if not (m.name.endswith('.cs') or m.name.endswith('.md') or m.name.endswith('.json') or m.name.endswith('.csproj')):
                    continue
                # strip top dir
                parts=m.name.split('/',1)
                rel = parts[1] if len(parts)>1 else parts[0]
                dst=os.path.join(root, rel)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                f=tf.extractfile(m)
                open(dst,'wb').write(f.read())
                n+=1
            print(f'== {name} ({br}): {n} files, tarball {len(data)}B')
            ok=True
            break
        except Exception as e:
            print(f'  {name} {br} ERR {type(e).__name__} {e}')
    if not ok:
        print(f'!! {name} FAILED')
print('done')
