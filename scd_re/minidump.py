import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Minimal hand-rolled minidump (MDMP) reader for the DINO.exe crash dumps.

No cdb/WinDbg, no python `minidump` lib. Parses just enough:
  - stream directory (header+12)
  - ExceptionStream (type 6): faulting ThreadId, EXCEPTION_RECORD, CONTEXT RVA
  - Memory64ListStream (type 9): builds a VA->file-offset map to read process memory
  - x86 CONTEXT register extraction

Usage:
  python minidump.py <dump> info            # exception + faulting registers
  python minidump.py <dump> read <VA-hex> <len>   # hex dump of process memory at VA
  python minidump.py <dump> dw <VA-hex> <count>   # read <count> dwords at VA
"""
import sys, struct

class MiniDump:
    def __init__(self, path):
        self.f = open(path, "rb")
        d = self.f.read(4)
        assert d == b"MDMP", f"bad signature {d!r}"
        self.f.seek(8)
        nstreams, dir_rva = struct.unpack("<II", self.f.read(8))
        self.streams = {}
        self.f.seek(dir_rva)
        for _ in range(nstreams):
            stype, dsize, drva = struct.unpack("<III", self.f.read(12))
            self.streams.setdefault(stype, (dsize, drva))
        self.mem = []  # list of (va_start, size, file_off)
        self._parse_mem64()
        self._parse_mem32()

    def _parse_mem64(self):
        if 9 not in self.streams:
            return
        _, rva = self.streams[9]
        self.f.seek(rva)
        nranges, base_rva = struct.unpack("<QQ", self.f.read(16))
        descs = []
        for _ in range(nranges):
            start, size = struct.unpack("<QQ", self.f.read(16))
            descs.append((start, size))
        off = base_rva
        for start, size in descs:
            self.mem.append((start, size, off))
            off += size

    def _parse_mem32(self):
        # MemoryListStream (type 5): 32-bit MINIDUMP_MEMORY_DESCRIPTORs, each with its own data RVA.
        # Written by MiniDumpNormal-style handlers (e.g. the DC2 Rebirth wrapper's crash dumper),
        # which the WER dumps' Memory64List path above never sees.
        if 5 not in self.streams:
            return
        _, rva = self.streams[5]
        self.f.seek(rva)
        (nranges,) = struct.unpack("<I", self.f.read(4))
        for _ in range(nranges):
            start, dsize, drva = struct.unpack("<QII", self.f.read(16))
            self.mem.append((start, dsize, drva))

    def va_to_off(self, va):
        for start, size, foff in self.mem:
            if start <= va < start + size:
                return foff + (va - start)
        return None

    def read(self, va, n):
        out = bytearray()
        while n > 0:
            o = self.va_to_off(va)
            if o is None:
                return bytes(out)  # short read at unmapped
            # how far until end of this range
            for start, size, foff in self.mem:
                if start <= va < start + size:
                    avail = (start + size) - va
                    break
            take = min(n, avail)
            self.f.seek(o)
            out += self.f.read(take)
            va += take
            n -= take
        return bytes(out)

    def dw(self, va):
        b = self.read(va, 4)
        if len(b) < 4:
            return None
        return struct.unpack("<I", b)[0]

    def exception(self):
        _, rva = self.streams[6]
        self.f.seek(rva)
        tid, align = struct.unpack("<II", self.f.read(8))
        # EXCEPTION_RECORD (MINIDUMP_EXCEPTION)
        code, flags, nested, addr = struct.unpack("<IIQQ", self.f.read(24))
        nparams, _align = struct.unpack("<II", self.f.read(8))
        params = struct.unpack("<15Q", self.f.read(120))
        # MINIDUMP_LOCATION_DESCRIPTOR for the CONTEXT
        ctx_size, ctx_rva = struct.unpack("<II", self.f.read(8))
        return {
            "tid": tid, "code": code, "flags": flags, "addr": addr,
            "nparams": nparams, "params": params[:nparams],
            "ctx_size": ctx_size, "ctx_rva": ctx_rva,
        }

    def context(self, rva):
        # x86 CONTEXT offsets (from the brief, verified):
        # Edi@156 Esi@160 Ebx@164 Edx@168 Ecx@172 Eax@176 Ebp@180 Eip@184 Esp@196
        self.f.seek(rva)
        ctx = self.f.read(716)
        names = {"Edi":156,"Esi":160,"Ebx":164,"Edx":168,"Ecx":172,
                 "Eax":176,"Ebp":180,"Eip":184,"Esp":196,
                 "SegCs":188,"EFlags":192,"SegSs":200}
        return {k: struct.unpack_from("<I", ctx, v)[0] for k, v in names.items()}


def main():
    path = sys.argv[1]
    cmd = sys.argv[2] if len(sys.argv) > 2 else "info"
    md = MiniDump(path)
    if cmd == "info":
        ex = md.exception()
        print(f"exception code  : 0x{ex['code']:08x}")
        print(f"faulting addr   : 0x{ex['addr']:08x}")
        print(f"thread id       : 0x{ex['tid']:x}")
        print(f"exc params      : {[hex(p) for p in ex['params']]}")
        c = md.context(ex["ctx_rva"])
        for k in ("Eip","Eax","Ebx","Ecx","Edx","Esi","Edi","Ebp","Esp","EFlags"):
            print(f"  {k:5s}= 0x{c[k]:08x}")
        print(f"mem ranges      : {len(md.mem)}")
    elif cmd == "read":
        va = int(sys.argv[3], 16); n = int(sys.argv[4], 0)
        b = md.read(va, n)
        for i in range(0, len(b), 16):
            row = b[i:i+16]
            hexs = " ".join(f"{x:02x}" for x in row)
            print(f"  0x{va+i:08x}  {hexs}")
    elif cmd == "dw":
        va = int(sys.argv[3], 16); n = int(sys.argv[4])
        for i in range(n):
            v = md.dw(va + i*4)
            print(f"  0x{va+i*4:08x}: {('0x%08x'%v) if v is not None else 'UNMAPPED'}")
    elif cmd == "streams":
        for stype, (dsize, drva) in sorted(md.streams.items()):
            print(f"  stream type {stype:3d}  size {dsize:#x}  rva {drva:#x}")
        print(f"  mem ranges: {len(md.mem)}")
        for start, size, _ in md.mem[:40]:
            print(f"    0x{start:08x} +0x{size:x}")
        if len(md.mem) > 40:
            print(f"    ... {len(md.mem)-40} more")

if __name__ == "__main__":
    main()
