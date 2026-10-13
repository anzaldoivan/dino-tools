import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Full static decode of room sections s1 and s7 across all rebirth ST*.DAT.
Goal: find the enemy/item/door placement record. Read-only.
Prints per-room records + cross-room field histograms."""
import sys, os, struct, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from dc2pkg import parse, GPC_LZSS0
REB = game_path("DC2", 'rebirth/Data')
BASE = 0x005e0000

def blob(p):
    ents,_=parse(open(p,"rb").read()); lz=[e for e in ents if e.type==GPC_LZSS0]
    return lz[0].decomp if lz else None
def slots(d):
    n=len(d); return [ (lambda v: None if v==0 else (v-BASE if BASE<=v<=BASE+n else None))(struct.unpack_from("<I",d,i*4)[0]) for i in range(12)]
def bounds(d,si):
    n=len(d); s=slots(d)
    if not isinstance(s[si],int): return None
    o=s[si]; pts=sorted(set(x for x in s if isinstance(x,int)))+[n]; return o, pts[pts.index(o)+1]-o

def decode_s1(d):
    """s1: u16 count, u16 hdr, then count * 20-byte records.
    record (as fields): u16 t, u16 k(=1000?), u32 id, s16 x, s16 _, s16 z, u16 flag, u16 p, u16 q"""
    bd=bounds(d,1)
    if not bd: return None,[]
    o,size=bd
    cnt=struct.unpack_from("<H",d,o)[0]
    hdr=struct.unpack_from("<H",d,o+2)[0]
    recs=[]
    p=o+4
    for i in range(cnt):
        if p+20>o+size+4: break
        t,k = struct.unpack_from("<HH",d,p)
        rid = struct.unpack_from("<I",d,p+4)[0]
        x,y,z,flag,pp,qq = struct.unpack_from("<6h",d,p+8) if p+20<=len(d) else (0,)*6
        recs.append(dict(t=t,k=k,id=rid,x=x,y=y,z=z,flag=flag&0xffff,p=pp,q=qq))
        p+=20
    return (cnt,hdr,size,o), recs

def decode_s7(d):
    """s7: characterize. Many rooms empty (first word 0)."""
    bd=bounds(d,7)
    if not bd: return None,None
    o,size=bd
    fw=struct.unpack_from("<I",d,o)[0]
    return (o,size,fw), d[o:o+min(size,48)]

if __name__=="__main__":
    mode = sys.argv[1] if len(sys.argv)>1 else "hist"
    rooms = sorted(glob.glob(os.path.join(REB,"ST[0-9]*.DAT")))
    if mode=="s1":
        # per-room s1 dump
        kvals=collections.Counter(); tvals=collections.Counter(); idvals=collections.Counter()
        flagvals=collections.Counter(); cnts=collections.Counter()
        idlo=collections.Counter()
        for p in rooms:
            d=blob(p); hdr,recs=decode_s1(d)
            if hdr is None: continue
            cnt,h,size,o=hdr; cnts[cnt]+=1
            for r in recs:
                kvals[r['k']]+=1; tvals[r['t']]+=1; idvals[r['id']]+=1
                flagvals[r['flag']]+=1; idlo[r['id']&0xff]+=1
        print("=== s1 across all rooms ===")
        print("count-per-room dist:", dict(sorted(cnts.items())))
        print("t  (off+0) hist:", dict(tvals.most_common(12)))
        print("k  (off+2) hist:", dict(kvals.most_common(12)))
        print("id (off+4) hist (top):", dict(idvals.most_common(20)))
        print("id low byte hist:", dict(sorted(idlo.items())))
        print("flag(off+14) hist:", dict(flagvals.most_common(12)))
    elif mode=="s1dump":
        for nm in sys.argv[2:] or ["ST100.DAT","ST300.DAT","ST503.DAT","ST205.DAT"]:
            d=blob(os.path.join(REB,nm)); hdr,recs=decode_s1(d)
            if hdr is None: print(f"{nm}: no s1"); continue
            cnt,h,size,o=hdr
            print(f"\n### {nm} s1 @0x{o:x} size=0x{size:x} count={cnt} hdr=0x{h:x}")
            for r in recs:
                print(f"   t={r['t']:>4} k={r['k']:>5} id={r['id']:>6} (0x{r['id']:x})  "
                      f"x={r['x']:>6} y={r['y']:>6} z={r['z']:>6} flag=0x{r['flag']:04x} p={r['p']} q={r['q']}")
    elif mode=="s7":
        present=0; nonempty=0
        sizes=collections.Counter()
        for p in rooms:
            d=blob(p); meta,head=decode_s7(d)
            if meta is None: continue
            present+=1; o,size,fw=meta
            if fw!=0 and size>4:
                nonempty+=1
                print(f"{os.path.basename(p):12} s7 size=0x{size:x} fw=0x{fw:x}  {head[:24].hex()}")
            sizes[size if size<0x20 else 'big']+=1
        print(f"\ns7 present={present} nonempty={nonempty}; size dist={dict(sizes)}")
