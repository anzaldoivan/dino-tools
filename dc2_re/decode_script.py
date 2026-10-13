import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Walk the slot-5 SCD-VM program of a DC2 room and list opcodes, highlighting
enemy spawns (op 0x1a) and the op-0x05 push literals that feed each spawn block.

Uses the statically-derived opcode length table (opcode_lengths.py) and the
spawn-block field map (K47). Read-only. Usage: python decode_script.py [ST102.DAT ...]
"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dc2pkg import parse, GPC_LZSS0
try:
    from type_species import get as _typemap
    TYPEMAP = _typemap()
except Exception:
    TYPEMAP = {}

DATA = game_path("DC2", 'rebirth/Data')
BASE = 0x005e0000

# opcode byte-length table derived from handlers (opcode_lengths.py).
# 0x04=RETURN, 0x19=jump -> set IP absolutely (no fixed length).
LEN = {0x01:6,0x02:6,0x03:6,0x05:4,0x06:4,0x07:4,0x08:4}
for op in range(0x10,0x5a):
    LEN.setdefault(op,2)
RETURN, JUMP = 0x04, 0x19
PUSH = 0x05
SPAWN = 0x1a
NOP_OPS = {0x00,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,0x34,0x3c,0x3d,0x3f,0x5a,0x5b,0x5c,0x5d,0x5e,0x5f}

MODES = {0:"lit",1:"var",2:"blk",6:"glb"}
# block offset -> meaning (K47); offset = (#slots from operand-stack top)*4
BLOCKMAP = {0x00:"GATE",0x04:"?",0x08:"HP/ptr",0x0c:"MODEL_BASE",0x10:"rot?",
            0x14:"posZ",0x18:"posY",0x1c:"posX",0x20:"TYPE",0x24:"SLOT"}

# --- AOT (op 0x30/0x31/0x32/0x33) commit opcodes -------------------------------
# Each *-COMMIT reads a push-built block ([entity+0x84]) and copies it into the
# 0x28-byte AOT record (0x752388 + slot*0x28). block offset = (#slots from stack
# top)*4 (push k=0 -> last push -> block+0). Maps below are the block offsets that
# carry the meaningful field, per the builder disassembly (disx, 2026-06-27):
#   op 0x30 DOOR (builder 0x489750): rec+0x1c(dest)=block+0xc ; slot=block+0x48
#   op 0x31 SAT-1 VM trigger (builder 0x489880): rec+0x14(thread)=block+0x04,
#     rec+0x18(routine)=block+0x00; slot=block+0x30. Neither field is a catalog id/count.
AOT_OPS = {0x30:"DOOR", 0x31:"SAT1", 0x32:"STATE", 0x33:"FLAG"}
# fields to surface per AOT opcode: list of (block_off, label)
AOT_FIELDS = {
    0x30: [(0x0c,"dest"), (0x48,"slot")],
    0x31: [(0x00,"routine"), (0x04,"thread"), (0x30,"slot")],
    0x32: [(0x04,"key"), (0x30,"slot")],            # SAT-2 STATE: rec+0x14=block+0x04 (state key); slot=block+0x30
    0x33: [(0x00,"value"), (0x04,"id"), (0x08,"group"), (0x34,"slot")],  # SAT-3 FLAG: 0x46f9f0(group,id,value)
}

def room_blob(name):
    d = open(os.path.join(DATA, name), "rb").read()
    ents,_ = parse(d)
    lz = [e for e in ents if e.type==GPC_LZSS0]
    return lz[0].decomp if lz else None

def script_section(d):
    """slot-5 = directory entry [5] (u32 @ blob+0x14). Returns (start, size)."""
    n=len(d)
    v=struct.unpack_from("<I",d,0x14)[0]
    if not (BASE<=v<BASE+n): return None
    start=v-BASE
    # section end = next-higher directory pointer, else blob end
    ptrs=[struct.unpack_from("<I",d,i)[0]-BASE for i in range(0,0x80,4)
          if BASE<=struct.unpack_from("<I",d,i)[0]<BASE+n]
    higher=[p for p in ptrs if p>start]
    end=min(higher) if higher else n
    return start,end-start

def routine_directory(d, start, size):
    """Return routine starts in true VM directory-index order."""
    opbase=start+0x1c
    offs=[]; i=0; bound=size
    while i*4 < bound:
        v=struct.unpack_from("<I",d,opbase+i*4)[0]
        if not (0 <= v < size): break
        offs.append(v)
        bound=min(bound,v)
        i+=1
    return [opbase+off for off in offs]


