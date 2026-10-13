import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Capstone-based x86 disassembler for Dino2.exe. Read-only.
Disassembles a function (linear until ret/int3 run), resolves call/jmp targets and
RIP-less absolute [disp] operands, and can dump indirect-call/jump-table dispatch."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from exe_xref import PE
from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_OP_MEM, CS_OP_IMM

def load(path):
    exe=open(path,"rb").read(); return exe, PE(exe)

def disasm_func(exe, pe, va, max_insns=400, stop_on_ret=True):
    md=Cs(CS_ARCH_X86, CS_MODE_32); md.detail=True
    fo=pe.va2fo(va)
    code=exe[fo:fo+max_insns*8]
    out=[]
    for ins in md.disasm(code, va):
        out.append(ins)
        if stop_on_ret and ins.mnemonic in ("ret","retn") :
            break
        if len(out)>=max_insns: break
    return out

def fmt(ins, pe, exe):
    note=""
    # call/jmp target annotate
    if ins.mnemonic in ("call","jmp") and ins.operands and ins.operands[0].type==CS_OP_IMM:
        t=ins.operands[0].imm; note=f"   ; -> 0x{t:08x}"
    # indirect via memory: [disp] or [reg*scale+disp]
    for op in ins.operands:
        if op.type==CS_OP_MEM:
            disp=op.mem.disp
            if op.mem.base==0 and op.mem.index==0 and disp>0x400000:
                note=f"   ; [abs 0x{disp:08x} in {pe.sec_of(disp)}]"
            elif op.mem.index!=0 and disp>0x400000:
                note=f"   ; TABLE base 0x{disp:08x} ({pe.sec_of(disp)}) scaled"
    return f"  0x{ins.address:08x}: {ins.mnemonic:7} {ins.op_str}{note}"

if __name__=="__main__":
    path=sys.argv[1]; exe,pe=load(path)
    for a in sys.argv[2:]:
        va=int(a,16)
        print(f"\n===== func @ 0x{va:08x} =====")
        for ins in disasm_func(exe,pe,va):
            print(fmt(ins,pe,exe))
