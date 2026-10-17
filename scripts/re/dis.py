# usage: dis.py <hexaddr> [before_bytes] [after_bytes]
import sys, pefile
from capstone import *
EXE = r"C:\Users\Frost\Desktop\Projects\Game\Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
a = int(sys.argv[1],16); b=int(sys.argv[2],0) if len(sys.argv)>2 else 0x60; n=int(sys.argv[3],0) if len(sys.argv)>3 else 0x100
def rd(va,l): return data[pe.get_offset_from_rva(va-base):][:l]
md = Cs(CS_ARCH_X86, CS_MODE_64); md.detail=False
def strat(va):
    for enc in ('ascii','utf-16le'):
        try:
            raw=rd(va,200)
            s = raw.decode(enc,'ignore').split('\x00')[0] if enc=='ascii' else raw.decode('utf-16le','ignore').split('\x00')[0]
            if len(s)>=4 and s.isprintable(): return s
        except: pass
for i in md.disasm(rd(a-b,b+n), a-b):
    note=''
    if 'rip' in i.op_str:
        import re
        m=re.search(r'rip ([+-]) (0x[0-9a-f]+)',i.op_str)
        if m:
            t=i.address+i.size+(int(m.group(2),16)*(1 if m.group(1)=='+' else -1))
            s=strat(t); note=f'  ; ->{t:x}'+(f' "{s}"' if s else '')
    print(f'{i.address:x}: {i.mnemonic} {i.op_str}{note}')
