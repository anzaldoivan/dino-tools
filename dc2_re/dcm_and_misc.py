import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
import struct, sys
from dc2pkg import parse, GPC_LZSS0, GPC_DATA, PC_NAMES

def hx(b,off=0,n=128):
    out=[]
    for i in range(0,min(len(b)-off,n),16):
        c=b[off+i:off+i+16]
        out.append(f"  {off+i:06x}: "+" ".join(f'{x:02x}' for x in c)+"  "+"".join(chr(x) if 32<=x<127 else '.' for x in c))
    return "\n".join(out)

def dcm(path):
    with open(path,"rb") as f: data=f.read()
    ents,_=parse(data)
    lz=[e for e in ents if e.type==GPC_LZSS0]
    if not lz:
        print(f"{path}: no LZSS0"); return
    d=lz[0].decomp
    vptr,nptr,tptr,qptr=struct.unpack_from("<IIII",d,0)
    tri_cnt,quad_cnt=struct.unpack_from("<HH",d,16)
    obj_cnt=struct.unpack_from("<I",d,20)
    tpage,clut=struct.unpack_from("<HH",d,24)
    base=struct.unpack_from("<I",lz[0].reserve,0)[0]
    print(f"\n### {path}  DCM blob {len(d)}B base=0x{base:08x}")
    print(f"  vertex_ptr=0x{vptr:08x} normal_ptr=0x{nptr:08x} tri_ptr=0x{tptr:08x} quad_ptr=0x{qptr:08x}")
    print(f"  tri_cnt={tri_cnt} quad_cnt={quad_cnt} obj_cnt={obj_cnt} tpage={tpage} clut={clut}")
    print(f"  rebased: vptr-base=0x{vptr-base:x}")
    print(hx(d,0,64))
    # trailing DATA blob
    dt=[e for e in ents if e.type==GPC_DATA]
    if dt:
        e=dt[-1]
        print(f"  trailing DATA size={e.size} res0=0x{struct.unpack_from('<I',e.reserve,0)[0]:08x}")
        print(hx(e.data,0,min(e.size,96)))

if __name__=="__main__":
    mode=sys.argv[1]
    if mode=="dcm":
        for p in sys.argv[2:]: dcm(p)
    elif mode=="raw":
        for p in sys.argv[2:]:
            with open(p,"rb") as f: data=f.read()
            print(f"\n### RAW {p} ({len(data)} B)"); print(hx(data,0,256))
    elif mode=="door":
        for p in sys.argv[2:]:
            with open(p,"rb") as f: data=f.read()
            ents,_=parse(data)
            for e in ents:
                if e.type==GPC_DATA:
                    print(f"\n### {p} DATA size={e.size} res0=0x{struct.unpack_from('<I',e.reserve,0)[0]:08x}")
                    print(hx(e.data,0,e.size))
