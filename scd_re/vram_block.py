import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Decode DC1 type-8 entries as raw-VRAM upload blocks (NOT TIM files).

tim_probe.py showed type-8 decompresses to N*0x10000 + 8 bytes => an 8-byte
header + raw 16bpp VRAM pixels. This script dumps that header and the type-0
DATA descriptor (cont.3/cont.7: 'rgb0,rgb1 + 83 00 XX 00 texture-page coords')
to recover the VRAM destination rect (PSX LoadImage RECT: x,y,w,h).
"""
import struct, sys, os

DATA = game_path("DC1", 'english/Data')
if not os.path.isdir(DATA):
    DATA = game_path("DC1", 'english/Data')
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


def parse_entries(b):
    pos = 2048; out = []
    for i in range(2048 // 16):
        typ, size = struct.unpack_from('<II', b, i * 16)
        if typ > 8 or size == 0: break
        if pos + size > len(b): break
        al = (size + SECT - 1) & ~(SECT - 1)
        out.append((i, typ, size, pos, al)); pos += al
    return out


def hexd(buf, off, n):
    return ' '.join(f'{buf[off+i]:02x}' for i in range(min(n, len(buf) - off)))


def dump_room(room):
    path = os.path.join(DATA, f"st{room}.dat")
    b = open(path, 'rb').read()
    ents = parse_entries(b)
    print(f"=== st{room}.dat ({len(b)} B) ===")
    for idx, typ, size, off, al in ents:
        if typ == 0:  # DATA descriptor
            print(f"  DATA[{idx}] off={off:#x} size={size:#x}")
            print(f"     first 0x40: {hexd(b, off, 0x40)}")
            # parse as sequence of u16
            words = struct.unpack_from('<%dH' % (min(size, 0x40) // 2), b, off)
            print(f"     u16[0..32): {[hex(w) for w in words]}")
        if typ == 8:
            raw = b[off:off + size]
            dec = lzss(raw)
            # 8-byte header: try u16 x,y,w,h
            x, y, w, h = struct.unpack_from('<HHHH', dec, 0)
            payload = len(dec) - 8
            print(f"  TYPE8[{idx}] raw={size:#x} dec={len(dec):#x} payload={payload:#x}")
            print(f"     hdr8: {hexd(dec, 0, 8)}  => x={x} y={y} w={w} h={h}  "
                  f"w*h*2={w*h*2:#x}  match={'Y' if w*h*2==payload else 'N'}")
            tpx, tpy = x // 64, y // 256
            print(f"     VRAM dst rect=({x},{y}) {w}x{h}  tpageBase=({tpx},{tpy})")
    print()


if __name__ == "__main__":
    rooms = sys.argv[1:] or ["102", "203", "400"]
    for r in rooms:
        dump_room(r)