def subprograms(d, start, size):
    """Routine offset DIRECTORY at opbase=start+0x1c: entry i is the u32 at opbase+i*4
    (offset relative to opbase; entry 0 = the init/entry routine, where almost all
    door/item AOT commits live). The directory is SELF-BOUNDING: the engine indexes it
    blindly (0x48bf20: IP := a54 + [a54 + param*4]) with no count field, so the only
    structural bound is that entries occupy bytes BELOW the lowest routine offset seen
    so far. Read entries while i*4 < min(offsets so far); stop at the first value that
    is not a plausible in-section offset. Entries need NOT be ascending (ST002's init
    0x48 precedes array head 0x28 in table order). Fixes two 2026-07-10 bugs vs the
    prior init+ascending-array heuristic: (a) a hard 20-entry cap truncated big rooms
    (ST003/205/402/503/607 have up to 30 routines; the dropped tail routines held 2
    real doors + 3 items + ~215 flag ops), and (b) single-entry directories (init
    offset 4, script starts at +0x20: ST103) grew a PHANTOM routine from the first
    opcode word, truncating the real routine to 1 op and hiding ST103's two doors
    (-> ST104/ST105) from the door graph. Corpus INVALID stays 5 under the fix.
    Returns absolute opcode starts, sorted ascending by offset (so routines() can
    bound each routine by the next-higher start)."""
    return sorted(set(routine_directory(d,start,size)))


def _routine_identity(d,start,size,routine_ordinal,routine_start):
    directory=routine_directory(d,start,size)
    vm_indices=[i for i,value in enumerate(directory) if value == routine_start]
    vm_index=vm_indices[0] if len(vm_indices) == 1 else None
    return {"routine_ordinal":routine_ordinal,
            "vm_directory_index":vm_index,
            "vm_directory_indices":vm_indices,
            "routine_start":routine_start}

def disasm(d, start, end):
    """Linear walk [start,end) by opcode length; stop at RETURN. Returns list of
    (off, opcode, raw_bytes)."""
    out=[]; ip=start
    while ip < end:
        op=d[ip]
        if op==RETURN:
            out.append((ip,op,d[ip:ip+2])); break
        if op==JUMP:
            out.append((ip,op,d[ip:ip+4])); ip+=4; continue  # treat as 4 for listing
        L=LEN.get(op)
        if L is None:
            out.append((ip,op,d[ip:ip+1])); break
        out.append((ip,op,d[ip:ip+L])); ip+=L
    return out

def routines(d, start, size):
    """Return [(routine_start, routine_end)] absolute offsets.
    subprograms() returns the routine starts (init offset at +0x1c merged with the
    ascending array at +0x20), sorted ascending; each routine is bounded by the
    next-higher start (or section end). ST104 is a 1-routine room (init only). The
    single-fallback (walk +0x20 to first RETURN) is kept only for rooms with no
    parseable offset table."""
    progs = subprograms(d, start, size)
    if progs:
        bounds = progs + [start+size]
        return [(progs[i], bounds[i+1]) for i in range(len(progs))], "table"
    s = start+0x20
    ops = disasm(d, s, start+size)
    end = (ops[-1][0]+len(ops[-1][2])) if ops else start+size
    return [(s, end)], "single"

def block_from_pushrun(ops, idx):
    """Collect the contiguous op-0x05 push run immediately before ops[idx] and map
    it to block offsets (k=0 -> last push -> block+0). Returns {off:(val,mode)}."""
    run=[]; j=idx-1
    while j>=0 and ops[j][1]==PUSH: run.append(ops[j]); j-=1
    run.reverse()
    m={}
    for k,(_,_,pb) in enumerate(reversed(run)):
        m[k*4]=(struct.unpack_from("<h",pb,2)[0], pb[1])
    return m, len(run)

