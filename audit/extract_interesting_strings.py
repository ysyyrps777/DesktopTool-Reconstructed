import re, pathlib, json, sys
paths=[pathlib.Path(p) for p in sys.argv[1:]]
keywords=re.compile(r'(?i)(https?://|powershell|cmd\.exe|schtasks|appdata|registry|runonce|currentversion|delete|uninstall|desktoptool|everything|github|gitlab|gitee|upload|download|socket|webhook|token|password|secret)')
for path in paths:
    data=path.read_bytes()
    vals=[]
    for enc,pat in [('ascii', rb'[\x20-\x7e]{6,}'),('utf16le', rb'(?:[\x20-\x7e\x80-\xff]\x00){6,}')]:
        for m in re.finditer(pat,data):
            raw=m.group()
            try: s=raw.decode('latin1' if enc=='ascii' else 'utf-16le',errors='ignore')
            except: continue
            if keywords.search(s): vals.append((enc,s[:1000]))
    print(f'===== {path} ({len(data)} bytes) =====')
    for enc,s in vals[:500]: print(f'[{enc}] {s}')
