import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Self-validating primitive walk: confirms packet layout AND counts primitives.

For each bone mesh, step by stride (16 tri / 20 quad) and read CLUT@+0xa,
tpage@+0xe. Stop as soon as CLUT@+0xa leaves the model's CLUT marker set
(detected from the first packet). A wrong stride/offset would break the
invariant on packet 2, so a long clean run is strong proof of the layout.
"""
import struct, os
from collections import Counter

DATA = game_path("DC1", 'english/Data')
if not os.path.isdir(DATA):
    DATA = game_path("DC1", 'english/Data')
B = 0x80100000
SECT = 2048


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


def get_rdt(room):
    b = open(os.path.join(DATA, f"st{room}.dat"), 'rb').read()
    pos = 2048; ents = []
    for i in range(128):
        typ, size = struct.unpack_from('<II', b, i * 16)
        if typ > 8 or size == 0: break
        if pos + size > len(b): break
        ents.append((typ, size, pos, (size + SECT - 1) & ~(SECT - 1))); pos += (size + SECT - 1) & ~(SECT - 1)
    typ, size, off, al = ents[-1]
    return lzss(b[off:off + size]) if typ == 7 else b[off:off + size]


def walk(rdt, head, stride, clut_set, limit):
    o = head; n = 0; tp = Counter()
    while o + stride <= len(rdt) and n < limit:
        if struct.unpack_from('<H', rdt, o + 0x0a)[0] not in clut_set:
            break
        tp[struct.unpack_from('<H', rdt, o + 0x0e)[0]] += 1
        o += stride; n += 1
    return n, tp, o


def tpage_str(a):
    return f"{a:#06x}(X{(a&0xF)*64},Y{((a>>4)&1)*256},{['4','8','16','?'][(a>>7)&3]}bpp)"


def analyze(room, mptr, name):
    rdt = get_rdt(room)
    off = mptr - B
    hdr = struct.unpack_from('<IIII', rdt, off)
    bc = struct.unpack_from('<I', rdt, off + 0x14)[0]
    ba = off + 0x18
    # CLUT marker(s): the single dominant clut at the first triangle's +0xa
    first_tri = struct.unpack_from('<I', rdt, ba + 8)[0] - B
    clut0 = struct.unpack_from('<H', rdt, first_tri + 0x0a)[0]
    clut_set = {clut0}
    print(f"\n## {name}: st{room} model={mptr:#010x} bones={bc} CLUTmarker={clut0:#06x}")
    allTP = Counter(); total_tri = total_quad = 0
    for i in range(bc):
        mA, mB = struct.unpack_from('<II', rdt, ba + i * 0x14 + 8)
        line = f"   b{i:<2}"
        for tag, m, stride in (('tri', mA, 16), ('quad', mB, 20)):
            if not (B <= m < B + len(rdt)):
                line += f"  {tag}=-"; continue
            n, tp, _ = walk(rdt, m - B, stride, clut_set, 4096)
            allTP.update(tp)
            if tag == 'tri': total_tri += n
            else: total_quad += n
            line += f"  {tag}@{m-B:#07x}x{n}{list(map(hex,tp))}"
        if i < 4 or i == bc - 1:
            print(line)
    print(f"   TOTAL tris={total_tri} quads={total_quad}; CLUT(all)={hex(clut0)}")
    print(f"   tpages used: " + ", ".join(tpage_str(t) + f"x{n}" for t, n in allTP.most_common()))
    return clut0, allTP


if __name__ == "__main__":
    analyze("400", 0x80109bd8, "Pteranodon (donor)")
    analyze("10e", 0x80110e70, "Raptor (st10e)")
