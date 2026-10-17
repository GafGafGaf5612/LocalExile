# usage: finddisp.py <lo_va> <hi_va> <substring>   -- disassemble [lo,hi) and print the 6 instructions before every
# instruction whose operands contain <substring> (e.g. "+ 0x980]"); used to find writers of a struct field
import sys, pefile
from capstone import *
EXE = r"C:\Users\Frost\Desktop\Projects\Game\Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
lo=int(sys.argv[1],16); hi=int(sys.argv[2],16); pat=sys.argv[3]
off=pe.get_offset_from_rva(lo-base)
md=Cs(CS_ARCH_X86, CS_MODE_64); md.skipdata=True
prev=[]
for i in md.disasm(data[off:off+(hi-lo)], lo):
    prev.append(f'{i.address:x}: {i.mnemonic} {i.op_str}'); prev=prev[-6:]
    if pat in i.op_str: print('\n'.join(prev)); print('--')
