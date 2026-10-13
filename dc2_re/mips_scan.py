import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python
"""Aligned manual MIPS hi/lo materialization scanner for the PSX EXE.

Linear capstone disasm desyncs on embedded data; this decodes every 4-byte
aligned word directly (no desync) and finds lui+{addiu,ori,lw,sw,...} pairs
that build an address inside [lo,hi). Tracks the last lui per register.
"""
import sys, struct

TADDR=0x80018000; HDR=0x800
REG=['zero','at','v0','v1','a0','a1','a2','a3','t0','t1','t2','t3','t4','t5',
     't6','t7','s0','s1','s2','s3','s4','s5','s6','s7','t8','t9','k0','k1',
     'gp','sp','fp','ra']

def decode(path, lo, hi):
    raw=open(path,'rb').read()
    text=raw[HDR:HDR+0x90000]
    n=len(text)//4
    pend={}   # reg -> (addr, imm16)
    out=[]
    for i in range(n):
        w=struct.unpack_from('<I', text, i*4)[0]
        addr=TADDR+i*4
        op=w>>26
        rs=(w>>21)&31; rt=(w>>16)&31; imm=w&0xffff
        if op==0x0F:        # lui rt, imm
            pend[rt]=(addr,imm)
        elif op==0x09:      # addiu rt, rs, imm
            if rs in pend:
                h,_=0,0; a,himm=pend[rs]
                sx=imm-0x10000 if imm&0x8000 else imm
                val=((himm<<16)+sx)&0xffffffff
                if lo<=val<hi: out.append((addr,'addiu',val,REG[rt],a))
            if rt!=rs: pend.pop(rt,None)
        elif op==0x0D:      # ori rt, rs, imm
            if rs in pend:
                a,himm=pend[rs]
                val=((himm<<16)|imm)&0xffffffff
                if lo<=val<hi: out.append((addr,'ori',val,REG[rt],a))
            if rt!=rs: pend.pop(rt,None)
        elif op in (0x20,0x21,0x23,0x24,0x25,0x28,0x29,0x2B):  # load/store base+off
            if rs in pend:
                a,himm=pend[rs]
                sx=imm-0x10000 if imm&0x8000 else imm
                val=((himm<<16)+sx)&0xffffffff
                if lo<=val<hi:
                    mn={0x20:'lb',0x21:'lh',0x23:'lw',0x24:'lbu',0x25:'lhu',
                        0x28:'sb',0x29:'sh',0x2B:'sw'}[op]
                    out.append((addr,mn+'/mem',val,REG[rt],a))
            # base reg unchanged; loads into rt clobber rt
            if op in (0x20,0x21,0x23,0x24,0x25): pend.pop(rt,None)
        elif op==0:         # special: addu/addiu via add etc clobbers rd
            funct=w&0x3f; rd=(w>>11)&31
            if funct in (0x21,0x20,0x23,0x25,0x24): pend.pop(rd,None)
    return out

if __name__=='__main__':
    path=sys.argv[1]; lo=int(sys.argv[2],16); hi=int(sys.argv[3],16)
    for addr,mn,val,reg,luiaddr in decode(path,lo,hi):
        print('0x%08x  %-8s -> 0x%08x  $%s  (lui@0x%08x)'%(addr,mn,val,reg,luiaddr))

def jal_to(path, target):
    """Aligned scan: RAM addrs of `jal target` (and `j target`)."""
    raw=open(path,'rb').read(); text=raw[HDR:HDR+0x90000]
    out=[]
    for i in range(len(text)//4):
        w=struct.unpack_from('<I',text,i*4)[0]
        op=w>>26
        if op in (2,3):  # j / jal
            tgt=((TADDR & 0xf0000000) | ((w&0x3ffffff)<<2))
            if tgt==target:
                out.append((TADDR+i*4, 'jal' if op==3 else 'j'))
    return out
