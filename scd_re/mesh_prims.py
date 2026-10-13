import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Dump an enemy model's per-bone meshA/meshB blocks and locate the PSX GPU
tpage/CLUT codes inside the primitive packets.

Model layout (cont.14): model+0x14 bonecount; bone array @+0x18 stride 0x14;
per-bone meshA ptr @+8, meshB ptr @+0xc.

DC/RE 'md1' mesh-object header (hypothesis, verified by dump):
  u32 vtx_ptr; u32 vtx_count; u32 nrm_ptr; u32 nrm_count;
  u32 tri_ptr; u32 tri_count; u32 quad_ptr; u32 quad_count;
Textured primitives carry PSX GPU codes: u16 clut, u16 tpage at fixed offsets.
We scan each mesh for plausible (clut,tpage) pairs and report the page coords.
"""
import struct, os, sys
from scd import rdt_index

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
    payload = b[off:off + size]
    return lzss(payload) if typ == 7 else payload


def decode_tpage(a):
    xb = (a & 0xF) * 64
    yb = ((a >> 4) & 1) * 256
    tp = (a >> 7) & 3
    return xb, yb, {0: '4bpp', 1: '8bpp', 2: '16bpp', 3: '?'}[tp]


def decode_clut(c):
    return (c & 0x3F) * 16, (c >> 6) & 0x1FF  # X (16-unit), Y


def hexd(buf, off, n):
    return ' '.join(f'{buf[off+i]:02x}' for i in range(min(n, len(buf) - off)))


def dump_mesh(rdt, mptr, name, nbones_limit=99):
    off = mptr - B
    hdr = struct.unpack_from('<IIII', rdt, off)
    bc = struct.unpack_from('<I', rdt, off + 0x14)[0]
    print(f"\n## {name} model={mptr:#010x} off={off:#x} bonecount={bc} hdr={[hex(x) for x in hdr]}")
    ba = off + 0x18
    seen = []
    for i in range(min(bc, nbones_limit)):
        mA, mB = struct.unpack_from('<II', rdt, ba + i * 0x14 + 8)
        for tag, m in (('A', mA), ('B', mB)):
            if not (B <= m < B + len(rdt)):
                continue
            mo = m - B
            # md1 object header: 8 dwords
            v = struct.unpack_from('<8I', rdt, mo)
            print(f"  b{i:<2}{tag} @ {m:#x} (off {mo:#x}) hdr8={[hex(x) for x in v]}")
            print(f"        raw: {hexd(rdt, mo, 48)}")
            seen.append((i, tag, mo, v))
    return seen


def scan_tpage_codes(rdt, lo, hi):
    """Brute scan a byte range for plausible tpage codes used by the room's
    DATA descriptor set (0x82/0x83/0x182/0x183...) to spot where the model
    encodes them."""
    hits = {}
    for o in range(lo, hi - 1):
        w = struct.unpack_from('<H', rdt, o)[0]
        if w in (0x82, 0x83, 0x182, 0x183, 0x80, 0x81, 0x88, 0x8a):
            hits.setdefault(w, []).append(o)
    return hits


if __name__ == "__main__":
    # native Pteranodon in st400 (cont.27): model 0x80109bd8, 18 bones
    rdt = get_rdt("400")
    seen = dump_mesh(rdt, 0x80109bd8, "st400 Pteranodon", nbones_limit=4)
    # show span of the model block & scan for tpage-like codes in first meshes
    if seen:
        lo = min(s[2] for s in seen)
        hi = max(s[2] for s in seen) + 0x400
        hits = scan_tpage_codes(rdt, lo, hi)
        print("\n  tpage-like u16 hits near meshes:")
        for w, offs in sorted(hits.items()):
            print(f"   {w:#06x}: {[hex(o) for o in offs[:12]]}{' ...' if len(offs)>12 else ''}")
