import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Locate the enemy (E*.DAT) filename pointer table in Dino2.exe and, where possible,
per-stage enemy-load references. Read-only PE analysis (no disassembly emit).

Usage: python3 tools/dc2_re/exe_enemy_table.py <path-to-Dino2.exe>
"""
import struct, sys, re

def load(path):
    return open(path, "rb").read()

class PE:
    def __init__(self, exe):
        self.exe = exe
        pe = struct.unpack_from("<I", exe, 0x3c)[0]
        assert exe[pe:pe+4] == b"PE\0\0", "not a PE"
        nsec = struct.unpack_from("<H", exe, pe+6)[0]
        opthdr = pe + 24
        self.imagebase = struct.unpack_from("<I", exe, opthdr+28)[0]
        sizeof_opt = struct.unpack_from("<H", exe, pe+20)[0]
        so = opthdr + sizeof_opt
        self.secs = []
        for i in range(nsec):
            o = so + i*40
            name = exe[o:o+8].rstrip(b"\0").decode("latin1")
            vsz, va, rsz, rptr = struct.unpack_from("<IIII", exe, o+8)  # VSize,VAddr,RawSize,RawPtr
            self.secs.append((name, va, vsz, rptr, rsz))

    def foff_to_va(self, fo):
        for name, va, vsz, rptr, rsz in self.secs:
            if rsz and rptr <= fo < rptr+rsz:
                return self.imagebase + va + (fo - rptr)
        return None

    def va_to_foff(self, v):
        for name, va, vsz, rptr, rsz in self.secs:
            base = self.imagebase + va
            if base <= v < base + max(vsz, rsz):
                fo = rptr + (v - base)
                if rsz and fo < rptr + rsz:
                    return fo
        return None

def find_strings(exe, pat):
    out = {}
    for m in re.finditer(pat, exe):
        s = m.group().rstrip(b"\x00").decode("latin1")
        out[s] = m.start()
    return out

def main():
    path = sys.argv[1]
    exe = load(path)
    pe = PE(exe)
    print("imagebase=0x%08x" % pe.imagebase)
    for name, va, vsz, rptr, rsz in pe.secs:
        print("  %-8s VA=0x%08x VSize=0x%-6x RawPtr=0x%06x RawSize=0x%x"
              % (name, pe.imagebase+va, vsz, rptr, rsz))

    # all E*.DAT and ST*.DAT strings with file-offset + VA
    estr = find_strings(exe, rb"E[0-9A][0-9]?\.DAT\x00")
    estr = {k: v for k, v in estr.items() if re.fullmatch(r"E[0-9A][0-9]?\.DAT", k)}
    print("\nE*.DAT strings (file-order):")
    for k in sorted(estr, key=lambda s: estr[s]):
        print("  %-8s foff=0x%06x VA=0x%08x" % (k, estr[k], pe.foff_to_va(estr[k]) or 0))

    # VA->name for the on-disk 13
    disk = ["E00","E10","E20","E30","E31","E32","E40","E50","E60","E70","E80","E90","EA0"]
    va_of = {}
    for k in disk:
        fk = k + ".DAT"
        if fk in estr:
            va_of[k] = pe.foff_to_va(estr[fk])
    rev = {v: k for k, v in va_of.items()}
    vaset = set(va_of.values())

    # scan .data/.rdata for runs of pointers into the E-file string set
    print("\nPointer tables referencing E*.DAT strings:")
    i = 0
    n = len(exe)
    seen_runs = []
    while i < n-4:
        v = struct.unpack_from("<I", exe, i)[0]
        if v in vaset:
            lo = i
            run = []
            while i < n-4:
                w = struct.unpack_from("<I", exe, i)[0]
                if w in vaset:
                    run.append(rev[w]); i += 4
                else:
                    break
            if len(run) >= 3:
                seen_runs.append((lo, run))
                print("  TABLE foff=0x%06x VA=0x%08x  (%d entries):" % (lo, pe.foff_to_va(lo) or 0, len(run)))
                print("     idx->file:", " ".join("%d=%s" % (j, f) for j, f in enumerate(run)))
        else:
            i += 4
    if not seen_runs:
        print("  (none with >=3 consecutive entries)")
    return pe, estr, va_of, seen_runs

if __name__ == "__main__":
    main()
