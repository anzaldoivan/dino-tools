import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Read-only MIPS32-LE disassembler driver for DC1 PSX stage overlays (english/Data/st*.bin).

Overlay loads at a fixed PSX RAM base; psx_va = base + file_offset.
Resolves jal/j targets, reconstructs lui+ori/addiu constants (li-style immediates & 0x80xx_xxxx ptrs),
and can scan for callers of a target VA. NO writes anywhere. Usage examples at bottom.
"""
import sys, os, struct
from capstone import (Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN,
                      CS_OP_IMM, CS_OP_REG, CS_OP_MEM)

# file -> PSX load base. NOTE (2026-06-29): DC1 overlay loads are DATA-DRIVEN — the
# canonical overlays are /PSX/DATA/ST*.DAT entry0 (type-7 LZSS) whose 16-byte resource-header
# record carries loadaddr@+8 (ST1.DAT entry0 -> 0x800e8000, the keypad/common-object overlay).
# The /PSX/BIN/ST*.BIN here are uncompressed sibling builds (-0x38 resident skew). Verified:
# st1.bin (room glue) = 0x80130000; st2..st9.bin best-fit 0x800f0000 (per-stage object overlays).
# Full record: scratch/psx/dc1/README.md, docs/dc1/PSX-MIPS-ROUTE.md §9.
BASES = {
    'st1.bin': 0x80130000,
    'st2.bin': 0x800f0000, 'st3.bin': 0x800f0000, 'st4.bin': 0x800f0000,
    'st5.bin': 0x800f0000, 'st6.bin': 0x800f0000, 'st7.bin': 0x800f0000,
    'st8.bin': 0x800f0000, 'st9.bin': 0x800f0000,
    'title.bin': 0x80140000, 'ending.dat': 0x80140000,
}

def load(name):
    path = name if os.path.sep in name or os.path.exists(name) else os.path.join('english','Data',name)
    d = open(path,'rb').read()
    base = BASES.get(os.path.basename(path), None)
    return d, base

def md():
    m = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 + CS_MODE_LITTLE_ENDIAN); m.detail = True
    return m

def w32(d,off): return struct.unpack('<I', d[off:off+4])[0]

def jal_target(word):    # returns absolute (within 256MB seg) for op j(2)/jal(3)
    op = word >> 26
    if op in (2,3): return ((word & 0x03ffffff) << 2)
    return None

def disasm(d, base, off, n=80, track_const=True):
    """Linear disasm n insns from file offset; annotate jal/j and reconstructed constants."""
    m = md()
    code = d[off:off+n*4+8]
    regs = {}  # reg name -> reconstructed value after lui/ori/addiu (rough)
    out = []
    for ins in m.disasm(code, base+off):
        note = ""
        mn, ops = ins.mnemonic, ins.op_str
        word = struct.unpack('<I', ins.bytes)[0]
        t = jal_target(word)
        if t is not None:
            seg = (base & 0xf0000000) | t
            note = f"   ; -> 0x{seg:08x}"
        if track_const:
            o = ins.operands
            if mn == 'lui' and len(o)==2 and o[1].type==CS_OP_IMM:
                regs[ins.reg_name(o[0].reg)] = (o[1].imm & 0xffff) << 16
            elif mn in ('ori','addiu','addi') and len(o)==3 and o[2].type==CS_OP_IMM:
                rs = ins.reg_name(o[1].reg)
                if rs in regs:
                    imm = o[2].imm
                    if mn=='ori': v = regs[rs] | (imm & 0xffff)
                    else: v = (regs[rs] + imm) & 0xffffffff
                    rd = ins.reg_name(o[0].reg); regs[rd]=v
                    note += f"   ; {rd}=0x{v:08x}"
        out.append((ins.address, ins.bytes.hex(), mn, ops, note))
    return out

def pr(rows):
    for addr,hx,mn,ops,note in rows:
        print(f"  0x{addr:08x}: {hx:8} {mn:8} {ops}{note}")

def find_callers(d, base, target_va):
    """Scan for jal/j whose resolved target == target_va. Returns list of (off, psx_va)."""
    hits=[]
    for off in range(0, len(d)-4, 4):
        word = w32(d,off)
        t = jal_target(word)
        if t is None: continue
        seg = (base & 0xf0000000) | t
        if seg == target_va: hits.append((off, base+off))
    return hits

def find_imm_refs(d, base, value):
    """Find lui/ori or addiu pairs that build `value`, and direct 16-bit imm matches. Coarse."""
    hits=[]
    hi = (value>>16)&0xffff; lo = value&0xffff
    for off in range(0, len(d)-4, 4):
        word=w32(d,off); op=word>>26
        imm = word & 0xffff
        # addiu/ori/li small immediates
        if op in (8,9,0xd,0xc) and imm==(value&0xffff) and value<0x10000:
            hits.append((off, base+off, 'imm16'))
    return hits

def header(d, base, k=16):
    print(f"-- header: count word=0x{w32(d,0):08x} ; leading words --")
    for i in range(k):
        w=w32(d,i*4)
        tag = " <ptr>" if 0x80000000<=w<0x80200000 else ""
        fo = (w-base) if (base and 0x80000000<=w<0x80200000) else None
        ft = f"  fileoff=0x{fo:x}" if fo is not None and 0<=fo<len(d) else ""
        print(f"   [{i:2}] +0x{i*4:03x} = 0x{w:08x}{tag}{ft}")

if __name__=='__main__':
    cmd = sys.argv[1] if len(sys.argv)>1 else 'header'
    name = sys.argv[2] if len(sys.argv)>2 else 'st1.bin'
    d, base = load(name)
    if base is None:
        # allow explicit base as 4th arg
        base = int(sys.argv[4],0) if len(sys.argv)>4 else 0x80130000
    if cmd=='header':
        header(d, base, int(sys.argv[3]) if len(sys.argv)>3 else 16)
    elif cmd=='dis':
        off = int(sys.argv[3],0)
        if off>=base: off-=base
        pr(disasm(d, base, off, int(sys.argv[4]) if len(sys.argv)>4 else 80))
    elif cmd=='callers':
        va=int(sys.argv[3],0)
        for o,p in find_callers(d,base,va): print(f"  caller @ 0x{p:08x} (off 0x{o:x})")
    elif cmd=='imm':
        v=int(sys.argv[3],0)
        for o,p,k in find_imm_refs(d,base,v): print(f"  {k} @ 0x{p:08x} (off 0x{o:x})")
