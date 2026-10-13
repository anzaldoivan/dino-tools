import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC1 item-placement extractor (PROVEN 2026-06-19 cont.9).

The editable item surface is the SCD opcode 0x28 with subtype byte[pc+2]==4
(record length 44). Within that record:
    item id  = word[pc+0x1c]  (low byte = id; 0xff = empty/no-item slot)
    count    = word[pc+0x1e]

Proven: (a) obj+0x268 (the gosub event-table base, set at room load from RDT
header[0x14]) IS the same rec+0x14 table scd.py walks — so all event subs are
already walked; (b) the 0x28 handler (0x426a73) uses word[+0x1c] as an item id:
it indexes a per-item property table (base+id+0x9fe3) and the collected-flag
array (GetFlag group 8, bit=id); (c) corpus-wide every subtype-4 +0x1c lands in
the item-id space (0x01..0x6f or 0xff), never coordinate noise; (d) decoded ids
match the placements.md oracle across rooms (st105 9mm 0x16 count 17, st10c key
0x30, st113 Plug 0x2b, st103 5/6 items, ...); (e) byte-identical across the 5
western languages.

Supply pool 0x10..0x23 is rerollable; key items 0x2b..0x6f are flag-gated.
"""
import struct
from scd import get_rdt, func_table, walk_sub

OP28_ITEM_SUBTYPE = 4
OP28_ITEM_LEN = 44
ID_OFF = 0x1c
COUNT_OFF = 0x1e


def item_records(rdt):
    """Yield (buf_offset, sub_idx, item_id, count) for every 0x28 item record."""
    base, starts = func_table(rdt)
    if not starts:
        return
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op == 0x28 and ln == OP28_ITEM_LEN and rdt[p + 2] == OP28_ITEM_SUBTYPE:
                idw = struct.unpack_from('<H', rdt, p + ID_OFF)[0]
                cnt = struct.unpack_from('<H', rdt, p + COUNT_OFF)[0]
                yield (p, i, idw & 0xff, cnt)


def room_items(path):
    rdt, typ = get_rdt(path)
    if rdt is None:
        return None, []
    return rdt, list(item_records(rdt))
