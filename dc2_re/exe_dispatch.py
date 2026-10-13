import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Find the enemy-constructor function-pointer table (the spawn dispatch).
Scan .rdata/.data for 4-byte values that land inside any known enemy-constructor
function body; clusters of such pointers = the dispatch table. Read-only."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from exe_xref import PE

# known model-load sites (a point inside each enemy constructor)
LOAD_SITES = {
    0x633000:[0x41f606,0x457cdb,0x467a2b],
    0x638000:[0x439b08,0x45b5a7,0x45e4d7,0x461b47,0x465147,0x46a6b8],
    0x63d000:[0x4420ce],
    0x63f500:[0x433d65],
    0x650000:[0x43d227],
}

if __name__=="__main__":
    exe=open(sys.argv[1],"rb").read(); pe=PE(exe)
    # build "inside a constructor" windows: [site-0xC0, site+0x20]
    sites=sorted(s for v in LOAD_SITES.values() for s in v)
    windows=[(s-0xC0, s+0x20) for s in sites]
    def inside(va):
        return any(lo<=va<hi for lo,hi in windows)

    # scan .rdata + .data for 4-byte LE pointers landing inside a constructor
    print("=== pointers (in .rdata/.data) into enemy-constructor bodies ===")
    hits=[]
    for name,va,vsz,rptr,rsz in pe.secs:
        if name not in (".rdata",".data") or not rsz: continue
        for fo in range(rptr, rptr+rsz-3):
            v=struct.unpack_from("<I",exe,fo)[0]
            if 0x401000<=v<0x4e8000 and inside(v):
                hits.append((pe.fo2va(fo), v))
    # print, marking gaps (table boundaries)
    prev=None
    for at,v in hits:
        gap = "" if prev is not None and at-prev==4 else "  <-- new cluster"
        print(f"  [0x{at:08x}] -> 0x{v:08x}{gap}")
        prev=at

    # for each cluster, also show the full surrounding table (consecutive code pointers)
    print("\n=== expand clusters: full consecutive code-pointer runs ===")
    cluster_starts=[hits[i][0] for i in range(len(hits)) if i==0 or hits[i][0]-hits[i-1][0]!=4]
    for cs in cluster_starts:
        fo=pe.va2fo(cs)
        # walk back while previous dword is a .text pointer
        start=fo
        while True:
            p=start-4
            v=struct.unpack_from("<I",exe,p)[0]
            if 0x401000<=v<0x4e8000: start=p
            else: break
        # walk forward while dword is a .text pointer
        end=fo
        while True:
            v=struct.unpack_from("<I",exe,end)[0]
            if 0x401000<=v<0x4e8000: end+=4
            else: break
        n=(end-start)//4
        print(f"\n  TABLE @ VA 0x{pe.fo2va(start):08x}  ({n} code pointers):")
        for k in range(n):
            v=struct.unpack_from("<I",exe,start+k*4)[0]
            mark=" *CTOR*" if inside(v) else ""
            print(f"    [{k:2}] 0x{pe.fo2va(start+k*4):08x} -> 0x{v:08x}{mark}")
