import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Parse a DC1 room package and decode its type-8 TIM (texture) entries.

Offline texture-import investigation (docs/TEXTURE-IMPORT-VRAM.md).
Lists every Gian-package entry, decompresses the type-8 (GPC_LZSS2) texture
entries, finds embedded PSX TIM blocks (magic 0x00000010) and decodes each
TIM's VRAM framebuffer placement (DX/DY) + CLUT placement → the VRAM tpage
coordinates the model's primitives must reference.

PSX TIM layout:
  u32 magic = 0x10
  u32 flags : bits0-1 = bpp mode (0=4bpp,1=8bpp,2=16bpp,3=24bpp), bit3 = hasCLUT
  [if hasCLUT] CLUT block:  u32 byteLen; u16 DX,DY (VRAM x,y); u16 W,H; W*H u16 palette
  image block:             u32 byteLen; u16 DX,DY (VRAM x,y); u16 W,H (W in 16-bit units); pixels

VRAM is 1024x512 16-bit. A texture page is 64px(16bpp-equiv) wide x 256 tall:
  tpageX = DX // 64 ;  tpageY = DY // 256
"""
import struct, sys, os

HERE = os.path.dirname(__file__)
DATA = game_path("DC1", 'english/Data')
if not os.path.isdir(DATA):
    DATA = game_path("DC1", 'english/Data')

SECT = 2048
BPP = {0: "4bpp", 1: "8bpp", 2: "16bpp", 3: "24bpp"}


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


def decode_tim(buf, off):
    """Decode one TIM at buf[off]. Returns dict or None."""
    if off + 8 > len(buf): return None
    magic, flags = struct.unpack_from('<II', buf, off)
    if magic != 0x10: return None
    bpp = flags & 3
    has_clut = bool(flags & 8)
    p = off + 8
    clut = None
    if has_clut:
        blen, dx, dy, w, h = struct.unpack_from('<IHHHH', buf, p)
        clut = dict(byteLen=blen, dx=dx, dy=dy, w=w, h=h, at=p)
        p += blen
    if p + 12 > len(buf): return None
    iblen, idx, idy, iw, ih = struct.unpack_from('<IHHHH', buf, p)
    img = dict(byteLen=iblen, dx=idx, dy=idy, w=iw, h=ih, at=p)
    end = p + iblen
    return dict(off=off, end=end, bpp=bpp, has_clut=has_clut, clut=clut, img=img)


def scan_tims(buf):
    """Find every TIM magic and decode."""
    res = []
    i = 0
    while i + 8 <= len(buf):
        if struct.unpack_from('<I', buf, i)[0] == 0x10:
            # flags sanity: only low bits used
            fl = struct.unpack_from('<I', buf, i + 4)[0]
            if fl & ~0xF == 0:
                t = decode_tim(buf, i)
                if t and t['img']['w'] and t['img']['h'] and t['end'] <= len(buf):
                    res.append(t)
                    i = t['end']
                    continue
        i += 4
    return res


def dump_room(room):
    path = os.path.join(DATA, f"st{room}.dat")
    b = open(path, 'rb').read()
    ents = parse_entries(b)
    print(f"=== st{room}.dat ({len(b)} B) — {len(ents)} entries ===")
    for idx, typ, size, off, al in ents:
        tag = {0:'DATA',1:'TEX',2:'PAL',3:'SND',4:'MP3',5:'LZSS0',6:'LZSS1',7:'UNK/RDT',8:'LZSS2/TEX'}.get(typ, '?')
        print(f"  [{idx}] type={typ}({tag}) size={size:#x} off={off:#x} aligned={al:#x}")
    print()
    for idx, typ, size, off, al in ents:
        if typ != 8:
            continue
        raw = b[off:off + size]
        dec = lzss(raw)
        print(f"  --- entry[{idx}] type-8: raw {size:#x} -> decompressed {len(dec):#x} ---")
        tims = scan_tims(dec)
        # also try treating the entry as RAW TIM(s) (uncompressed)
        if not tims:
            tims = scan_tims(raw)
            if tims:
                print("     (TIMs found in RAW payload, not decompressed)")
                dec = raw
        print(f"     TIM blocks: {len(tims)}")
        for t in tims:
            c = t['clut']
            cstr = (f"CLUT@{c['at']:#x} vram=({c['dx']},{c['dy']}) {c['w']}x{c['h']}"
                    if c else "noCLUT")
            im = t['img']
            tpx, tpy = im['dx'] // 64, im['dy'] // 256
            print(f"     TIM @{t['off']:#06x} {BPP[t['bpp']]:5} "
                  f"img vram=({im['dx']},{im['dy']}) {im['w']}x{im['h']} "
                  f"tpage=({tpx},{tpy})  {cstr}")


if __name__ == "__main__":
    rooms = sys.argv[1:] or ["102", "203", "400"]
    for r in rooms:
        dump_room(r)
        print()