def item_commits(d, start, size):
    """Pure: op-0x31 SAT-1 physical VM triggers in this slot-5 section.

    `thread` (block+0x04) is the VM entity/thread slot and `routine` (block+0x00)
    is the slot-5 routine index. They are deliberately not named id/count: neither is
    a catalog item field or an item rewrite lever. `pos` is the trigger quad's first
    (X,Z) corner: rec+4/rec+6 = block+0x24/block+0x20
    (builder 0x489880 maps rec+4..rec+0x12 = 4 signed (X,Z) corner words; corpus 2026-07-12:
    all commits carry them as mode-0 literals in 13-push runs, quads geometrically sane).
    None when a corner operand is missing or non-literal."""
    out=[]
    routs,_=routines(d,start,size)
    for routine_ordinal,(s,e) in enumerate(routs):
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op!=0x31: continue
            run=_pushrun_offsets(ops, idxof[off])
            m,_=block_from_pushrun(ops, idxof[off])
            thread=_operand(run,0x04); routine=_operand(run,0x00); slot=_operand(run,0x30)
            x,xm=m.get(0x24,(None,None)); z,zm=m.get(0x20,(None,None))
            pos=[x,z] if (xm==0 and zm==0) else None
            out.append({**_routine_identity(d,start,size,routine_ordinal,s), "op_off":off,
                        "thread":thread["value"], "routine":routine["value"],
                        "slot":slot["value"], "thread_operand":thread,
                        "routine_operand":routine, "slot_operand":slot, "pos":pos})
    return out

def _pushrun_offsets(ops, idx):
    """The contiguous op-0x05 push run immediately before ops[idx], in program order
    (run[0]=first push). block+N = reversed(run)[N//4] = run[len-1-(N//4)]."""
    run=[]; j=idx-1
    while j>=0 and ops[j][1]==PUSH: run.append(ops[j]); j-=1
    run.reverse()
    return run


def _operand(run, block_off):
    """Return byte-proven metadata for one push-built block operand."""
    index = len(run) - 1 - block_off // 4
    if not (0 <= index < len(run)):
        return {"block_off": block_off, "mode": None, "value": None,
                "push_off": None, "value_off": None}
    push_off, _op, raw = run[index]
    return {"block_off": block_off, "mode": raw[1],
            "value": struct.unpack_from("<h", raw, 2)[0],
            "push_off": push_off, "value_off": push_off + 2}


def take_item_commits(d, start, size):
    """Decode SAT-5/op-0x35 TAKE-ITEM records (builder 0x489B10).

    The catalog-bearing operand is block+0x10. The remaining operands are site
    identity/cleanup fields which a writer must verify but never rewrite.
    """
    out=[]
    routs,_=routines(d,start,size)
    for routine_ordinal,(s,e) in enumerate(routs):
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op != 0x35:
                continue
            run=_pushrun_offsets(ops,idxof[off])
            item=_operand(run,0x10)
            flag5=_operand(run,0x14)
            p3=_operand(run,0x0c)
            secondary=_operand(run,0x04)
            slot=_operand(run,0x40)
            secondary_handle = None
            if secondary["value"] is not None:
                secondary_handle = (secondary["value"] + 0x0c) & 0xff
            out.append({**_routine_identity(d,start,size,routine_ordinal,s), "op_off":off, "kind":1,
                        "item_id":item["value"], "item_operand":item,
                        "flag5":flag5["value"], "flag5_operand":flag5,
                        "p3":p3["value"], "p3_operand":p3,
                        "cleanup_secondary":secondary_handle,
                        "cleanup_secondary_operand":secondary,
                        "slot":slot["value"], "slot_operand":slot})
    return out


def scripted_give_commits(d, start, size):
    """Decode op-0x2C's five-operand GiveItem constructor calls.

    Only kind 1 follows the direct acquisition path. Kinds 2-5 are retained as
    non-grant records so no consumer can silently turn them into item locations.
    """
    out=[]
    routs,_=routines(d,start,size)
    for routine_ordinal,(s,e) in enumerate(routs):
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op != 0x2c:
                continue
            run=_pushrun_offsets(ops,idxof[off])
            kind=_operand(run,0x10)
            item=_operand(run,0x0c)
            p3=_operand(run,0x08)
            flag5=_operand(run,0x04)
            cleanup=_operand(run,0x00)
            kind_value=kind["value"]
            cleanup_handle=None
            if kind_value == 1 and cleanup["value"] is not None:
                cleanup_handle=((cleanup["value"] & 0xff) << 8) | 0xff
            elif cleanup["value"] is not None:
                cleanup_handle=cleanup["value"] & 0xffff
            out.append({**_routine_identity(d,start,size,routine_ordinal,s), "op_off":off,
                        "kind":kind_value, "kind_operand":kind,
                        "item_id":item["value"], "item_operand":item,
                        "p3":p3["value"], "p3_operand":p3,
                        "flag5":flag5["value"], "flag5_operand":flag5,
                        "cleanup_handle":cleanup_handle,
                        "cleanup_operand":cleanup,
                        "direct_give":kind_value == 1})
    return out


