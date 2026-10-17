# usage: worldareas.py <AreaId>...   -- WorldAreas rows (Id, Name, Act, IsTown, HasWaypoint, Connections, AreaLevel, HASH16)
# read straight from a dumped Data/WorldAreas.datc64 (scripts/re/ggdump → Tools/re/out/gg/Data_WorldAreas.datc64).
import struct, sys
d=open(r'Tools/re/out/gg/Data_WorldAreas.datc64','rb').read()
n=struct.unpack_from('<I',d,0)[0]; mark=d.find(b'\xbb'*8); rs=(mark-4)//n; var=d[mark:]
def s(off):
    o=off; out=[]
    while o+1<len(var):
        c=struct.unpack_from('<H',var,o)[0]
        if c==0: break
        out.append(chr(c)); o+=2
    return ''.join(out)
rows=[d[4+i*rs:4+(i+1)*rs] for i in range(n)]
ids=[s(struct.unpack_from('<Q',r,0)[0]) for r in rows]
def row(i):
    r=rows[i]; o=0
    Id=s(struct.unpack_from('<Q',r,o)[0]); o+=8
    Name=s(struct.unpack_from('<Q',r,o)[0]); o+=8
    act=struct.unpack_from('<i',r,o)[0]; o+=4
    town=r[o]; o+=1; wp=r[o]; o+=1
    cnt,off=struct.unpack_from('<QQ',r,o); o+=16
    conns=[struct.unpack_from('<Q',var,off+8*k)[0] for k in range(cnt)]
    lvl=struct.unpack_from('<i',r,o)[0]; o+=4
    h16=struct.unpack_from('<H',r,o)[0]
    return Id,Name,act,town,wp,[ids[c] if c<len(ids) else hex(c) for c in conns],lvl,hex(h16)
if __name__=='__main__':
    print('rows',n,'rowsize',rs)
    for name in sys.argv[1:]:
        i=ids.index(name); print(i,row(i))
