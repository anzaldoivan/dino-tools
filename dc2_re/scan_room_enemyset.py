import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""T3c: per-room enemy-set scan. Parse each rebirth ST*.DAT package, decode the
small sound-config DATA entries (records `00 0T 00 II vv vv ww ww`, T = type tag:
4=room,3=enemy,7=door), and report per-room the T=3 (enemy) records. Read-only."""
import sys, os, struct, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from dc2pkg import parse, GPC_DATA, GPC_SOUND, PC_NAMES

REB = game_path("DC2", 'rebirth/Data')

def data_entries(path):
    ents,_ = parse(open(path,"rb").read())
    return ents

def decode_sndcfg(b):
    """Decode 8-byte records `tt 0T ?? II  vv vv ww ww`. Return list of (T, idx, v, w)."""
    recs=[]
    for o in range(0, len(b)-7, 8):
        b0,T,b2,idx = b[o], b[o+1], b[o+2], b[o+3]
        v,w = struct.unpack_from("<HH", b, o+4)
        if b0==0xff and T==0xff: continue  # padding (appears mid-entry — skip, do NOT stop)
        recs.append((T, idx, v, w, b0, b2))
    return recs

if __name__=="__main__":
    rooms=sorted(glob.glob(os.path.join(REB,"ST[0-9]*.DAT")))
    Tglobal=collections.Counter()
    enemy_rooms={}; t3_idx_hist=collections.Counter()
    verbose = "-v" in sys.argv
    for p in rooms:
        nm=os.path.basename(p)
        ents=data_entries(p)
        datas=[e for e in ents if e.type==GPC_DATA]
        # the sound-config DATA is the small one (~24..256 B); pick all small DATA entries
        t3=[]
        layout=[]
        for e in datas:
            if e.size>4096: continue
            for (T,idx,v,w,b0,b2) in decode_sndcfg(e.data):
                Tglobal[T]+=1
                layout.append((T,idx,v,w))
                if T==3:
                    t3.append((idx,v,w)); t3_idx_hist[idx]+=1
        if t3:
            enemy_rooms[nm]=t3
        if verbose:
            tags=collections.Counter(T for T,_,_,_ in layout)
            print(f"{nm:12} DATA-recs by T: {dict(sorted(tags.items()))}  T3={[(hex(i),v,w) for i,v,w in t3]}")
    print("\n=== global T-tag histogram (all rooms) ===", dict(sorted(Tglobal.items())))
    print(f"\n=== rooms WITH enemy (T=3) sound-config records: {len(enemy_rooms)}/{len(rooms)} ===")
    for nm,t3 in enemy_rooms.items():
        print(f"  {nm}: {[(hex(i),v) for i,v,w in t3]}")
    print(f"\n=== T=3 index histogram (sound-bank/enemy id candidate) ===")
    print("  ", dict(sorted(t3_idx_hist.items())))
