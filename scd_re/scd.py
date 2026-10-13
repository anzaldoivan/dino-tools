import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Shared DC1 SCD walker (init script via rec+0x14 func table).

Lengths: scd_opcode_table.json + control-flow lengths resolved 2026-06-19 from
DINO.exe handler pc-advances. 0x28 and 0x5a are variable.
"""
import json, struct, os

PSX_RDT_BASE = 0x80100000
_HERE = os.path.dirname(__file__)
T = {int(k, 16): v['len'] for k, v in json.load(open(os.path.join(_HERE, 'scd_opcode_table.json'))).items()}
# control-flow encoded lengths resolved from handler pc-advances (DINO.exe):
T.update({0x0a: 4, 0x0c: 4, 0x0f: 4, 0x10: 2, 0x11: 2,
          0x31: 4, 0x54: 4, 0x57: 8, 0x59: 20})
OP28 = {0: 48, 1: 40, 2: 32, 3: 36, 4: 44, 5: 32, 6: 32, 7: 52, 8: 32, 9: 32, 0xa: 32, 0xb: 32}


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
    HEADER = 2048; SECT = 2048; es = 16; pos = HEADER; out = []
    for i in range(HEADER // es):
        typ, size = struct.unpack_from('<II', b, i * es)
        if typ > 8 or size == 0: break
        if pos + size > len(b): break
        al = (size + SECT - 1) & ~(SECT - 1)
        out.append((typ, size, pos, al)); pos += al
    return out


def get_rdt(path):
    b = open(path, 'rb').read()
    ents = parse_entries(b)
    if not ents:
        return None, None
    typ, size, off, al = ents[-1]
    payload = b[off:off + size]
    return (lzss(payload) if typ == 7 else payload), typ


def oplen(buf, pos):
    op = buf[pos]
    if op == 0x28:
        return OP28.get(buf[pos + 2])
    if op == 0x5a:
        return 4 + 8 * buf[pos + 3]
    return T.get(op)


def func_table(rdt):
    """rec+0x14 -> init-script function-offset table. Returns (base, [sub_start,...])."""
    base = struct.unpack_from('<I', rdt, 0x14)[0] - PSX_RDT_BASE
    if not (0 <= base < len(rdt) - 4):
        return None, []
    first = struct.unpack_from('<I', rdt, base)[0]
    if first % 4 or first == 0 or base + first > len(rdt):
        return None, []
    n = first // 4
    ents = [struct.unpack_from('<I', rdt, base + i * 4)[0] for i in range(n)]
    return base, [base + e for e in ents]


def walk_sub(rdt, start, end):
    pos = start; ops = []
    while pos < end:
        op = rdt[pos]; ln = oplen(rdt, pos)
        if not ln or pos + ln > end:
            ops.append((pos, op, None)); return ops, False
        ops.append((pos, op, ln)); pos += ln
    return ops, True


def walk_room(path):
    """Walk every init subroutine. Returns (rdt, list of (sub_idx, ops, ok))."""
    rdt, typ = get_rdt(path)
    if rdt is None:
        return None, []
    base, starts = func_table(rdt)
    if not starts:
        return rdt, []
    res = []
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, ok = walk_sub(rdt, s, e)
        res.append((i, ops, ok))
    return rdt, res
