import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC1 NPC-vs-dinosaur investigation: full 0x20 entity census + skeleton dump.

Goal-1 census (docs/NPC-DINO-INVESTIGATION-PROMPT.md). For every story room:
 - enumerate every 0x20 entity record (slot/cat/killflag/model/motion/bones/species)
 - dump the full skeleton fingerprint (bone count, parent tuple, flag tuple) so a
   humanoid NPC rig can be compared against a confirmed raptor rig at equal bone count.

Cross-reference against placements.md: rooms that have 0x20 entities but no listed
dino enemy are the NPC/cutscene rooms.

Usage:
    python npc_census.py [room]      # full census, or one room e.g. 010d / st10d
"""
import os
import sys
import struct
import glob
from scd import get_rdt, func_table, walk_sub
from enemies import (enemy_records, MODEL_OFF, MOTION_OFF,
                     bone_count, species, SPECIES_BY_BONES,
                     BONE_ARRAY_OFF, BONE_STRIDE, BONE_COUNT_OFF)

DATA = game_path("DC1", "english/Data")

# placements.md enemy presence: room code -> has a listed dino enemy.
# Distilled from data/dc1/placements.md (vanilla story rooms). Rooms NOT in this
# set that nonetheless carry 0x20 entities are NPC/cutscene-suspect.
DINO_ROOMS = {
    0x010E, 0x0114, 0x0102, 0x0108, 0x010B, 0x0109,  # 1F (0109 = Gail-scripted kill)
    0x0203, 0x0204, 0x0202, 0x0200,                   # 2F (0202/0200 = T-Rex scripted)
    0x0309, 0x030E, 0x030A, 0x030D, 0x0306, 0x0305,   # B1
    0x0301, 0x0303,
    0x0404, 0x0407, 0x0401,                           # outdoors (0407 = Pteranodon)
    0x0500, 0x0507, 0x0503,                           # B2
    0x0605, 0x0606, 0x0603, 0x0608, 0x0607, 0x060B,   # B3
    0x0600, 0x0610,                                   # B3 T-Rex scripted
    # Wipe Out alt set (raptor counts in placements.md)
    0x0709, 0x070D, 0x0706, 0x0705, 0x0701, 0x0703,
    0x0800, 0x0802, 0x0804, 0x0805, 0x0808,
    0x0908, 0x0903, 0x0905, 0x0906,
}


def room_code_of(fname):
    """st10d.dat -> 0x010D ; st60a.dat -> 0x060A."""
    b = os.path.basename(fname).lower()
    if not b.startswith('st') or not b.endswith('.dat'):
        return None
    core = b[2:-4]
    if len(core) != 3:
        return None          # st1.dat etc (stage table, not a room)
    try:
        stage = int(core[0], 16)
        room = int(core[1:], 16)
    except ValueError:
        return None
    return (stage << 8) | room


def skeleton(rdt, model_ptr):
    """Return (bone_count, [(parent, flag), ...], [(x,y,z), ...]) or None."""
    bc = bone_count(rdt, model_ptr)
    if not bc:
        return None
    off = model_ptr - 0x80100000 + BONE_ARRAY_OFF
    bones = []
    pos = []
    for i in range(bc):
        bx, by, bz, parent, flag = struct.unpack_from('<hhhbB', rdt, off + i * BONE_STRIDE)
        bones.append((parent, flag))
        pos.append((bx, by, bz))
    return bc, bones, pos


def census_room(path):
    rdt, typ = get_rdt(path)
    if rdt is None:
        return None
    code = room_code_of(path)
    ents = []
    seen = set()
    for p, sub, slot, cat, kf, model, motion in enemy_records(rdt):
        key = (slot, model, motion)
        if key in seen:
            continue
        seen.add(key)
        bc, sp = species(rdt, model)
        ents.append((slot, cat, kf, model, motion, bc, sp))
    return code, rdt, ents


def main():
    arg = sys.argv[1] if len(sys.argv) > 1 else None
    files = sorted(glob.glob(os.path.join(DATA, 'st*.dat')))
    npc_suspects = []
    for f in files:
        code = room_code_of(f)
        if code is None:
            continue
        if arg and arg.lower() not in os.path.basename(f).lower():
            continue
        res = census_room(f)
        if res is None:
            continue
        code, rdt, ents = res
        if not ents:
            continue
        is_dino_room = code in DINO_ROOMS
        flag = '' if is_dino_room else '   <-- NO LISTED DINO (NPC-suspect)'
        print(f"\n=== {os.path.basename(f)}  code {code:04X}  "
              f"{len(ents)} entities{flag}")
        for slot, cat, kf, model, motion, bc, sp in ents:
            print(f"  slot{slot:<3} cat{cat} kf={kf:<3} "
                  f"model={model:#010x} motion={motion:#010x}  "
                  f"bones={bc:<3} {sp}")
            if arg:  # detailed skeleton dump only for single-room queries
                sk = skeleton(rdt, model)
                if sk:
                    bc2, bones, pos = sk
                    parents = tuple(p for p, _ in bones)
                    flags = tuple(fl for _, fl in bones)
                    print(f"        parents={parents}")
                    print(f"        flags  ={flags}")
                    for i, (xyz, (par, fl)) in enumerate(zip(pos, bones)):
                        print(f"          b{i:<2} parent={par:<3} flag={fl:#04x} pos={xyz}")
        if ents and not is_dino_room:
            npc_suspects.append((code, os.path.basename(f), ents))

    if not arg:
        print("\n\n########## NPC-SUSPECT ROOMS (entities, no listed dino) ##########")
        for code, name, ents in npc_suspects:
            sps = ', '.join(f"slot{s}:{sp}({bc}b)" for s, c, k, m, mo, bc, sp in ents)
            print(f"  {code:04X} {name:12} -> {sps}")


if __name__ == '__main__':
    main()
