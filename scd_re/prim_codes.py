import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Verify the primitive packet layout and enumerate every (CLUT,tpage) code a
model uses. Detects packet stride from the constant CLUT marker period.

Found layout (16B tri / 20B quad), both share:
  +0x00.. vertex indices (3xu16 tri / 4xu16 quad)
  +0x0a  u16 CLUT   (palette VRAM ref; constant 0x7ff0 for the Pteranodon)
  +0x0e  u16 tpage  (texture-page ref; 0x8b/0x9b for the Pteranodon)
"""
import struct, os, sys
from scd import rdt_index
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
        al = (size + SECT - 1) & ~(SECT - 1)
        ents.append((typ, size, pos, al)); pos += al
    typ, size, off, al = ents[rdt_index(b)]
    return lzss(b[off:off + size]) if typ == 7 else b[off:off + size]


def tpage_str(a):
    xb = (a & 0xF) * 64
    yb = ((a >> 4) & 1) * 256
    return f"{a:#06x}(X{xb},Y{yb},{['4','8','16','?'][(a>>7)&3]}bpp)"


def clut_str(c):
    return f"{c:#06x}(VRAM {(c&0x3F)*16},{(c>>6)&0x1FF})"


def codes_in_mesh(rdt, head, end, stride):
    """Read CLUT@+0xa, tpage@+0xe per packet of given stride in [head,end)."""
    cl, tp = Counter(), Counter()
    o = head
    while o + stride <= end:
        cl[struct.unpack_from('<H', rdt, o + 0x0a)[0]] += 1
        tp[struct.unpack_from('<H', rdt, o + 0x0e)[0]] += 1
        o += stride
    return cl, tp


def detect_stride(rdt, head, marker=0x7ff0):
    """Find packet stride = period of the CLUT marker at +0xa."""
    first = None; second = None
    for o in range(head, head + 0x400):
        if o + 2 <= len(rdt) and struct.unpack_from('<H', rdt, o)[0] == marker:
            if (o - head) % 1 == 0:
                if first is None and (o - head) == 0x0a:
                    first = o
                elif first is not None:
                    second = o; break
    return (second - first) if (first and second) else None


def analyze(room, mptr, name):
    rdt = get_rdt(room)
    off = mptr - B
    hdr = struct.unpack_from('<IIII', rdt, off)
    bc = struct.unpack_from('<I', rdt, off + 0x14)[0]
    print(f"\n## {name}: st{room} model={mptr:#010x} bones={bc}")
    print(f"   header ptrs: vtx?={hdr[0]:#x} nrm?={hdr[1]:#x} triPool={hdr[2]:#x} quadPool={hdr[3]:#x}")
    ba = off + 0x18
    bone_heads = []
    for i in range(bc):
        mA, mB = struct.unpack_from('<II', rdt, ba + i * 0x14 + 8)
        bone_heads.append((i, mA, mB))
    # The triangle pool (header[2]) and quad pool (header[3]) are SEPARATE contiguous
    # regions; bound an A-mesh only by other A-heads (and quadPool start), a B-mesh only
    # by other B-heads (and buffer end) — mixing them caused the earlier over-read.
    tri_heads = sorted(set(a - B for (_, a, _) in bone_heads if B <= a < B + len(rdt)))
    quad_heads = sorted(set(b - B for (_, _, b) in bone_heads if B <= b < B + len(rdt)))
    tri_pool_end = (hdr[3] - B) if B <= hdr[3] < B + len(rdt) else len(rdt)

    def next_in(heads, h, hard_end):
        for x in heads:
            if x > h:
                return x
        return hard_end
    CL, TP = Counter(), Counter()
    for i, mA, mB in bone_heads:
        for tag, m, stride, heads, hard in (('A', mA, 16, tri_heads, tri_pool_end),
                                            ('B', mB, 20, quad_heads, len(rdt))):
            if not (B <= m < B + len(rdt)):
                continue
            h = m - B
            st = detect_stride(rdt, h) or stride
            cl, tp = codes_in_mesh(rdt, h, next_in(heads, h, hard), st)
            CL.update(cl); TP.update(tp)
            if i < 3:
                print(f"   b{i}{tag} @{m:#x} stride={st} "
                      f"clut={[hex(x) for x in cl]} tpage={[hex(x) for x in tp]}")
    print(f"   ALL CLUT codes used: " + ", ".join(clut_str(c) + f"x{n}" for c, n in CL.most_common()))
    print(f"   ALL tpage codes used: " + ", ".join(tpage_str(t) + f"x{n}" for t, n in TP.most_common()))
    return CL, TP


if __name__ == "__main__":
    # Pteranodon (donor, st400) vs a native raptor (st102 slot0 is RaptorHeavy)
    analyze("400", 0x80109bd8, "Pteranodon (donor)")
    # find st102 raptor model ptr from its enemy record via model_footprint? use known native.
    # st10e raptor (cont.16): 0x80110e70
    analyze("10e", 0x80110e70, "Raptor (st10e)")