def flag_set_commits(d, start, size):
    """Decode direct op-0x1C and deferred SAT-3/op-0x33 SetFlag sites."""
    out=[]
    routs,_=routines(d,start,size)
    for routine_ordinal,(s,e) in enumerate(routs):
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op not in (0x1c,0x33):
                continue
            run=_pushrun_offsets(ops,idxof[off])
            group=_operand(run,0x08)
            flag_id=_operand(run,0x04)
            value=_operand(run,0x00)
            out.append({**_routine_identity(d,start,size,routine_ordinal,s), "op_off":off, "opcode":op,
                        "group":group["value"], "group_operand":group,
                        "flag_id":flag_id["value"], "flag_id_operand":flag_id,
                        "value":value["value"], "value_operand":value})
    return out

def door_commits(d, start, size):
    """Pure: op-0x30 door commits. dest = block+0x0c literal (reversed-run[3]). The dest
    word value IS the destination room's st_id: dest_id = f"{w:03X}" (stage=high byte,
    room=low byte) -- the identity mapping st_id = '%X%02X' % (stage, room) confirmed by
    the live CreateFileA trace in DOOR-ROUTING-RE.md (0x102 -> ST102, hardcoded 0x101 ->
    ST101). This validates 180/181 literal doors corpus-wide (K81); the prior stage=low formula
    ("ST101->ST201") was a byte-order bug and is RETRACTED. dest_off = blob offset of the
    editable 2-byte dest word. slot = block+0x48. Only dest_mode==0 doors are editable."""
    out=[]
    routs,_=routines(d,start,size)
    for s,e in routs:
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op!=0x30: continue
            run=_pushrun_offsets(ops, idxof[off])
            m,_=block_from_pushrun(ops, idxof[off])
            dword,dmode=m.get(0x0c,(None,None))
            slot=m.get(0x48,(None,None))[0]
            destpush=run[len(run)-4] if len(run)>=4 else None  # block+0x0c
            dest_id=None
            if dword is not None:
                w=dword & 0xffff
                dest_id=f"{w:03X}"   # dest word value == destination st_id (DOOR-ROUTING-RE live trace)
            # Door GATE fields (K78, SAT-0 handler 0x48b500): warp iff lock_index==0 OR
            # flag(8, unlock_flag_id) set; else the lock message [0x731ab8 + lock_index*4].
            # op-0x30 builder map: rec+0x21 = block+0x00, rec+0x20 = block+0x04.
            flg, flg_mode = m.get(0x00,(None,None))
            lck, lck_mode = m.get(0x04,(None,None))
            # Door FLAGS + TYPE literals (2026-07-10): builder 0x489750 sets
            # rec+0 = block+0x44 | 9 and rec+0x1e = block+0x08 (the door-type/anim word,
            # live-confirmed 0x0107 on ST101's ST102 door). rec+0 & 0xc semantics (K38):
            # 8 = usable door, 0xc = blocked-at-build; bit 0x80 -> SAT-0 handler writes
            # scene_mgr+0x926=1 (0x48b524-9), which makes the next room-entry step
            # 0x451240 SKIP its 0x48f420 call. All 181 op-0x30 commits carry both as
            # mode-0 literals (corpus, 0 short runs).
            dflags, dflags_mode = m.get(0x44,(None,None))
            dtype, dtype_mode = m.get(0x08,(None,None))
            out.append({"dest_word":dword, "dest_mode":dmode, "dest_id":dest_id,
                        "slot":slot, "dest_off":(destpush[0]+2) if destpush else None,
                        "op_off":off,
                        "lock_index":lck, "lock_mode":lck_mode,
                        "unlock_flag_id":flg, "unlock_flag_mode":flg_mode,
                        "flags":dflags, "flags_mode":dflags_mode,
                        "door_type":dtype, "door_type_mode":dtype_mode})
    return out


