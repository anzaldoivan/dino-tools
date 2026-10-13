import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Bind-pose-fingerprint the ME** model/motion catalog (file-ids 126-254 in the 0x647C40
table) into a file-id -> {species, is-person} map for the CE cutscene-loader session.

Each ME**.DAT is a Gian package; we decompress every entry, scan for an embedded EMD
skeleton, and classify each by full rest-pose distance to the st50c humanoid corpse vs an
st10e raptor (the same discriminator as extfile_bindpose.py). A file that contains a humanoid
skeleton is a candidate live-cutscene-actor model.
"""
import struct, os, re
import extfile_bindpose as E
from enemies import enemy_records

DINO = game_path("DC1", 'english/DINO.exe')
BASE = 0x400000


def name_table():
    data = open(DINO, "rb").read()
    lo = 0x647C40 - BASE
    out = {}
    for k in range(530):
        v = struct.unpack_from("<I", data, lo + k * 4)[0]
        s = data[v - BASE:v - BASE + 24].split(b"\x00")[0].decode("latin1")
        out[k] = s
    return out


def classify(buf_hits_pose):
    pass


if __name__ == "__main__":
    _, _, HUM = E.ref_pose("50c", 0x8010fa7c)
    b = open(f"{E.DATA}/st10e.dat", "rb").read(); ents = E.parse_entries(b)
    t, s, o, a = ents[E.rdt_index(b)]
    _, _, RAP = E.ref_pose("10e", next(enemy_records(E.lzss(b[o:o + s])))[5])

    names = name_table()
    cat = [(k, v) for k, v in names.items()
           if v.split("/")[-1].startswith(("ME", "H1", "H2", "H3", "DOWN"))]

    rows = []
    for fid, path in cat:
        base = path.split("/")[-1]
        fp = f"{E.DATA}/{base}"
        if not os.path.exists(fp):
            rows.append((fid, base, "MISSING", None, None, None)); continue
        try:
            bufs = list(E.entry_buffers(fp))
        except Exception as ex:
            rows.append((fid, base, f"ERR:{ex}", None, None, None)); continue
        # collect skeletons across entries
        skels = []
        for i, typ, size, buf in bufs:
            for off, bc, pars, ys in E.scan(buf):
                if bc == 15:
                    pose = E.full_pose(buf, off)
                    dh = E.pose_dist(pose, HUM); dr = E.pose_dist(pose, RAP)
                    skels.append((bc, "HUM" if dh < dr else "rap", round(dh), round(dr)))
                else:
                    skels.append((bc, f"{bc}b", None, None))
        if not skels:
            rows.append((fid, base, "no-skel", None, None, None)); continue
        # pick the dominant classification
        has_hum = any(s[1] == "HUM" for s in skels)
        bcset = sorted({s[0] for s in skels})
        kind = "PERSON" if has_hum else ("dino" if any(s[0] != 15 or s[1] == "rap" for s in skels) else "?")
        rows.append((fid, base, kind, bcset, len(skels), skels[0]))

    # report
    persons = [r for r in rows if r[2] == "PERSON"]
    print(f"=== ME catalog fingerprint: {len(rows)} files, {len(persons)} contain a HUMANOID skeleton ===\n")
    print(f"{'fid':>4} {'file':14} {'kind':8} {'boneSet':16} {'#skel':>5}  first")
    for fid, base, kind, bcset, n, first in rows:
        bs = str(bcset) if bcset else ""
        print(f"{fid:4} {base:14} {kind:8} {bs:16} {str(n):>5}  {first if first else ''}")
    print("\n=== file-ids that contain a HUMANOID (candidate cutscene-actor models) ===")
    for fid, base, kind, bcset, n, first in persons:
        print(f"  id{fid} {base}  {bcset}")
