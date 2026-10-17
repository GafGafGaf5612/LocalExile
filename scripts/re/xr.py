# usage: xr.py <string> [ctx]  -- find string (ascii/utf16) in PE, list code xrefs (RIP-relative), disasm context
import sys, struct, pefile, re
from capstone import *
import os
EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
def rva2off(rva): return pe.get_offset_from_rva(rva)
def off2rva(off): return pe.get_rva_from_offset(off)
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
tb = data[text.PointerToRawData:text.PointerToRawData+text.SizeOfRawData]; trva=text.VirtualAddress
needle = sys.argv[1]
pats = [needle.encode(), needle.encode('utf-16le')]
addrs=[]
for p in pats:
    for m in re.finditer(re.escape(p), data):
        try: addrs.append((off2rva(m.start()), p is pats[1]))
        except: pass
print("string hits:", [(hex(base+a),u) for a,u in addrs])
# scan lea/mov reg,[rip+disp32]: opcodes 48/4C 8D modrm(05|0D|15..) disp32
for a,u in addrs:
    for i in range(len(tb)-7):
        if tb[i] in (0x48,0x4c) and tb[i+1] in (0x8d,0x8b) and (tb[i+2]&0xC7)==0x05:
            d=struct.unpack_from('<i',tb,i+3)[0]
            if trva+i+7+d==a: print("xref code @", hex(base+trva+i))
