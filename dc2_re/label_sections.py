import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Label the ST*.DAT room section table on rebirth/Data.
Read-only. Dumps the leading pointer table, section boundaries/sizes, and a
hexdump of each section head, to identify enemy/item/door/camera/geometry sections."""
import sys, os, struct, glob
sys.path.insert(0, os.path.dirname(__file__))
from dc2pkg import parse, GPC_LZSS0

REB = game_path("DC2", 'rebirth/Data')
BASE = 0x005e0000

def room_blob(name):
    data = open(os.path.join(REB, name), "rb").read()
    ents, _ = parse(data)
    lz = [e for e in ents if e.type == GPC_LZSS0]
    return lz[0].decomp if lz else None

def hexdump(b, off, n=64):
    out = []
    for i in range(0, min(len(b)-off, n), 16):
        c = b[off+i:off+i+16]
        h = " ".join(f"{x:02x}" for x in c)
        a = "".join(chr(x) if 32 <= x < 127 else "." for x in c)
        out.append(f"    {off+i:05x}: {h:<47} {a}")
    return "\n".join(out)

def analyze(name, dump_sections=True):
    d = room_blob(name)
    if not d:
        print(f"{name}: no LZSS0"); return
    n = len(d)
    print(f"\n{'='*72}\n{name}  blob={n} (0x{n:x})\n{'='*72}")
    # Read the leading u32 array. Classify each as: PTR (in 0x5e0000..base+n), SMALL (<0x1000), OTHER.
    print("  leading u32 array:")
    ptrs = []  # (header_index, header_off, rebased_off)
    smalls = []
    i = 0
    while i < 0x80:
        v = struct.unpack_from("<I", d, i)[0]
        if BASE <= v < BASE + n:
            rb = v - BASE
            ptrs.append((i//4, i, rb))
            tag = f"PTR -> 0x{rb:05x}"
        elif v < 0x10000:
            smalls.append((i//4, i, v))
            tag = f"int {v}"
        else:
            tag = f"0x{v:08x} ?"
        print(f"    [{i//4:2}] +0x{i:02x} = 0x{v:08x}   {tag}")
        i += 4
        # stop after we've clearly left the table: a big non-pointer following some ptrs
        if len(ptrs) >= 1 and not (BASE <= v < BASE+n) and v >= 0x10000:
            # peek: if next two are also non-ptr, stop
            nxt = struct.unpack_from("<I", d, i)[0] if i < n-3 else 0
            if not (BASE <= nxt < BASE+n):
                break
    # Section boundaries: unique sorted rebased offsets
    offs = sorted(set(rb for _,_,rb in ptrs))
    print(f"\n  {len(ptrs)} pointers -> {len(offs)} distinct section offsets")
    bounds = offs + [n]
    print("  sections (offset -> size), with which header slots reference them:")
    for k, o in enumerate(offs):
        size = bounds[k+1] - o
        refs = [hi for hi,_,rb in ptrs if rb == o]
        print(f"    sec[{k}] off=0x{o:05x} size=0x{size:x} ({size})  <- header slots {refs}")
    if dump_sections:
        for k, o in enumerate(offs):
            size = bounds[k+1] - o
            print(f"\n  -- sec[{k}] @0x{o:05x} (size {size}) --")
            print(hexdump(d, o, min(size, 64)))

if __name__ == "__main__":
    names = sys.argv[1:] or ["ST100.DAT", "ST500.DAT", "ST300.DAT"]
    for nm in names:
        analyze(nm)
