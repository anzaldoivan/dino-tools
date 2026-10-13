import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Phase-1 raptor HP tier scaler hunt (RAPTOR-TIER-RE).
Scans Dino2.exe .text (and .psxseg) for:
  A) 16-bit writes to [reg+0x108]/[reg+0x10A] that are NOT immediate ctor inits
     (the runtime scaler must write HP from a register).
  B) reads of [reg+0x10C] (variant/tier-base candidate field).
  C) tier constants 818/844/798/1626/3252/1000 as immediates anywhere in code.
Read-only. capstone for precise decode around raw displacement hits."""
import sys, struct
from exe_xref import PE
import capstone

def scan(exe, pe, lo, hi, disp, want_reads=False, want_writes=False):
    """Find instructions in [lo,hi) file range touching [reg+disp]."""
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = True
    needle = struct.pack("<I", disp)
    out = []
    i = lo
    while True:
        j = exe.find(needle, i, hi)
        if j < 0: break
        i = j + 1
        # decode instructions starting up to 8 bytes before the disp bytes
        for back in range(1, 9):
            st = j - back
            if st < lo: break
            ins = next(md.disasm(exe[st:st+16], pe.fo2va(st)), None)
            if ins is None: continue
            if not (st + 1 <= j and st + ins.size >= j + 4): continue
            if f"0x{disp:x}" not in ins.op_str: continue
            # classify mem operand access
            acc_r = acc_w = False
            for op in ins.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.disp == disp:
                    acc_r = bool(op.access & capstone.CS_AC_READ)
                    acc_w = bool(op.access & capstone.CS_AC_WRITE)
            if (want_reads and acc_r) or (want_writes and acc_w):
                out.append((ins.address, ins.mnemonic + " " + ins.op_str, acc_r, acc_w, st))
            break
    # dedupe by address
    seen = set(); res = []
    for t in out:
        if t[0] in seen: continue
        seen.add(t[0]); res.append(t)
    return res

def const_scan(exe, pe, lo, hi, vals):
    hits = []
    for v in vals:
        for pat, kind in [(struct.pack("<I", v), "imm32"), (struct.pack("<H", v), "imm16")]:
            i = lo
            while True:
                j = exe.find(pat, i, hi)
                if j < 0: break
                i = j + 1
                if kind == "imm16":
                    # only report imm16 hits with a plausible mov-word prefix nearby
                    pre = exe[j-9:j]
                    if b"\x66" not in pre: continue
                hits.append((v, kind, j, pe.fo2va(j)))
    return hits

if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else game_path("DC2", 'rebirth/Dino2.exe')
    exe = open(path, "rb").read()
    pe = PE(exe)
    ranges = {}
    for name, va, vsz, rptr, rsz in pe.secs:
        ranges[name] = (rptr, rptr + rsz)
    scans = [(".text", ranges[".text"])]
    if ".psxseg" in ranges: scans.append((".psxseg", ranges[".psxseg"]))

    for sec, (lo, hi) in scans:
        print(f"\n########## section {sec} ##########")
        for disp, label in [(0x108, "HP cur"), (0x10A, "HP max"), (0x10C, "variant/tier base")]:
            w = scan(exe, pe, lo, hi, disp, want_writes=True)
            r = scan(exe, pe, lo, hi, disp, want_reads=True)
            print(f"\n=== [reg+0x{disp:X}] ({label}) — {len(w)} writes / {len(r)} reads ===")
            for addr, txt, ar, aw, fo in w:
                imm = "imm" in txt or ", 0x" in txt or txt.rstrip("0123456789").endswith(", ")
                tag = "IMM" if "0x" == txt.split(", ")[-1][:2] else "REG/RMW"
                print(f"  W 0x{addr:08x}  {txt}   [{tag}]")
            for addr, txt, ar, aw, fo in r:
                if aw: continue
                print(f"  R 0x{addr:08x}  {txt}")

    print("\n=== tier constants in code sections ===")
    for sec, (lo, hi) in scans:
        for v, kind, fo, va in const_scan(exe, pe, lo, hi, [818, 844, 798, 1626, 3252, 409, 1000]):
            print(f"  {sec} {kind} {v} @ VA 0x{va:08x}  ctx={exe[fo-8:fo+8].hex()}")
