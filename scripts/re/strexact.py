# usage: strexact.py <name> [...]   -- addresses of exact NUL-terminated ASCII (a:) and UTF-16LE (u:) strings
import sys, re, pefile
EXE = r"C:\Users\Frost\Desktop\Projects\Game\Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
for n in sys.argv[1:]:
    out=[]
    for enc,term in (('ascii',b'\0'),('utf-16le',b'\0\0')):
        p=n.encode(enc)
        for m in re.finditer(re.escape(p)+re.escape(term), data):
            prev=data[m.start()-(1 if enc=='ascii' else 2):m.start()]
            if prev.strip(b'\0'): continue
            try: out.append(f'{enc[0]}:{base+pe.get_rva_from_offset(m.start()):#x}')
            except: pass
    print(n, ' '.join(out))
