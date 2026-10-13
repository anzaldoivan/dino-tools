import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC1 enemy / model inventory extractor (Phase 2, 2026-06-20 cont.11).

Clone of items.py for the enemy surface. See docs/STATIC-SCD-RE.md cont.10/11
and memory scd-enemy-opcode-found.

Enemy placement = SCD opcode 0x20 (handler 0x426370, fixed len 24), the
SCE_EM_SET analog. Record layout (file form, before relocation):
    +1   slot      = byte   (large-entity array index, stride 0x260)
    +2   category  = byte   (entity AI/class; 0x01 raptor-grounded, 0x04, 0x07
                             flyer/pteranodon, ... NOT a fine species id)
    +4   killflag  = byte   (GetFlag group 4 "already killed" id)
    +0x10 model    = u32    PSX ptr 0x8010xxxx -> EMD model resource (species)
    +0x14 motion   = u32    PSX ptr 0x8014xxxx -> motion/animation resource

The SPECIES is bound to the loaded MODEL resource referenced by +0x10, not to a
small enum byte (cont.10). So the editable, in-room-safe surface is the
(model, motion) POINTER PAIR: both targets are already loaded in the room (they
back the existing enemies), so permuting the pair among the room's enemy slots
is guaranteed-loaded and round-trips. That is what the randomizer does.

op23 (object-reg, handler 0x4266a6, fixed len 32) registers model/object
resources: dword[+8] is a model resource PSX ptr. We surface the distinct +8
pointers per room as the room's loaded-model inventory (Option-1 foundation:
mapping a model ptr -> a species id later lets us go beyond in-room swaps).

Filtering: the init-script walk occasionally desyncs and emits spurious "0x20"
records (all-0x80 padding, etc). A REAL enemy record is recognised by a valid
EMD/motion PSX pointer at +0x10 (and usually +0x14): PSX_LO <= ptr < PSX_HI.
"""
import struct
from scd import get_rdt, func_table, walk_sub

OP20_ENEMY = 0x20
OP20_LEN = 24
OP23_OBJREG = 0x23
OP23_LEN = 32

SLOT_OFF = 0x01
CAT_OFF = 0x02
KILLFLAG_OFF = 0x04
MODEL_OFF = 0x10
MOTION_OFF = 0x14
OP23_MODEL_OFF = 0x08

# Loaded-resource PSX address window. RDT data relocates into 0x8010xxxx..; real
# enemy model/motion ptrs observed in 0x80100000-0x80160000. Padding/junk records
# carry 0x00800080 / 0x80368000 etc, which fall outside this window.
PSX_LO = 0x80100000
PSX_HI = 0x80180000


def _is_loaded_ptr(p):
    return PSX_LO <= p < PSX_HI


# --- species decode (cont.14) --------------------------------------------------
# The model ptr (+0x10) targets a model block embedded in the SAME decompressed RDT
# buffer at (ptr - 0x80100000). The block's bone count (dword @ +0x14) plus the bone
# parent-hierarchy (bone array @ +0x18, stride 0x14: int16 x/y/z, int8 parent @+6,
# byte flag @+7) is a stable per-species invariant. Bone count is unique per class and
# 1:1 with the AI category over the whole corpus (139/139 records).
BONE_COUNT_OFF = 0x14
BONE_ARRAY_OFF = 0x18
BONE_STRIDE = 0x14
SPECIES_BY_BONES = {
    15: 'Humanoid',         # cat 1 - the human rig (NPCs, corpses); cont.97
    21: 'Velociraptor',     # cat 2 - the common raptor (was 'Raptor(heavy)')
    20: 'Tyrannosaurus',    # cat 3 - boss rig (was 'LargeGround'; cont.23)
    10: 'Tyrannosaurus',    # cat 4 - Chief's Room rig
    7:  'Swarm',            # cat 5
    18: 'Pteranodon',       # cat 7
    22: 'Therizinosaurus',  # cat 8 (placed by 0x59 / 0x5B)
}


def bone_count(rdt, model_ptr):
    """Skeleton bone count for the model at file-form model_ptr; 0 if it doesn't decode."""
    off = model_ptr - 0x80100000
    if off < 0 or off + BONE_COUNT_OFF + 4 > len(rdt):
        return 0
    c = struct.unpack_from('<I', rdt, off + BONE_COUNT_OFF)[0]
    if c == 0 or c > 64:
        return 0
    if off + BONE_ARRAY_OFF + c * BONE_STRIDE > len(rdt):
        return 0
    return c


def species(rdt, model_ptr):
    """(bone_count, species_name) for the model at model_ptr."""
    bc = bone_count(rdt, model_ptr)
    return bc, SPECIES_BY_BONES.get(bc, 'Unknown')


def enemy_records(rdt):
    """Yield real op20 enemy records.

    Each: (buf_offset, sub_idx, slot, category, killflag, model_ptr, motion_ptr).
    """
    base, starts = func_table(rdt)
    if not starts:
        return
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op != OP20_ENEMY or ln != OP20_LEN:
                continue
            model = struct.unpack_from('<I', rdt, p + MODEL_OFF)[0]
            motion = struct.unpack_from('<I', rdt, p + MOTION_OFF)[0]
            if not _is_loaded_ptr(model):
                continue  # padding / walk desync, not a placed enemy
            yield (p, i, rdt[p + SLOT_OFF], rdt[p + CAT_OFF],
                   rdt[p + KILLFLAG_OFF], model, motion)


def model_registrations(rdt):
    """Yield op23 object-reg records: (buf_offset, sub_idx, index, model_ptr)."""
    base, starts = func_table(rdt)
    if not starts:
        return
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op != OP23_OBJREG or ln != OP23_LEN:
                continue
            mp = struct.unpack_from('<I', rdt, p + OP23_MODEL_OFF)[0]
            yield (p, i, rdt[p + SLOT_OFF], mp)


def room_enemies(path):
    """(rdt, [enemy_records], [model_registrations]) or (None, [], [])."""
    rdt, typ = get_rdt(path)
    if rdt is None:
        return None, [], []
    return rdt, list(enemy_records(rdt)), list(model_registrations(rdt))


def room_inventory(path):
    """Summarise a room for the swap planner.

    Returns dict: {
        slots: {slot: {cat, killflag, model, motion, count}},  # per enemy slot
        models: [distinct (model, motion) pairs across slots],
        loaded_models: sorted set of op23 model ptrs,           # Option-1 base
    }
    A slot may appear in several subroutines (difficulty / revisit variants); we
    fold them, asserting the (model, motion) is consistent per slot.
    """
    rdt, ens, regs = room_enemies(path)
    slots = {}
    for _, sub, slot, cat, kf, model, motion in ens:
        d = slots.get(slot)
        if d is None:
            bc, sp = species(rdt, model)
            slots[slot] = {'cat': cat, 'killflag': kf, 'model': model,
                           'motion': motion, 'count': 1, 'bones': bc, 'species': sp}
        else:
            d['count'] += 1
    pairs = sorted({(d['model'], d['motion']) for d in slots.values()})
    loaded = sorted(
        {(mp, *species(rdt, mp)) for _, _, _, mp in regs if _is_loaded_ptr(mp)})
    return {'slots': slots, 'models': pairs, 'loaded_models': loaded}
