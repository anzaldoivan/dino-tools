import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Dump a skeletal model's full footprint (header ptrs + per-bone mesh ptrs).

Model layout (from model-setup 0x416b79, cont.14):
  +0x00..+0x0c : 4 PSX ptrs (relocated)
  +0x14        : bone count (dword)
  +0x18        : bone array, stride 0x14:
                   int16 x,y,z (+0/+2/+4), int8 parent(+6), u8 flag(+7),
                   ptr meshA(+8), ptr meshB(+0xc)   <- per-bone geometry, relocated
"""
import sys, struct
from scd import get_rdt

DATA = game_path("DC1", 'english/Data')
B = 0x80100000


def dump(room, mptr, motion=None):
    rdt, _ = get_rdt(f"{DATA}/st{room}.dat")
    off = mptr - B
    h = struct.unpack_from('<IIII', rdt, off)
    bc = struct.unpack_from('<I', rdt, off + 0x14)[0]
    print(f"st{room} model={mptr:#010x} off={off:#x} rdt={len(rdt):#x}")
    print(f"  header ptrs +0/4/8/c = {[hex(x) for x in h]}  bonecount={bc}")
    ba = off + 0x18
    ptrs = []
    lo, hi = off, off
    for i in range(bc):
        x, y, z, par, fl = struct.unpack_from('<hhhbB', rdt, ba + i * 0x14)
        mA, mB = struct.unpack_from('<II', rdt, ba + i * 0x14 + 8)
        for m in (mA, mB):
            if B <= m < B + len(rdt):
                o = m - B
                ptrs.append(o)
                lo = min(lo, o); hi = max(hi, o)
        print(f"   b{i:<2} par={par:<3} fl={fl:#04x} pos=({x},{y},{z}) "
              f"meshA={mA:#x} meshB={mB:#x}")
    skel_end = ba + bc * 0x14
    print(f"  skeleton block: {off:#x}..{skel_end:#x} ({skel_end-off} B)")
    if ptrs:
        print(f"  per-bone mesh ptr span: {B+lo:#x}..{B+hi:#x} (buffer {lo:#x}..{hi:#x})")
    if motion:
        print(f"  motion ptr {motion:#010x} -> buffer off {motion-B:#x}")


if __name__ == "__main__":
    # st50c corpse (humanoid control) + a raptor for shape contrast
    dump("50c", 0x8010fa7c, 0x80140a20)
    print()
    dump("10e", 0x80110e70)
