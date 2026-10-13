import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""I1 decode validator for docs/dc2/DC2-BGM-RANDO-PLAN.md (DC2 BGM rando).

Re-derives, from the pristine rebirth build, every fact the C# lever
(Dc2MusicTablePatch) bakes in, and exits non-zero on any mismatch:

  1. the in-EXE Data filename pointer table music slice (base VA 0x71b230,
     indices 150..217, all M[SEF]_*.DAT, contiguous);
  2. the consumer disasm byte-cite at 0x404280
     (mov eax,[0x878fa8]; and eax,0xffff; mov eax,[eax*4+0x71b230]);
  3. all on-disk music containers parse as {u32 4, u32 payloadBytes,
     u32 trackIndex, u32 flag(0|1), pad16} track records + payloads;
  4. every music filename string is a unique NUL-bounded occurrence in .data
     (the string-relocation invariant the C# patch relies on);
  5. prints the like-class grouping (trackIndexSet) the shuffle uses.

Read-only on the game install. Usage:
  python3 tools/dc2_re/ms_bgm_probe.py [gameRoot=4249140_DinoCrisis2/rebirth]
"""
import os
import re
import struct
import sys

# Dino2.exe section map (objdump -h): .data file 0x100000, VA 0x71b000.
DATA_FILE_OFF = 0x100000
DATA_VA = 0x71B000
DATA_RAW_SIZE = 0x25000
TABLE_BASE_VA = 0x71B230
MUSIC_FIRST = 150
MUSIC_LAST = 217
CONSUMER_VA = 0x404280
CONSUMER_FILE_OFF = CONSUMER_VA - 0x400000  # .text: file off == VA - imagebase
# mov eax,[0x878fa8]; sub esp,0x28; and eax,0xffff; push edi; mov eax,[eax*4+0x71b230]
CONSUMER_BYTES = bytes.fromhex("a1a88f870083ec2825ffff0000578b048530b27100")
MUSIC_ID_GLOBAL = 0x878FA8

MUSIC_NAME = re.compile(r"M[SEF]_[0-9A-F_]+\.DAT")

fails = 0


def check(ok, msg):
    global fails
    print(("PASS " if ok else "FAIL ") + msg)
    if not ok:
        fails += 1


def read_cstr(exe, va):
    if not (DATA_VA <= va < DATA_VA + DATA_RAW_SIZE):
        return None
    off = DATA_FILE_OFF + va - DATA_VA
    end = exe.index(b"\x00", off)
    try:
        return exe[off:end].decode("ascii")
    except UnicodeDecodeError:
        return None


def parse_container(path):
    """Return the sorted trackIndex list, or None if the format check fails.

    Layout: one 0x800 header sector — N x 32-byte records {u32 4, u32 payloadBytes,
    u32 trackIndex, u32 flag(0|1)}, pad16=0, then 'dummy header    ' filler to 0x800 —
    followed by the N payloads, each 2048-aligned.
    """
    d = open(path, "rb").read()
    off, idxs, total = 0, [], 0x800
    while off + 32 <= 0x800:
        magic, size, tidx, flag = struct.unpack_from("<IIII", d, off)
        if magic != 4:
            break
        if flag not in (0, 1) or d[off + 16:off + 32] != b"\x00" * 16:
            return None
        idxs.append(tidx)
        total += (size + 2047) & ~2047
        off += 32
    if not idxs or total != len(d):
        return None
    if d[off:off + 12] != b"dummy header":  # header filler pattern
        return None
    return sorted(idxs)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "4249140_DinoCrisis2/rebirth"
    exe = open(os.path.join(root, "Dino2.exe"), "rb").read()
    data_dir = os.path.join(root, "Data")

    # 1. table slice
    entries = []
    for i in range(MUSIC_FIRST, MUSIC_LAST + 1):
        va = struct.unpack_from("<I", exe, DATA_FILE_OFF + (TABLE_BASE_VA - DATA_VA) + i * 4)[0]
        entries.append((i, read_cstr(exe, va)))
    check(all(n and MUSIC_NAME.fullmatch(n) for _, n in entries),
          f"table slice {MUSIC_FIRST}..{MUSIC_LAST}: 68 M[SEF]_*.DAT entries")
    for j in (MUSIC_FIRST - 1, MUSIC_LAST + 1):  # slice bounds are tight
        va = struct.unpack_from("<I", exe, DATA_FILE_OFF + (TABLE_BASE_VA - DATA_VA) + j * 4)[0]
        n = read_cstr(exe, va)
        check(not (n and MUSIC_NAME.fullmatch(n)), f"entry {j} outside slice is not music ({n})")

    # 2. consumer byte-cite
    got = exe[CONSUMER_FILE_OFF:CONSUMER_FILE_OFF + len(CONSUMER_BYTES)]
    check(got == CONSUMER_BYTES,
          f"consumer 0x{CONSUMER_VA:x} loads [0x{MUSIC_ID_GLOBAL:x}]*4 + 0x{TABLE_BASE_VA:x} ({got.hex()})")

    # 3. containers parse; 4. string uniqueness; 5. class grouping
    data_seg = exe[DATA_FILE_OFF:DATA_FILE_OFF + DATA_RAW_SIZE]
    classes = {}
    parsed = missing = 0
    for i, name in entries:
        uniq = data_seg.count(b"\x00" + name.encode() + b"\x00")
        if uniq != 1:
            check(False, f"string '{name}' occurs {uniq}x in .data (need exactly 1)")
        path = os.path.join(data_dir, name)
        if not os.path.exists(path):
            missing += 1
            print(f"       (no file on disk: {name} — vestigial entry, excluded from shuffle)")
            continue
        idxs = parse_container(path)
        if idxs is None:
            check(False, f"container {name} failed format parse")
            continue
        parsed += 1
        classes.setdefault(tuple(idxs), []).append(name)
    check(parsed == 68 - missing, f"{parsed}/{68 - missing} on-disk containers parsed ({missing} vestigial)")

    print("\nlike-classes (trackIndexSet -> shuffle group):")
    for k in sorted(classes):
        print(f"  {list(k)}: {len(classes[k])} files: {', '.join(sorted(classes[k]))}")

    print(f"\n{'OK' if fails == 0 else f'{fails} FAILURES'}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
