import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Decode the object-candidate sections (s7,s8) of rebirth ST*.DAT rooms.
Hypothesis: count-led list of sub-pointers -> typed records (type tag + XYZ).
Read-only. Dumps structure + a cross-room histogram of the 2-byte type tag."""
import sys, os, struct, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from dc2pkg import parse, GPC_LZSS0
REB = game_path("DC2", 'rebirth/Data')
BASE = 0x005e0000

def blob(path):
    ents,_ = parse(open(path,"rb").read())
    lz=[e for e in ents if e.type==GPC_LZSS0]
    return lz[0].decomp if lz else None

def slots(d):
    n=len(d); out=[]
    for i in range(12):
        v=struct.unpack_from("<I",d,i*4)[0]
        out.append(None if v==0 else (v-BASE if BASE<=v<=BASE+n else None))
    return out

def sec_bounds(d, si):
    n=len(d); s=slots(d)
    if not isinstance(s[si],int): return None
    o=s[si]; pts=sorted(set(x for x in s if isinstance(x,int))) + [n]
    return o, pts[pts.index(o)+1]-o

def hexdump(b,off,n):
    out=[]
    for i in range(0,min(len(b)-off,n),16):
        c=b[off+i:off+i+16]
        out.append(f"    {off+i:05x}: "+" ".join(f"{x:02x}" for x in c))
    return "\n".join(out)

def decode_s8(path, verbose=False):
    d=blob(path);
    if not d: return []
    bd=sec_bounds(d,8)
    if not bd: return []
    o,size=bd
    cnt=struct.unpack_from("<I",d,o)[0]
    recs=[]
    if verbose:
        print(f"\n### {os.path.basename(path)}  s8 @0x{o:x} size=0x{size:x} count={cnt}")
        print(hexdump(d,o,min(size,96)))
    # array of `cnt` u32 sub-pointers right after the count
    subptrs=[]
    for i in range(cnt):
        v=struct.unpack_from("<I",d,o+4+i*4)[0]
        if BASE<=v<BASE+len(d): subptrs.append(v-BASE)
    if verbose: print(f"    subptrs={[hex(x) for x in subptrs]}")
    # each sub-pointer -> a record; read the 2-byte 'tag' at +0x2 (the `xx 00 TT 00` shape)
    for sp in subptrs:
        if sp+4<=len(d):
            w0=struct.unpack_from("<H",d,sp)[0]
            tag=struct.unpack_from("<H",d,sp+2)[0]
            recs.append((w0,tag,sp))
            if verbose:
                print(f"    rec@0x{sp:x}: w0={w0} tag=0x{tag:x}")
                print(hexdump(d,sp,32))
    return recs

if __name__=="__main__":
    args=sys.argv[1:]
    if args and args[0]=="hist":
        # cross-room: histogram of tags + count of rooms with s8
        taghist=collections.Counter(); w0hist=collections.Counter()
        nrooms_s8=0; cnts=collections.Counter()
        for p in sorted(glob.glob(os.path.join(REB,"ST[0-9]*.DAT"))):
            d=blob(p)
            if not d: continue
            bd=sec_bounds(d,8)
            if not bd: continue
            nrooms_s8+=1
            cnt=struct.unpack_from("<I",d,bd[0])[0]; cnts[cnt]+=1
            for w0,tag,sp in decode_s8(p):
                taghist[tag]+=1; w0hist[w0]+=1
        print(f"rooms with s8 present: {nrooms_s8}")
        print(f"s8 count-word distribution: {dict(sorted(cnts.items()))}")
        print(f"tag (0x2 word) histogram: {dict(taghist.most_common(20))}")
        print(f"w0 (0x0 word) histogram: {dict(w0hist.most_common(20))}")
    else:
        for nm in (args or ["ST100.DAT","ST300.DAT","ST302.DAT","ST503.DAT"]):
            decode_s8(os.path.join(REB,nm), verbose=True)
