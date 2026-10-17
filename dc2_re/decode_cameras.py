import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Decode a DC2 room's cameras, camera zones and background directory. Read-only.

Room loader 0x481F60 copies the 12-slot directory into the scene manager
([0x876DB8]); the fields below are named from their readers (K143-K146).

slot 9  -> scene+0x7B8 (after a 16-byte header: u32 ?, u16 count, u16 0x1ADA, 8 B)
  16-byte camera record, applied by the streamer at 0x403CF5:
    +0  s16 x,y,z  eye        +6  u16 projection distance (0x4C0DD0)
    +8  s16 x,y,z  target     +14 s16 roll
slot 10 -> scene+0xA6C: one {u32 sector, u32 bytes} per camera = that camera's
  package in ST*.DBS (0x403B1C: seek sector<<11). Camera i shows background i.
slot 3  -> scene+0xA44: u32 count, 44-byte camera zones, tested in order by
  0x4035F0 against the player; the first hit's camera is made current (0x403A40):
    +0 u8 flags (0x80 = off)   +4 u8 camera      +5 u8 -> cam state +0x4F
    +6 u8 floor, +7 u8 floors above it (player +0x5A must be in the range)
    +8 2 x s16 unknown         +12 4 x (s16 x, s16 z) trigger quad
    +28 4 x (s16 x, s16 z) quad tested for actors under the current camera
        (0x48FA20, 0x42EA9D, 0x430E3D)
slot 6  -> scene+0xA64: u32 count, 20-byte records {u8, u8 on, s8 floor, u8,
  4 x (s16 x, s16 z)} read by the sound code at 0x48C550 for SE 0x30/0x31.

Usage: decode_cameras.py [ST101 ...]   (no args = census; --json for all rooms)
"""
import os, struct, glob, json, collections
_path_sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dc2pkg import parse, GPC_LZSS0

REB = game_path("DC2", "rebirth/Data")
BASE = 0x005E0000


def room_blob(name):
    """Decompressed room blob; the pristine .bak is read where one exists."""
    p = os.path.join(REB, name + ".DAT")
    p = p + ".bak" if os.path.exists(p + ".bak") else p
    ents, _ = parse(_ToolPath(p).read_bytes())
    return [e for e in ents if e.type == GPC_LZSS0][0].decomp


def slot(d, i):
    v = struct.unpack_from("<I", d, i * 4)[0]
    return v - BASE if v else None


def quad(d, o):
    return [list(struct.unpack_from("<2h", d, o + 4 * k)) for k in range(4)]


def decode(d):
    o9, o10, o3, o6 = (slot(d, i) for i in (9, 10, 3, 6))
    far, n, magic = struct.unpack_from("<IHH", d, o9)
    cams = []
    for i in range(n):
        ex, ey, ez, h, tx, ty, tz, roll = struct.unpack_from("<3hH3hh", d, o9 + 16 + 16 * i)
        sec, size = struct.unpack_from("<II", d, o10 + 8 * i)
        cams.append({"eye": [ex, ey, ez], "target": [tx, ty, tz], "proj": h, "roll": roll,
                     "dbs_offset": sec << 11, "dbs_size": size})
    zones = []
    for i in range(struct.unpack_from("<I", d, o3)[0]):
        o = o3 + 4 + 44 * i
        zones.append({"flags": d[o], "camera": d[o + 4], "b5": d[o + 5],
                      "floor": d[o + 6], "floor_span": d[o + 7],
                      "w8": list(struct.unpack_from("<2h", d, o + 8)),
                      "trigger": quad(d, o + 12), "visible": quad(d, o + 28)})
    snd = []
    for i in range(struct.unpack_from("<I", d, o6)[0]):
        o = o6 + 4 + 20 * i
        snd.append({"b0": d[o], "on": d[o + 1], "floor": struct.unpack_from("<b", d, o + 2)[0],
                    "b3": d[o + 3], "quad": quad(d, o + 4)})
    return {"header": [far, magic], "cameras": cams, "zones": zones, "sound_zones": snd}


def room_names():
    return sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(REB, "ST[0-9]*.DAT")))


if __name__ == "__main__":
    args = _path_sys.argv[1:]
    if args == ["--json"]:
        json.dump({r: decode(room_blob(r)) for r in room_names()}, _path_sys.stdout)
    elif not args:
        c = collections.Counter()
        for r in room_names():
            v = decode(room_blob(r))
            used = {z["camera"] for z in v["zones"]}
            c["rooms"] += 1; c["cameras"] += len(v["cameras"]); c["zones"] += len(v["zones"])
            c["sound zones"] += len(v["sound_zones"])
            c["cameras no zone selects"] += len(set(range(len(v["cameras"]))) - used)
            c["zones naming a camera past the table"] += sum(z["camera"] >= len(v["cameras"]) for z in v["zones"])
        for k, v in c.items():
            print(f"{v:6} {k}")
    for r in (a for a in args if a != "--json"):
        v = decode(room_blob(r))
        print(r, "header", v["header"])
        for i, c in enumerate(v["cameras"]):
            print(f"  cam {i:2} eye {c['eye']} target {c['target']} proj {c['proj']} roll {c['roll']} dbs 0x{c['dbs_offset']:x}+{c['dbs_size']}")
        for i, z in enumerate(v["zones"]):
            print(f"  zone {i:2} -> cam {z['camera']} flags {z['flags']:#x} floor {z['floor']}+{z['floor_span']} trigger {z['trigger']}")
