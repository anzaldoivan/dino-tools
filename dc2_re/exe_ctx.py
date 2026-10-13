import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Dump byte windows around given VAs in Dino2.exe + search .data for function-
pointer tables referencing a set of code VAs. Read-only."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from exe_xref import PE, find_le32

def win(exe, pe, va, back=40, fwd=40):
    fo=pe.va2fo(va)
    s=exe[fo-back:fo+fwd]
    # print as VA: hex
    out=[]
    for i in range(0,len(s),16):
        a=va-back+i
        out.append(f"  {a:08x}: "+" ".join(f"{x:02x}" for x in s[i:i+16]))
    return "\n".join(out)

if __name__=="__main__":
    path=sys.argv[1]
    exe=open(path,"rb").read(); pe=PE(exe)

    print("##### context around enemy model-load sites #####")
    for va in [0x41f606,0x457cdb,0x467a2b,0x439b08,0x433d65,0x4420ce,0x43d227]:
        print(f"\n--- VA 0x{va:08x} ---")
        print(win(exe,pe,va,40,32))

    # search ALL of .text + .data for any 0x71b3xx / 0x71b4xx word (filename table ref)
    tlo,thi=pe.text_range()
    print("\n##### any ref to 0x71b3xx/0x71b4xx anywhere #####")
    for va in range(0x71b3b0,0x71b410,4):
        for h in find_le32(exe,va):
            sec=pe.sec_of(pe.fo2va(h)) if pe.fo2va(h) else "?"
            print(f"  ref to 0x{va:08x} @ foff=0x{h:x} VA=0x{(pe.fo2va(h) or 0):08x} [{sec}]")

    # The 6 routines pushing 0x638000 — get their context to see the enemy-type dispatch
    print("\n##### the six 0x638000 (E60) init sites #####")
    for va in [0x439b08,0x45b5a7,0x45e4d7,0x461b47,0x465147,0x46a6b8]:
        print(f"\n--- 0x{va:08x} ---"); print(win(exe,pe,va,24,28))
