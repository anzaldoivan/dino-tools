import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python
"""Static MIPS analysis helper for the PSX DC2 boot EXE (SLUS_012.79).

PS-X EXE: 0x800-byte header, then text loaded flat at t_addr.
  ram = t_addr + (file_off - 0x800)   ;   file_off = ram - t_addr + 0x800

Provides addr<->offset mapping, find_le32, a capstone MIPS32-LE disassembler,
hi/lo address-materialization scanning (lui+addiu/ori), and jal-target search.

CLI:
  python psx_exe.py <exe> info
  python psx_exe.py <exe> dis <ram_hex> [n]
  python psx_exe.py <exe> le32 <value_hex>          # ram addrs whose word == value
  python psx_exe.py <exe> ptrto <addr_hex>          # lui/addiu(+ori) sites building addr
  python psx_exe.py <exe> callers <func_ram_hex>    # jal sites targeting func
"""
import sys, struct
try:
    from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN
except ImportError:
    Cs = None

class PsxExe:
    def __init__(self, path):
        self.raw = open(path, 'rb').read()
        assert self.raw[:8] == b'PS-X EXE', 'not a PS-X EXE'
        self.pc0     = struct.unpack_from('<I', self.raw, 0x10)[0]
        self.taddr   = struct.unpack_from('<I', self.raw, 0x18)[0]
        self.tsize   = struct.unpack_from('<I', self.raw, 0x1c)[0]
        self.hdr     = 0x800
        self.text    = self.raw[self.hdr:self.hdr+self.tsize]
        self.lo      = self.taddr
        self.hi      = self.taddr + self.tsize
        if Cs:
            self.md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 | CS_MODE_LITTLE_ENDIAN)
            self.md.detail = True

    # ---- address mapping ----
    def in_range(self, ram): return self.lo <= ram < self.hi
    def fo(self, ram):       return ram - self.taddr + self.hdr
    def ram(self, fo):       return fo - self.hdr + self.taddr
    def word(self, ram):     return struct.unpack_from('<I', self.raw, self.fo(ram))[0]
    def bytes_at(self, ram, n):
        o = self.fo(ram); return self.raw[o:o+n]

    # ---- search ----
    def find_le32(self, value):
        """All RAM addresses in text whose 4-byte LE word == value."""
        tgt = struct.pack('<I', value); out = []; i = self.hdr; end = self.hdr+self.tsize
        while True:
            j = self.raw.find(tgt, i, end)
            if j < 0: break
            out.append(self.ram(j)); i = j + 1
        return out

    def dis(self, ram, n=40):
        o = self.fo(ram)
        return list(self.md.disasm(self.raw[o:o+n*4], ram))

    # ---- MIPS helpers ----
    def jal_callers(self, func):
        """RAM addrs of `jal func` instructions."""
        out = []
        for ins in self.md.disasm(self.text, self.taddr):
            if ins.mnemonic == 'jal':
                tgt = int(ins.op_str, 16) if ins.op_str.startswith('0x') else None
                if tgt == func:
                    out.append(ins.address)
        return out

    def ptr_sites(self, addr):
        """Find lui/(addiu|ori) pairs that materialize `addr` (per register).
        Returns list of (lui_ram, second_ram, reg, kind)."""
        hi = (addr >> 16) & 0xffff
        lo = addr & 0xffff
        # for addiu (sign-extended) the lui immediate is adjusted when lo bit15 set
        hi_addiu = (hi + 1) & 0xffff if (lo & 0x8000) else hi
        pend = {}   # reg -> (lui_ram, imm16)
        out = []
        for ins in self.md.disasm(self.text, self.taddr):
            m, ops = ins.mnemonic, ins.op_str
            if m == 'lui':
                try:
                    reg, imm = [x.strip() for x in ops.split(',')]
                    pend[reg] = (ins.address, int(imm, 16) & 0xffff)
                except Exception:
                    pass
            elif m in ('addiu', 'ori', 'addi'):
                parts = [x.strip() for x in ops.split(',')]
                if len(parts) == 3:
                    rd, rs, imm = parts
                    if rs in pend:
                        lui_ram, himm = pend[rs]
                        try: ival = int(imm, 16) & 0xffff
                        except Exception: ival = None
                        if ival is not None:
                            if m in ('addiu','addi') and himm == hi_addiu and ival == lo:
                                out.append((lui_ram, ins.address, rd, m))
                            elif m == 'ori' and himm == hi and ival == lo:
                                out.append((lui_ram, ins.address, rd, m))
                    # rd overwritten -> drop pending for rd
                    if rd != rs: pend.pop(rd, None)
            elif m in ('lw','sw','lb','lbu','lh','lhu','sb','sh'):
                # base+offset form referencing a lui'd register: addr = (himm<<16)+soff
                parts = ops.split(',')
                if len(parts) == 2 and '(' in parts[1]:
                    off_s, base = parts[1].split('(')
                    base = base.strip(') ')
                    if base in pend:
                        lui_ram, himm = pend[base]
                        try: off = int(off_s.strip(), 16)
                        except Exception: off = 0
                        if off & 0x8000: off -= 0x10000
                        if ((himm << 16) + off) & 0xffffffff == addr:
                            out.append((lui_ram, ins.address, base, m))
        return out

def main():
    e = PsxExe(sys.argv[1]); cmd = sys.argv[2]
    if cmd == 'info':
        print('pc0=0x%08x taddr=0x%08x tsize=0x%x range=0x%08x..0x%08x'
              % (e.pc0, e.taddr, e.tsize, e.lo, e.hi))
    elif cmd == 'dis':
        ram = int(sys.argv[3], 16); n = int(sys.argv[4]) if len(sys.argv) > 4 else 40
        for ins in e.dis(ram, n):
            print('0x%08x  %-10s %s' % (ins.address, ins.mnemonic, ins.op_str))
    elif cmd == 'le32':
        for r in e.find_le32(int(sys.argv[3], 16)):
            print('0x%08x' % r)
    elif cmd == 'ptrto':
        for lui_ram, s_ram, reg, kind in e.ptr_sites(int(sys.argv[3], 16)):
            print('lui@0x%08x %s@0x%08x reg=%s kind=%s' % (lui_ram, kind, s_ram, reg, kind))
    elif cmd == 'callers':
        for c in e.jal_callers(int(sys.argv[3], 16)):
            print('0x%08x' % c)

if __name__ == '__main__':
    main()
