import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Decode the s2 offset-table sub-records of rebirth ST*.DAT rooms.
s2 = u32 count, then offset array (relative to section start), then sub-records.
Goal: are sub-records event/script bytecode? enemy ids? Read-only."""
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

def parse_s2(d):
    """Return (off,size,count, offsets[], records[(start,end,bytes)])."""
    bd=bounds(d,2)
    if not bd: return None
    o,size=bd
    cnt=struct.unpack_from("<I",d,o)[0]
    maxcnt=max(0,(size-4)//4)
    if cnt>maxcnt: cnt=maxcnt   # guard against non-count first words
    # read cnt offsets after the count word; treat as relative to section start
    offs=[struct.unpack_from("<I",d,o+4+i*4)[0] for i in range(cnt) if o+4+i*4+4<=len(d)]
    # data region begins after count + cnt*4; records bounded by consecutive offsets (+ section end)
    bounds_list = sorted(set(offs)) + [size]
    recs=[]
    for x in sorted(set(offs)):
        if x<size:
            end = bounds_list[bounds_list.index(x)+1]
            recs.append((x, end, d[o+x:o+end]))
    return o,size,cnt,offs,recs

def hx(b,n=48):
    return " ".join(f"{x:02x}" for x in b[:n])

if __name__=="__main__":
    mode=sys.argv[1] if len(sys.argv)>1 else "dump"
    rooms=sorted(glob.glob(os.path.join(REB,"ST[0-9]*.DAT")))
    if mode=="dump":
        for nm in sys.argv[2:] or ["ST100.DAT","ST300.DAT","ST503.DAT"]:
            d=blob(os.path.join(REB,nm)); r=parse_s2(d)
            if not r: print(f"{nm}: no s2"); continue
            o,size,cnt,offs,recs=r
            print(f"\n### {nm}  s2 @0x{o:x} size=0x{size:x} count={cnt}")
            print(f"   offsets: {[hex(x) for x in offs]}")
            for st,en,b in recs:
                print(f"   rec @+0x{st:<4x} len={en-st:<4} : {hx(b,40)}")
    elif mode=="stats":
        # cross-room: record-length distribution, leading-byte histogram (opcode candidate)
        lens=collections.Counter(); lead=collections.Counter(); cnts=collections.Counter()
        # also: do record lengths look fixed (records) or variable (bytecode)?
        for p in rooms:
            d=blob(p); r=parse_s2(d)
            if not r: continue
            o,size,cnt,offs,recs=r; cnts[cnt]+=1
            for st,en,b in recs:
                lens[en-st]+=1
                if b: lead[b[0]]+=1
        print("count-per-room dist:", dict(sorted(cnts.items())))
        print("record-length dist (top):", dict(lens.most_common(20)))
        print("record leading-byte hist (top):", dict(lead.most_common(20)))
    elif mode=="ids":
        # search s2 sub-records for the E-file id space {0x00,0x10,...,0xA0} as a 1- or 2-byte field
        # and look at the first u16/u32 of each record
        first_u16=collections.Counter()
        for p in rooms:
            d=blob(p); r=parse_s2(d)
            if not r: continue
            o,size,cnt,offs,recs=r
            for st,en,b in recs:
                if len(b)>=2: first_u16[struct.unpack_from("<H",b,0)[0]]+=1
        print("s2 record first-u16 hist (top 30):", dict(first_u16.most_common(30)))
