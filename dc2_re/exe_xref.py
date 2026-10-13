import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Static xref scanner for Dino2.exe — find code referencing the E*.DAT filename
table, the E-string VAs, and the enemy-model RAM load bases. Read-only PE scan,
no full disassembly. Goal: locate the enemy load/spawn routine."""
import sys, os, struct, collections

class PE:
    def __init__(self, exe):
        self.exe = exe
        pe = struct.unpack_from("<I", exe, 0x3c)[0]
        assert exe[pe:pe+4] == b"PE\0\0"
        nsec = struct.unpack_from("<H", exe, pe+6)[0]
        opt = pe+24
        self.imagebase = struct.unpack_from("<I", exe, opt+28)[0]
        so = opt + struct.unpack_from("<H", exe, pe+20)[0]
        self.secs=[]
        for i in range(nsec):
            o=so+i*40
            name=exe[o:o+8].rstrip(b"\0").decode("latin1")
            vsz,va,rsz,rptr=struct.unpack_from("<IIII",exe,o+8)
            self.secs.append((name,va,vsz,rptr,rsz))
    def va2fo(self,v):
        for name,va,vsz,rptr,rsz in self.secs:
            b=self.imagebase+va
            if b<=v<b+max(vsz,rsz) and rsz and v-b<rsz: return rptr+(v-b)
        return None
    def fo2va(self,fo):
        for name,va,vsz,rptr,rsz in self.secs:
            if rsz and rptr<=fo<rptr+rsz: return self.imagebase+va+(fo-rptr)
        return None
    def sec_of(self,v):
        for name,va,vsz,rptr,rsz in self.secs:
            b=self.imagebase+va
            if b<=v<b+max(vsz,rsz): return name
        return "?"
    def text_range(self):
        for name,va,vsz,rptr,rsz in self.secs:
            if name==".text": return rptr, rptr+rsz
        return 0,0

def find_le32(exe, value, lo=0, hi=None):
    """All file offsets where the 4-byte LE of value appears."""
    if hi is None: hi=len(exe)
    target=struct.pack("<I",value)
    out=[]; i=lo
    while True:
        j=exe.find(target,i,hi)
        if j<0: break
        out.append(j); i=j+1
    return out

def ctx(exe,fo,pe,back=8,fwd=12):
    s=exe[fo-back:fo+fwd]
    return s.hex()

if __name__=="__main__":
    path=sys.argv[1]
    exe=open(path,"rb").read()
    pe=PE(exe)
    tlo,thi=pe.text_range()

    # 1. Map the full enemy filename table around 0x71b3bc: read .data words, resolve to E-strings
    print("=== enemy filename table region (VA 0x71b390..0x71b410) ===")
    estr_lo,estr_hi=0x71d150,0x71d560
    base_fo=pe.va2fo(0x71b390)
    tbl_entries=[]
    for k in range(0x80//4):
        va=0x71b390+k*4
        fo=pe.va2fo(va)
        w=struct.unpack_from("<I",exe,fo)[0]
        tag=""
        if estr_lo<=w<=estr_hi:
            sfo=pe.va2fo(w); s=exe[sfo:sfo+8].split(b'\0')[0].decode('latin1','replace')
            tag=f" -> '{s}'"; tbl_entries.append((va,w,s))
        print(f"  [0x{va:08x}] = 0x{w:08x}{tag}")

    # 2. xref the filename TABLE itself (code that indexes it): search for 0x71b3xx displacements
    print("\n=== .text refs to the filename table base(s) ===")
    for tva in [0x71b390,0x71b3a0,0x71b3b0,0x71b3bc,0x71b3c0,0x71b3d0,0x71b3dc,0x71b3e0]:
        hits=find_le32(exe,tva,tlo,thi)
        if hits:
            for h in hits:
                print(f"  table VA 0x{tva:08x} referenced @ .text foff=0x{h:x} (VA 0x{pe.fo2va(h):08x})  ctx={ctx(exe,h,pe)}")

    # 3. xref the enemy model RAM load bases (loader writes E-file LZSS0 here)
    print("\n=== .text refs to enemy-model RAM bases ===")
    for base in [0x633000,0x638000,0x63d000,0x63f500,0x640000,0x650000,0x6c1500]:
        hits=find_le32(exe,base,tlo,thi)
        for h in hits[:6]:
            print(f"  base 0x{base:08x} @ .text foff=0x{h:x} (VA 0x{pe.fo2va(h):08x})  ctx={ctx(exe,h,pe)}")

    # 4. xref individual E-string VAs (direct push of a filename)
    print("\n=== .text refs to individual E*.DAT string VAs (sample) ===")
    for sva in [0x71d158,0x71d1e0,0x71d1c0,0x71d55b]:  # EA0,E20,E40,E00
        hits=find_le32(exe,sva,tlo,thi)
        for h in hits[:4]:
            print(f"  Estr VA 0x{sva:08x} @ .text foff=0x{h:x} (VA 0x{pe.fo2va(h):08x})  ctx={ctx(exe,h,pe)}")
