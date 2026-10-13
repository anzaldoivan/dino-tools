import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Corpus scan: which rooms upload a texture block to the ENEMY VRAM slot
(X>=512 texture area, esp. the X=704/X=640 enemy pages) and the palette rows.

Entry header (confirmed): u32 type,u32 size,u16 dstX,u16 dstY,u16 W,u16 H.
A room that hosts a textured enemy uploads a type-8 block at (>=512,0,~64,512)
plus a type-2 palette at (768,Yclut). A room WITHOUT such a block leaves the
enemy VRAM pages unpopulated -> an imported enemy sampling X=704 reads invalid
VRAM there.
"""
import struct, os, glob, re

DATA = game_path("DC1", 'english/Data')
if not os.path.isdir(DATA):
    DATA = game_path("DC1", 'english/Data')


def entries(b):
    out = []
    for i in range(128):
        typ, size, x, y, w, h = struct.unpack_from('<IIHHHH', b, i * 16)
        if typ > 8 or size == 0:
            break
        out.append((typ, size, x, y, w, h))
    return out


def room_tex_slots(path):
    b = open(path, 'rb').read()
    tex = []   # type-8 blocks in the texture area (x>=512)
    pal = []   # palettes in the enemy palette column (x>=512)
    for typ, size, x, y, w, h in entries(b):
        if typ == 8 and x >= 512:
            tex.append((x, y, w, h))
        if typ == 2 and x >= 512:
            pal.append((x, y, w, h))
    return tex, pal


if __name__ == "__main__":
    files = sorted(glob.glob(os.path.join(DATA, "st*.dat")))
    have_704 = have_640 = none_enemy = 0
    rows = []
    for f in files:
        name = os.path.basename(f)
        m = re.match(r"st([0-9a-cA-C]+)\.dat", name)
        if not m or len(m.group(1)) < 2:
            continue  # skip stage overlays stN.dat
        tex, pal = room_tex_slots(f)
        xs = sorted(set(t[0] for t in tex))
        has704 = any(t[0] == 704 for t in tex)
        has640 = any(t[0] == 640 for t in tex)
        if has704: have_704 += 1
        if has640: have_640 += 1
        if not tex:
            none_enemy += 1
        rows.append((name, xs, [p[1] for p in pal], has704))
    print(f"rooms scanned: {len(rows)}")
    print(f"  with a type-8 texture block at X=704: {have_704}")
    print(f"  with a type-8 texture block at X=640: {have_640}")
    print(f"  with NO texture block in X>=512 area: {none_enemy}")
    print()
    # show the rooms of interest + any room lacking X=704
    for name, xs, paly, has704 in rows:
        if name in ("st102.dat", "st203.dat", "st400.dat", "st10e.dat") or not has704:
            print(f"  {name:12} texX={xs} palY={paly} has704={has704}")
