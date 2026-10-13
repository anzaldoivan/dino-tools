import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Compare 15-bone bind poses: are cutscene-room 0x20 entities raptors or humanoids?

Dumps the rest-pose bone position tuple for given (room, model_ptr) and clusters
identical poses. The st50c corpse (known humanoid) and st10e raptor are references.
"""
import struct
from scd import get_rdt
from enemies import bone_count, BONE_ARRAY_OFF, BONE_STRIDE

DATA = game_path("DC1", 'english/Data')


def pose(room, ptr):
    rdt, _ = get_rdt(f"{DATA}/st{room}.dat")
    bc = bone_count(rdt, ptr)
    if not bc:
        return None
    off = ptr - 0x80100000 + BONE_ARRAY_OFF
    pos = []
    for i in range(bc):
        x, y, z, par, fl = struct.unpack_from('<hhhbB', rdt, off + i * BONE_STRIDE)
        pos.append((x, y, z, par))
    return bc, pos


# (label, room, model_ptr)
TARGETS = [
    ("st50c-corpse(HUMANOID ref)", "50c", None),  # filled below from 0x20 census
    ("st10e-raptor(RAPTOR ref)",   "10e", None),
    ("st50e-slot0(Kirk room?)",    "50e", 0x80113f64),
    ("st50e-slot1",                "50e", 0x80117aa0),
    ("st50f-slot0(Gail room?)",    "50f", 0x8010b8cc),
    ("st505-slot0(Gail room?)",    "505", 0x80111630),
    ("st30e-slot8",                "30e", 0x80107ff8),
]


def census_first_enemy(room):
    """Return first 0x20 enemy model_ptr in a room (for ref rooms)."""
    from enemies import enemy_records
    rdt, _ = get_rdt(f"{DATA}/st{room}.dat")
    for rec in enemy_records(rdt):
        return rec[5]  # model_ptr
    return None


if __name__ == "__main__":
    # resolve refs
    poses = {}
    for label, room, ptr in TARGETS:
        if ptr is None:
            ptr = census_first_enemy(room)
        r = pose(room, ptr)
        if r is None:
            print(f"{label}: no pose (ptr {ptr:#x})")
            continue
        bc, pos = r
        key = tuple(pos)
        poses[label] = key
        # head bone heuristic: print bone count + first 3 + max |y| (height)
        ys = [p[1] for p in pos]
        print(f"{label:30} bc={bc} ptr={ptr:#010x} yspan=[{min(ys)},{max(ys)}] "
              f"b0={pos[0]} b1={pos[1]}")
    # equality matrix
    print("\nIdentical-pose groups:")
    labels = list(poses)
    groups = []
    for l in labels:
        for g in groups:
            if poses[l] == poses[g[0]]:
                g.append(l)
                break
        else:
            groups.append([l])
    for g in groups:
        print("  " + " == ".join(g))
