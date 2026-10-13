import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Offline static-RE harness for DINO.exe (Classic REbirth, PSX low-level recompile).

Analysis-only; NOT part of the randomizer build. See docs/STATIC-SCD-RE.md.
Image is byte-identical to the loaded process (no ASLR rebase), imagebase 0x400000,
so VA == file content. We hunt the SCD per-opcode instruction-size table.
"""
import sys
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

import os as _os
EXE = game_path("DC1", 'english/DINO.exe')
if not _os.path.exists(EXE):
    EXE = game_path("DC1", 'english/DINO.exe')


class Dino:
    def __init__(self, path=None):
        path = path or game_path("DC1", "english/DINO.exe")
        self.pe = pefile.PE(path, fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase  # 0x400000
        self.data = self.pe.__data__  # whole file bytes
        self.secs = []
        for s in self.pe.sections:
            name = s.Name.rstrip(b"\x00").decode("latin1")
            self.secs.append((name, self.base + s.VirtualAddress,
                              s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData))
        self.md = Cs(CS_ARCH_X86, CS_MODE_32)
        self.md.detail = False

    def sec_of(self, va):
        for name, vbase, vsize, praw, rsize in self.secs:
            if vbase <= va < vbase + max(vsize, rsize):
                return (name, vbase, vsize, praw, rsize)
        return None

    def va_to_off(self, va):
        s = self.sec_of(va)
        if not s:
            return None
        name, vbase, vsize, praw, rsize = s
        off = praw + (va - vbase)
        return off if off < len(self.data) else None

    def read(self, va, n):
        off = self.va_to_off(va)
        if off is None:
            return None
        return self.data[off:off + n]

    def disasm(self, va, n_bytes=64, count=0):
        code = self.read(va, n_bytes)
        if code is None:
            return []
        out = []
        for ins in self.md.disasm(code, va):
            out.append(ins)
            if count and len(out) >= count:
                break
        return out

    def text_ranges(self):
        for name, vbase, vsize, praw, rsize in self.secs:
            if name in (".text", "CODE") or (vsize and praw and name.startswith(".text")):
                yield (vbase, praw, rsize)


def find_bytes(d, pattern, sec_filter=None):
    """Yield VAs where raw byte `pattern` (bytes) occurs. sec_filter: section name substr."""
    res = []
    for name, vbase, vsize, praw, rsize in d.secs:
        if sec_filter and sec_filter not in name:
            continue
        blob = d.data[praw:praw + rsize]
        i = blob.find(pattern)
        while i != -1:
            res.append((name, vbase + i))
            i = blob.find(pattern, i + 1)
    return res


if __name__ == "__main__":
    d = Dino()
    print("imagebase", hex(d.base))
    for s in d.secs:
        print(f"  {s[0]:10} va={hex(s[1])} vsize={hex(s[2])} raw={hex(s[3])} rsize={hex(s[4])}")
