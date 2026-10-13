import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Per-species enemy-texture catalog dumper (NEW; does not edit prim_walk.py).

Given (room, model_ptr) for one of the 7 DC1 enemy model classes, this:
  1. walks every bone mesh of the model (tri stride 16 / quad stride 20) and
     collects the full tpage-code set (u16 @ prim+0x0e) and CLUT code
     (u16 @ prim+0x0a) the primitives carry — i.e. the texture refs baked into
     the model (docs/dc1/TEXTURE-IMPORT-VRAM.md (a));
  2. parses the room's 16-byte Gian entry headers (PSX LoadImage RECTs) and finds
     the texture VRAM block that COVERS each tpage's page origin and the palette
     block at the CLUT coords (the same match TextureImporter.ExtractSpeciesTexture
     performs), printing the texture_rect (X,0,W,H) and palette_rect (768,Yclut,256,1).

PSX code math: tpage origin = ((c&0xF)*64, ((c>>4)&1)*256); CLUT origin =
((c&0x3F)*16, (c>>6)&0x1FF). The palette ROW (Yclut) is the per-species
discriminator. Source of texture identity is the ROOM-FILE model primitives,
NOT any EXE per-species table.

Usage: python species_tpage.py        # dumps all 7 catalog rows
"""
import struct, os, json
from scd import rdt_index
from collections import Counter

DATA = game_path("DC1", "english/Data")
if not os.path.isdir(DATA):
    DATA = game_path("DC1", 'english/Data')
B = 0x80100000
SECT = 2048

# (category, bones, name, donor_room, model_ptr, place_op)  — model ptrs found
# via npc_census.py (0x20: model@+0x10) and check_install.py (0x59: model@+0x04).
SPECIES = [
    (1, 15, "Velociraptor",                  "10e", 0x80110e70, 0x20),
    (2, 21, "Raptor (heavy rig)",            "401", 0x801081c0, 0x20),
    (3, 20, "Tyrannosaurus (boss rig)",      "60a", 0x80114144, 0x20),
    (4, 10, "Tyrannosaurus (Chief's rig)",   "202", 0x8010f5d0, 0x20),
    (5, 7,  "Swarm (small dino)",            "307", 0x8010e39c, 0x20),
    (7, 18, "Pteranodon",                    "400", 0x80109bd8, 0x20),
    (8, 22, "Therizinosaurus",               "603", 0x8010cd68, 0x59),
]


def lzss(data):
    out = bytearray(); pos = 0; flag = 1; n = len(data)
    while pos < n:
        if flag == 1:
            if pos >= n: break
            flag = data[pos] | 0x100; pos += 1
        if pos >= n: break
        ch = data[pos]; pos += 1
        if flag & 1:
            out.append(ch)
        else:
            if pos >= n: break
            t = data[pos]; pos += 1
            dist = ((t & 0x0F) << 8) | ch; length = (t >> 4) + 2
            start = len(out) - dist
            if start < 0: break
            for k in range(length): out.append(out[start + k])
        flag >>= 1
    return bytes(out)


def entries(room):
    """[(idx, typ, size, x, y, w, h, payload_off)] for every package entry."""
    b = open(os.path.join(DATA, f"st{room}.dat"), 'rb').read()
    pos = 2048; out = []
    for i in range(128):
        typ, size = struct.unpack_from('<II', b, i * 16)
        if typ > 8 or size == 0: break
        if pos + size > len(b): break
        x, y, w, h = struct.unpack_from('<HHHH', b, i * 16 + 8)
        out.append((i, typ, size, x, y, w, h, pos))
        pos += (size + SECT - 1) & ~(SECT - 1)
    return b, out


def get_rdt(room):
    b, ents = entries(room)
    i, typ, size, x, y, w, h, off = ents[rdt_index(b)]
    return lzss(b[off:off + size]) if typ == 7 else b[off:off + size]


def tpage_origin(c):  return ((c & 0xF) * 64, ((c >> 4) & 1) * 256)
def clut_origin(c):   return ((c & 0x3F) * 16, (c >> 6) & 0x1FF)


def walk(rdt, head, stride, clut_set, limit=8192):
    o = head; n = 0; tp = Counter(); cl = Counter()
    while o + stride <= len(rdt) and n < limit:
        c = struct.unpack_from('<H', rdt, o + 0x0a)[0]
        if c not in clut_set:
            break
        cl[c] += 1
        tp[struct.unpack_from('<H', rdt, o + 0x0e)[0]] += 1
        o += stride; n += 1
    return n, tp, cl


def model_codes(rdt, mptr):
    """Walk all bone meshes; return (bone_count, tpage Counter, clut Counter)."""
    off = mptr - B
    bc = struct.unpack_from('<I', rdt, off + 0x14)[0]
    ba = off + 0x18
    first_tri = struct.unpack_from('<I', rdt, ba + 8)[0] - B
    clut0 = struct.unpack_from('<H', rdt, first_tri + 0x0a)[0]
    clut_set = {clut0}
    allTP = Counter(); allCL = Counter()
    for i in range(bc):
        mA, mB = struct.unpack_from('<II', rdt, ba + i * 0x14 + 8)
        for m, stride in ((mA, 16), (mB, 20)):
            if not (B <= m < B + len(rdt)):
                continue
            _, tp, cl = walk(rdt, m - B, stride, clut_set)
            allTP.update(tp); allCL.update(cl)
    return bc, allTP, allCL


def cover_rect(ents, px, py):
    """texture entry (typ 1/5/6/8) whose RECT contains VRAM point (px,py)."""
    for i, typ, size, x, y, w, h, off in ents:
        if typ in (1, 5, 6, 8) and x <= px < x + w and y <= py < y + h:
            return (x, y, w, h)
    return None


def pal_rect(ents, cx, cy):
    for i, typ, size, x, y, w, h, off in ents:
        if typ == 2 and x == cx and y == cy:
            return (x, y, w, h)
    return None


def main():
    rows = []
    for cat, bones, name, room, mptr, op in SPECIES:
        rdt = get_rdt(room)
        b, ents = entries(room)
        bc, tp, cl = model_codes(rdt, mptr)
        assert bc == bones, f"{name}: model bones {bc} != expected {bones}"
        tpages = sorted(tp.keys())
        clut = cl.most_common(1)[0][0]
        # texture rect: covering block for each tpage (all enemy tpages share one column)
        texrect = None
        for t in tpages:
            px, py = tpage_origin(t)
            r = cover_rect(ents, px, py)
            if r:
                texrect = r; break
        cx, cy = clut_origin(clut)
        pr = pal_rect(ents, cx, cy)
        rows.append({
            "category": cat, "bones": bones, "name": name, "donor_room": room,
            "place_op": hex(op), "tpage_codes": [hex(t) for t in tpages],
            "clut_code": hex(clut),
            "texture_rect": list(texrect) if texrect else None,
            "palette_rect": list(pr) if pr else None,
        })
        print(f"OK {name} cat{cat}/{bones}b @st{room}: "
              f"tpage={[hex(t) for t in tpages]} clut={hex(clut)} "
              f"tex={texrect} pal={pr}  (tp count={dict(tp)})")
    print("\nJSON:\n" + json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