def keyuse_commits(d, start, size):
    """Pure: op-0x3a KEY-USE (SAT-10) records (K78 follow-up). Builder 0x48a020 gates on
    flag(8, block+0x08) (skips the build once unlocked); consumer 0x48b790 checks the
    equip-word class byte (scene_mgr+0x10b5: {2,8}->subtype 1, {1,7}->subtype 2), pokes
    actor[block+0x04], then SetFlag(8, block+0x08, 1) = unlocks the paired SAT-0 door.
    Fields: flag8_id=block+0x08 (the door unlock_flag_id it satisfies), subtype=block+0x0c,
    actor_index=block+0x04, slot=block+0x38."""
    out=[]
    routs,_=routines(d,start,size)
    for s,e in routs:
        ops=disasm(d,s,e); idxof={x[0]:t for t,x in enumerate(ops)}
        for off,op,_ in ops:
            if op!=0x3a: continue
            m,_=block_from_pushrun(ops, idxof[off])
            g=lambda o: m.get(o,(None,None))[0]
            out.append({"flag8_id":g(0x08), "subtype":g(0x0c), "actor_index":g(0x04),
                        "slot":g(0x38), "op_off":off})
    return out

def analyze(name):
    d=room_blob(name)
    if not d: print(f"{name}: no blob"); return
    print(f"\n{'='*70}\n{name}  blob=0x{len(d):x}\n{'='*70}")
    sec=script_section(d)
    if not sec: print("  no slot-5"); return
    start,size=sec
    routs,how=routines(d,start,size)
    print(f"  slot-5 @0x{start:05x} size=0x{size:x}  {len(routs)} routine(s) [{how}]: "
          f"{[hex(s-start) for s,_ in routs]}")
    total_spawn=total_push=total_ops=invalid=total_aot=0
    aot_tally={}
    for ri,(s,end) in enumerate(routs):
        ops=disasm(d,s,end)
        spawns=[x for x in ops if x[1]==SPAWN]
        aots=[x for x in ops if x[1] in AOT_OPS]
        pushes=[x for x in ops if x[1]==PUSH]
        bad=[x for x in ops if x[1] not in LEN and x[1] not in (RETURN,JUMP) and x[1] not in NOP_OPS]
        total_spawn+=len(spawns); total_push+=len(pushes); total_ops+=len(ops)
        invalid+=len(bad); total_aot+=len(aots)
        for x in aots: aot_tally[x[1]]=aot_tally.get(x[1],0)+1
        end_op = ops[-1][1] if ops else None
        print(f"    routine[{ri}] @+0x{s-start:x}..0x{end-start:x}: {len(ops)} ops, "
              f"{len(pushes)} push, {len(spawns)} SPAWN, {len(aots)} AOT, {len(bad)} invalid, "
              f"ends=0x{end_op:02x}")
        idxof={x[0]:t for t,x in enumerate(ops)}
        # enemy spawns (op 0x1a)
        for si,(soff,_,_) in enumerate(spawns):
            m,nrun=block_from_pushrun(ops, idxof[soff])
            def fld(off):
                if off in m:
                    v,mode=m[off]; return f"{BLOCKMAP[off]}={v}(0x{v&0xffff:x}/{MODES.get(mode,mode)})"
                return f"{BLOCKMAP[off]}=-"
            sp=""
            if 0x20 in m and m[0x20][1]==0 and m[0x20][0] in TYPEMAP:
                sp=f"  => {TYPEMAP[m[0x20][0]]['species']}"
            print(f"      spawn[{si}] @+0x{soff-start:x} ({nrun} pushes): "
                  + "  ".join(fld(k) for k in (0x24,0x20,0x0c,0x1c,0x18,0x14,0x08,0x00)) + sp)
        # AOT commits (op 0x30/0x31/0x32/0x33)
        for ai,(aoff,aop,_) in enumerate(aots):
            m,nrun=block_from_pushrun(ops, idxof[aoff])
            fields=AOT_FIELDS.get(aop, [(0x00,"f0"),(0x04,"f4")])
            def afld(off,lab):
                if off in m:
                    v,mode=m[off]; return f"{lab}={v}(0x{v&0xffff:x}/{MODES.get(mode,mode)})"
                return f"{lab}=-"
            extra=""
            if aop==0x31 and 0x04 in m and m[0x04][1]==0:
                extra=f"  => {ITEM_IDS.get(m[0x04][0], '?id')}"
            print(f"      {AOT_OPS[aop]}[op0x{aop:02x}] @+0x{aoff-start:x} ({nrun} pushes): "
                  + "  ".join(afld(o,l) for o,l in fields) + extra)
    tally=" ".join(f"0x{k:02x}:{v}" for k,v in sorted(aot_tally.items()))
    print(f"    >>> TOTAL {total_ops} ops, {total_push} push, {total_spawn} SPAWN(0x1a), "
          f"{total_aot} AOT [{tally}], {invalid} INVALID")

if __name__=="__main__":
    for nm in (sys.argv[1:] or ["ST102.DAT"]):
        analyze(nm)
