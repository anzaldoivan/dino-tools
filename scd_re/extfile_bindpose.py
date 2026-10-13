import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Decompress the EXTERNAL character files (core*/opening/ending) and scan every
entry for an embedded skeletal model, fingerprinting each candidate's bone
parent-hierarchy against the st50c humanoid corpse vs an st10e raptor.

The parent column (int8 @ bone+6) is pose- and pointer-relocation-independent, so a
model block can be located by brute-force offset scan: at a true model head O, the
u32 @ O+0x14 is a small bone count and the int8 parents @ O+0x18+i*0x14+6 form a
valid tree (parent[i] < i, root negative). Humanoid vs raptor is decided by matching
the exact parent tuple to the references.
"""
import struct, os
from scd import parse_entries, lzss, rdt_index

DATA = game_path("DC1", 'english/Data')
BONE_COUNT_OFF, BONE_ARRAY_OFF, BONE_STRIDE = 0x14, 0x18, 0x14
KNOWN_RIGS = {7, 10, 15, 18, 20, 21, 22}


def full_pose(buf, o):
    """[(x,y,z,parent)] rest-pose tuple of the skeleton at offset o in buf."""
    bc = struct.unpack_from("<I", buf, o + BONE_COUNT_OFF)[0]
    return [struct.unpack_from("<hhhb", buf, o + BONE_ARRAY_OFF + i * BONE_STRIDE)
            for i in range(bc)]


def pose_dist(a, b):
    """Mean per-bone Euclidean distance between two equal-length rest poses (xyz only)."""
    import math
    n = min(len(a), len(b))
    if n == 0:
        return float("inf")
    s = sum(math.dist(a[i][:3], b[i][:3]) for i in range(n))
    return s / n


def ref_pose(room, model_ptr):
    """(bone_count, parent_tuple, full_pose) of a known in-RDT model."""
    b = open(f"{DATA}/st{room}.dat", "rb").read()
    ents = parse_entries(b)
    typ, size, off, al = ents[rdt_index(b)]
    rdt = lzss(b[off:off + size]) if typ == 7 else b[off:off + size]
    o = model_ptr - 0x80100000
    bc = struct.unpack_from("<I", rdt, o + BONE_COUNT_OFF)[0]
    pose = full_pose(rdt, o)
    return bc, tuple(p[3] for p in pose), pose


def hierarchy_at(buf, o):
    """(bone_count, parent_tuple, xyz_yspan) if a valid skeleton sits at offset o, else None."""
    if o + BONE_COUNT_OFF + 4 > len(buf):
        return None
    bc = struct.unpack_from("<I", buf, o + BONE_COUNT_OFF)[0]
    if bc not in KNOWN_RIGS:
        return None
    end = o + BONE_ARRAY_OFF + bc * BONE_STRIDE
    if end > len(buf):
        return None
    pars, ys = [], []
    for i in range(bc):
        x, y, z, par, fl = struct.unpack_from("<hhhbB", buf, o + BONE_ARRAY_OFF + i * BONE_STRIDE)
        # validity: tree (parent strictly less than own index), root negative, sane coords
        if i == 0:
            if par >= 0:
                return None
        elif not (0 <= par < i):
            return None
        if abs(x) > 4096 or abs(y) > 4096 or abs(z) > 4096:
            return None
        pars.append(par); ys.append(y)
    return bc, tuple(pars), (min(ys), max(ys))


def scan(buf):
    """All valid skeleton candidates in buf, deduped by (offset)."""
    hits = []
    for o in range(0, len(buf) - BONE_ARRAY_OFF, 4):
        r = hierarchy_at(buf, o)
        if r:
            hits.append((o, *r))
    return hits


def entry_buffers(path):
    """Yield (entry_index, type, decompressed_or_raw_bytes) for every package entry."""
    b = open(path, "rb").read()
    for i, (typ, size, off, al) in enumerate(parse_entries(b)):
        payload = b[off:off + size]
        buf = lzss(payload) if typ == 7 else payload
        yield i, typ, size, buf


if __name__ == "__main__":
    hum_bc, HUMANOID, HUM_POSE = ref_pose("50c", 0x8010fa7c)   # corpse oracle
    print(f"REF humanoid (st50c corpse) bc={hum_bc}: {HUMANOID}")
    # raptor ref: first 0x20 enemy in st10e
    from enemies import enemy_records
    b = open(f"{DATA}/st10e.dat", "rb").read()
    ents = parse_entries(b); typ, size, off, al = ents[rdt_index(b)]
    rdt = lzss(b[off:off + size])
    rap_ptr = next(enemy_records(rdt))[5]
    rap_bc, RAPTOR, RAP_POSE = ref_pose("10e", rap_ptr)
    print(f"REF raptor   (st10e)        bc=15: {RAPTOR}")
    ys_h = [p[1] for p in HUM_POSE]; ys_r = [p[1] for p in RAP_POSE]
    print(f"  humanoid yspan={min(ys_h),max(ys_h)}  raptor yspan={min(ys_r),max(ys_r)}  "
          f"(self-dist hum<->rap = {pose_dist(HUM_POSE, RAP_POSE):.0f})\n")

    FILES = ["core00.dat", "core01.dat", "core02.dat", "core03.dat",
             "core.dat", "opening.dat", "ending.dat"]
    for fn in FILES:
        path = f"{DATA}/{fn}"
        if not os.path.exists(path):
            print(f"{fn}: MISSING"); continue
        print(f"=== {fn} ===")
        for i, typ, size, buf in entry_buffers(path):
            hits = scan(buf)
            tag = f"  entry[{i}] type={typ} size={size} -> {len(buf)}B"
            if not hits:
                print(tag + "  (no skeleton)")
                continue
            print(tag + f"  {len(hits)} skeleton candidate(s):")
            for o, bc, pars, yspan in hits:
                pose = full_pose(buf, o)
                if bc == 15:
                    dh, dr = pose_dist(pose, HUM_POSE), pose_dist(pose, RAP_POSE)
                    near = "HUMANOID" if dh < dr else "RAPTOR"
                    cls = f"{near}  (d_hum={dh:.0f} d_rap={dr:.0f})"
                else:
                    cls = f"{bc}-bone rig"
                print(f"      @0x{o:06x} bc={bc:2} yspan={yspan} -> {cls}")
