import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Decode a DC2 ST*.DBS background file. Read-only.

A .DBS is a run of DC2 packages laid end to end, one per camera of the room
(K142). Each package:
  entry 0  type 0, load 0x6B9500: mask table = u32 count, then count 4-byte
           records {u8 tile_x, u8 tile_y, s16 depth}; pixel = tile*16 (reader
           0x4036F0: x = b0<<4, y = (b1 & 0xF)<<4, depth = s16 @+2).
  entry 1  type 0, load 0x6C1500: a JFIF, 320x240 in all 1169.
  entry 2  type 2 palette at VRAM (0,496)            } absent in a 2-entry
  entry 3  type 0: LZSS, unpacked size in the field  } package ("dummy header"
           normally holding the load address; count  } text at +0x40, tested
           x 18x18 16-bit cells (16x16 tile plus a   } at 0x403C1A)
           1-pixel border; opaque when bit 0x8000 is set).
           type 8: an LZSS 8-bit picture for VRAM (320,0); such packages
           have no mask tiles.
Streamer: 0x403AB0. Usage: decode_dbs.py [ST101.DBS ...]   (no args = census)
"""
import os, struct, glob, collections
_path_sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dc2pkg import lzss_dec

REB = game_path("DC2", "rebirth/Data")
SECTOR = 0x800


def packages(data):
    """[(offset, [(type, size, payload_offset, reserve24)])], end offset."""
    pos, out = 0, []
    while pos + SECTOR <= len(data):
        ents, p = [], pos + SECTOR
        for i in range(SECTOR // 32):
            t, s = struct.unpack_from("<II", data, pos + i * 32)
            if s == 0 or t > 8:
                break
            ents.append((t, s, p, data[pos + i * 32 + 8:pos + i * 32 + 32]))
            p += (s + SECTOR - 1) & ~(SECTOR - 1)
        if not ents:
            break
        out.append((pos, ents))
        pos = p
    return out, pos


def jpeg_size(j):
    i = 2
    while i + 9 < len(j):
        if j[i + 1] in (0xC0, 0xC2):
            h, w = struct.unpack_from(">HH", j, i + 5)
            return w, h
        i += 2 + struct.unpack_from(">H", j, i + 2)[0]
    return None


def background(data, pkg):
    """Decode one package -> dict(jpeg, tiles[(x,y,depth)], cells, extra)."""
    _, ents = pkg
    get = lambda i: data[ents[i][2]:ents[i][2] + ents[i][1]]
    tab = get(0)
    n = struct.unpack_from("<I", tab)[0]
    b = {"jpeg": get(1), "cells": None, "extra": None,
         "tiles": [(x * 16, (y & 15) * 16, d) for x, y, d in
                   (struct.unpack_from("<BBh", tab, 4 + 4 * i) for i in range(n))]}
    if len(ents) > 3:
        px = lzss_dec(get(3))
        if ents[3][0] == 0:
            b["cells"] = px          # n * 18*18 u16, row-major per tile
        else:
            b["extra"] = (struct.unpack_from("<4H", ents[3][3]), px)
    return b


def census(paths):
    c = collections.Counter()
    for f in paths:
        data = _ToolPath(f).read_bytes()
        pk, end = packages(data)
        c["files"] += 1
        c["unclosed"] += end != len(data)
        for p in pk:
            b = background(data, p)
            c["packages"] += 1
            c["shape %s" % (tuple(e[0] for e in p[1]),)] += 1
            c["jpeg %s" % (jpeg_size(b["jpeg"]),)] += 1
            c["tiles"] += len(b["tiles"])
            if b["cells"] is not None:
                c["cells_bad"] += len(b["cells"]) != 648 * len(b["tiles"])
    return c


if __name__ == "__main__":
    args = _path_sys.argv[1:]
    if not args:
        for k, v in sorted(census(sorted(glob.glob(os.path.join(REB, "*.DBS")))).items()):
            print(f"{v:8} {k}")
    for a in args:
        data = _ToolPath(a if os.path.exists(a) else os.path.join(REB, a)).read_bytes()
        pk, end = packages(data)
        print(f"{a}: {len(pk)} backgrounds, closes={end == len(data)}")
        for i, p in enumerate(pk):
            b = background(data, p)
            print(f"  {i:2} @0x{p[0]:06x} jpeg {len(b['jpeg'])} B {jpeg_size(b['jpeg'])} "
                  f"mask tiles {len(b['tiles'])}" + (f" extra {b['extra'][0]}" if b["extra"] else ""))
