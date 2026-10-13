import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC2 (and DC1) 'Gian' package parser — Python port of CPackageDC::OpenPC.

Ported inline from ref/DC2-Mod-SDK/extractor/DinoCrisis2.{h,cpp}.
Read-only investigation tool. Do NOT modify game files with this.
"""
import struct, sys

# PC entry type enum (GEntryTypePC)
GPC_DATA, GPC_TEXTURE, GPC_PALETTE, GPC_SOUND, GPC_MP3, GPC_LZSS0, GPC_LZSS1, GPC_UNK, GPC_LZSS2 = range(9)
PC_NAMES = {0:"DATA",1:"TEXTURE",2:"PALETTE",3:"SOUND",4:"MP3",5:"LZSS0",6:"LZSS1",7:"UNK",8:"LZSS2"}

def align(v, a=2048):
    return (v + a - 1) & ~(a - 1)

def lzss_dec(src):
    """Port of Dc2LzssDec. src=bytes -> bytes decompressed."""
    out = bytearray()
    i = 0
    n = len(src)
    flag = 1
    while i < n:
        if flag == 1:
            flag = src[i] | 0x100
            i += 1
            if i >= n:
                break
        ch = src[i]; i += 1
        if flag & 1:
            out.append(ch)
        else:
            if i >= n:
                break
            t = src[i]; i += 1
            jump = ((t & 0xF) << 8) | ch
            size = (t >> 4) + 2
            start = len(out) - jump
            for k in range(size):
                out.append(out[start + k])
        flag >>= 1
    return bytes(out)

class Entry:
    __slots__ = ("type","size","reserve","raw_off","raw_size","data","decomp")
    def __repr__(self):
        return f"<Entry {PC_NAMES.get(self.type,self.type)} size={self.size} raw_off={self.raw_off} raw_size={self.raw_size}>"

def parse(data, is_pc=True):
    """Parse a DC2 PC package. Returns (entries, pack_type_str).
    Decompresses LZSS0/LZSS2 (data) but leaves textures raw (no unswizzle)."""
    # detect entry size: DC2 => bytes[16:32]==0 -> 32-byte entries
    check = data[16:32]
    if check == b"\x00" * 16:
        entry_size = 32; pack = "DC2"
    else:
        entry_size = 16; pack = "DC1"
    entries = []
    si = 2048 // entry_size
    pos = 2048
    for i in range(si):
        eoff = i * entry_size
        etype, esize = struct.unpack_from("<II", data, eoff)
        reserve = data[eoff+8:eoff+entry_size]
        if etype > GPC_LZSS2 or etype < 0:
            break
        ssize = align(esize, 2048)
        seek = data[pos:pos+esize]
        e = Entry()
        e.type = etype; e.size = esize; e.reserve = reserve
        e.raw_off = pos; e.raw_size = ssize
        e.data = seek
        e.decomp = None
        if etype in (GPC_LZSS0, GPC_LZSS2):
            try:
                e.decomp = lzss_dec(data[pos:pos+esize])
            except Exception as ex:
                e.decomp = b""
        entries.append(e)
        pos += ssize
        if pos > len(data):
            break
    return entries, pack

def dump_header(data, n=8):
    out = []
    for i in range(n):
        v = struct.unpack_from("<I", data, i*4)[0]
        out.append(f"[0x{i*4:02x}]=0x{v:08x}")
    return "  ".join(out)

if __name__ == "__main__":
    for path in sys.argv[1:]:
        with open(path, "rb") as f:
            data = f.read()
        ents, pack = parse(data)
        print(f"\n=== {path}  ({len(data)} bytes)  pack={pack}  entries={len(ents)}")
        print("  hdr:", dump_header(data))
        for i, e in enumerate(ents):
            extra = ""
            if e.decomp is not None:
                extra = f" -> decomp {len(e.decomp)} B"
            # gfx coords for texture/palette
            if e.type in (GPC_TEXTURE, GPC_PALETTE, GPC_LZSS1):
                x,y,w,h = struct.unpack_from("<HHHH", e.reserve, 0)
                extra += f"  gfx x={x} y={y} w={w} h={h}"
            else:
                r0,r1 = struct.unpack_from("<II", e.reserve, 0)
                extra += f"  res0=0x{r0:08x} res1=0x{r1:08x}"
            print(f"  [{i:2}] {PC_NAMES.get(e.type,e.type):8} size={e.size:8} off=0x{e.raw_off:06x}{extra}")
