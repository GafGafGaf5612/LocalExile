# usage: vt.py <vtable_va> [...]   -- print slots 0-5 of each vtable (Client/Game.exe)
import sys, struct, pefile
EXE = r"C:\Users\Frost\Desktop\Projects\Game\Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
for a in sys.argv[1:]:
    va=int(a,16); off=pe.get_offset_from_rva(va-base)
    print(a, [hex(struct.unpack_from('<Q',data,off+8*i)[0]) for i in range(6)])
