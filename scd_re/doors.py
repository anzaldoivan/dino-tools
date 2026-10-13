import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC1 door / area-transition extractor (PROVEN 2026-06-20 Phase 3).

The door surface is the SCD opcode 0x28 (the AOT "area-of-things" umbrella) in its
SUBTYPE-0 form (byte[pc+2]==0, record length 48) -- the sibling of the item record
(subtype 4). Every 0x28 record's byte[pc+0x14] (the AOT semantic type) mirrors its
subtype byte, and the per-type action handler table is at DINO.exe 0x64fd58; entry 0
is the door/area-transition handler family. Within the subtype-0 record:

    destination = word[pc+0x1c]   (LE) = SSRR : low byte = room, high byte = stage
                                  -- exactly the rooms.json id (e.g. 0x010d)
    lock flag   = byte[pc+0x27]   (GetFlag group 9 gate id; 0 = ungated)
    door type   = byte[pc+0x28]   (0 = normal door; 1/2/3/8/0x2e.. = gated variants;
                                   0xfd/fe/ff = special non-door actions)

Static proof (DINO.exe):
  - AOT registration handler 0x426a73 stores each record body (pc+4) into the per-room
    AOT pointer array obj+0x1e8[slot]; the player-collision consumer 0x448xxx fetches it
    and dispatches via the AOT-type action table 0x64fd58 (index = byte[rec+0x14] = subtype).
  - The common-door handler 0x4493a8 reads word[rec+0x1c] and compares it against literal
    room codes (cmp 0x309 / 0x604 / 0x113) for per-destination special-casing -- i.e. it
    consumes rec+0x1c AS the destination stage+room.
  - The door sub-action handlers (type-0 action table 0x449384) gate on
    GetFlag(group 9, byte[rec+0x27]) -- the lock check.

Empirical proof (corpus + oracle):
  - 306/324 subtype-0 records decode to a rooms.json room at word[+0x1c]; the 18 that
    don't are stA*.dat (stage 0x0a/0x0b demo/extra stages absent from rooms.json) and
    decode correctly to 0x0aXX/0x0bXX.
  - BFS from the start room 0x010d (Backyard) reaches 93/95 story rooms ignoring locks;
    ~92% of story doors are reciprocal (the rest are one-way elevators/scripted drops).
  - Hand-checks match adjacency: st10c->010d, st107 hub (incl. cross-stage 0400),
    st203 -> {0204,0206,0202,0201}.
  - Lock correlation is clean: door-type 0 is almost always ungated (8/274 locked) while
    every special door-type (1/2/3/8/0x2e/0x30/0x31) carries a nonzero lock flag.
"""
import struct
from scd import get_rdt, func_table, walk_sub

OP28_DOOR_SUBTYPE = 0
OP28_DOOR_LEN = 48
DEST_OFF = 0x1c   # word: low=room, high=stage  -> rooms.json id
LOCK_OFF = 0x27   # byte: GetFlag group-9 gate id (0 = ungated)
TYPE_OFF = 0x28   # byte: door sub-action / kind (0 = normal door)


def door_records(rdt):
    """Yield (buf_offset, sub_idx, dest_code, lock, door_type) per 0x28 door record."""
    base, starts = func_table(rdt)
    if not starts:
        return
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op == 0x28 and ln == OP28_DOOR_LEN and rdt[p + 2] == OP28_DOOR_SUBTYPE:
                dest = struct.unpack_from('<H', rdt, p + DEST_OFF)[0]
                yield (p, i, dest, rdt[p + LOCK_OFF], rdt[p + TYPE_OFF])


def room_doors(path):
    rdt, typ = get_rdt(path)
    if rdt is None:
        return None, []
    return rdt, list(door_records(rdt))
