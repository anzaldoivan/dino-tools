import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Measure op23 mesh sizes: vertex count + byte span to next resource.

A character body mesh would be large/multi-part; scenery props are small.
"""
import sys, struct
from scd import get_rdt
from probe_op23 import op23_records

DATA = game_path("DC1", 'english/Data')


def run(room):
    rdt, typ = get_rdt(f"{DATA}/st{room}.dat")
    recs = op23_records(rdt)
    ptrs = sorted(set(struct.unpack_from('<I', rdt, p + 8)[0] for p, _ in recs))
    print(f"\n=== st{room}  op23={len(recs)} distinct_ptrs={len(ptrs)} rdt={len(rdt):#x} ===")
    for p, sub in recs:
        ptr = struct.unpack_from('<I', rdt, p + 8)[0]
        off = ptr - 0x80100000
        slot = rdt[p + 1]
        if not (0 <= off < len(rdt) - 0xc):
            print(f"  slot{slot:#04x} ptr={ptr:#x} OOB")
            continue
        cntA = struct.unpack_from('<H', rdt, off + 8)[0]
        cntB = struct.unpack_from('<H', rdt, off + 0xa)[0]
        # span: distance to next-higher distinct ptr (rough data size)
        higher = [q for q in ptrs if q > ptr]
        span = (min(higher) - ptr) if higher else None
        print(f"  slot{slot:#04x} off={off:#07x} cntA={cntA:<4} vtxN={cntB:<5} "
              f"span={span if span is None else hex(span)}")


if __name__ == "__main__":
    for r in sys.argv[1:]:
        run(r)
