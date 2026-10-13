import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""THROWAWAY Phase-0 probe (Lever B, PICKUP-GROUND-MODEL-FEASIBILITY): dump the raw
bytes of known bespoke pickup ground meshes to nail the prim format before the census.

Header per cont.72: {ptr->+0xC, ptr->+0xC, u16 count} + packed vertex/color prims.
This dump exists to measure the prim stride and whether prims carry tpage/CLUT codes.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(__file__))
from prim_walk import get_rdt

B = 0x80100000

def hexd(buf, off, n, per=16):
    lines = []
    for i in range(0, n, per):
        chunk = buf[off + i: off + min(i + per, n)]
        lines.append(f"  +{i:04x}: " + " ".join(f"{b:02x}" for b in chunk))
    return "\n".join(lines)

def dump(room, off, name, nbytes=0x120):
    rdt = get_rdt(room)
    p1, p2 = struct.unpack_from('<II', rdt, off)
    cnt = struct.unpack_from('<H', rdt, off + 8)[0]
    extra = struct.unpack_from('<H', rdt, off + 10)[0]
    print(f"\n## {name} st{room} off={off:#x} p1={p1:#x} p2={p2:#x} count={cnt} +0xA={extra:#x}")
    print(f"   p1-relative: p1-off-B = {p1 - B - off:#x} (expect 0xC if prims start after header)")
    print(hexd(rdt, off, min(nbytes, len(rdt) - off)))

if __name__ == "__main__":
    dump("10c", 0xD264, "BG Area Key")
    dump("103", 0xEA3C, "Entrance Key")
