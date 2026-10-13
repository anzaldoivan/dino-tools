import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Decode the SCD init script from a room .dat file (per-subroutine walk).

Pipeline (PROVEN):
  package last entry -> (LZSS if type 7) -> RDT buffer (PSX 0x80100000)
  rec+0x14 = SCD function-offset table (self-describing: entry0 = n*4 entries).
  Each entry is a subroutine offset from the table base; walk each [start,next).

Lengths + control-flow resolution live in scd.py / scd_opcode_table.json.
NOTE (2026-06-19 cont.7): the init-script 0x4C records are object/scenery
placements, NOT item pickups — obj+0x2E (rec+0x1A) is 0/var-ref for all 798
records corpus-wide. The editable item surface is elsewhere (see STATIC-SCD-RE.md).
"""
import struct, sys
from scd import (PSX_RDT_BASE, get_rdt, oplen, func_table, walk_sub, walk_room)


def main(path):
    rdt, typ = get_rdt(path)
    print(f"{path}: rdt len {len(rdt):#x} (entry type {typ})")
    base, starts = func_table(rdt)
    if not starts:
        print("  no valid rec+0x14 func table"); return
    print(f"func table @ rdt {base:#x}: {len(starts)} subroutines")
    clean = 0; recs4c = []
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, ok = walk_sub(rdt, s, e)
        clean += ok
        if not ok:
            p, op, _ = ops[-1]
            print(f"  sub{i} [{s:#x},{e:#x}) DERAIL @ {p:#x} op {op:#04x} "
                  f"({len(ops)} ops; trailing data unless op<=0x71)")
        for p, op, ln in ops:
            if op == 0x4c and ln:
                idw = struct.unpack_from('<H', rdt, p + 0x1a)[0]
                recs4c.append((i, p, idw))
    print(f"walked {clean}/{len(starts)} subroutines clean")
    for i, p, idw in recs4c:
        print(f"  0x4C @ sub{i} rdt {p:#x}: +0x1A={idw:#06x} "
              f"(lo={idw & 0xff:#04x} hi={idw >> 8:#04x})")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else r'../../english/Data/st10c.dat')
