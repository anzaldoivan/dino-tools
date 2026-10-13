import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Probe op23 dword[+8] targets in cutscene rooms vs combat rooms.

For each op23 record, resolve the +8 pointer into the decompressed RDT buffer and
dump the target header, testing two interpretations:
  - skeleton/model (like the 0x20 dino +0x10): dword@+0x14 = bone count, bone array @+0x18
  - static scenery (cont.15 claim): [vtxptr][vtxptr][u16 count][int16 xyz...]
Also dumps the op23 record body and which subroutine it came from.
"""
import sys, struct
from scd import get_rdt, func_table, walk_sub

OP23, OP23_LEN = 0x23, 32
DATA = game_path("DC1", "english/Data")


def op23_records(rdt):
    base, starts = func_table(rdt)
    out = []
    if not starts:
        return out
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op == OP23 and ln == OP23_LEN:
                out.append((p, i))
    return out


def hx(b):
    return " ".join(f"{x:02x}" for x in b)


def probe(room):
    path = f"{DATA}/st{room}.dat"
    rdt, typ = get_rdt(path)
    if rdt is None:
        print(f"st{room}: no rdt")
        return
    recs = op23_records(rdt)
    print(f"\n=== st{room}  rdt_len={len(rdt):#x}  type={typ}  op23_count={len(recs)} ===")
    for p, sub in recs:
        body = rdt[p:p + OP23_LEN]
        slot = rdt[p + 1]
        ptr = struct.unpack_from('<I', rdt, p + 8)[0]
        off = ptr - 0x80100000
        info = f"  sub{sub} slot{slot:#04x} rec@{p:#07x} +8ptr={ptr:#010x}"
        if 0 <= off < len(rdt) - 0x20:
            hdr = rdt[off:off + 0x20]
            bonecnt = struct.unpack_from('<I', rdt, off + 0x14)[0]
            p0, p1 = struct.unpack_from('<II', rdt, off)
            scen_cnt = struct.unpack_from('<H', rdt, off + 8)[0]
            info += f" off={off:#07x}\n      hdr: {hx(hdr)}"
            info += f"\n      [skel?] bonecount@+14={bonecnt}"
            info += f"  [scen?] ptr0={p0:#x} ptr1={p1:#x} cnt@+8={scen_cnt}"
        else:
            info += f" off={off:#x} OUT-OF-BUFFER"
        print(info)


if __name__ == "__main__":
    for r in sys.argv[1:]:
        probe(r)
