import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Step 2 (CROSS-ROOM-SPECIES-PLAN): extract a species' model+motion as a
pointer-closed, relocatable unit, with verified extents.

Strategy (no PSX mesh-format parse needed):
  - relocation set = every 4-aligned dword in the RDT whose value is a valid
    file-form PSX ptr [B, B+len). That is exactly what the engine relocates.
  - resource starts = the targets of every op20 +0x10/+0x14 and op23 +8 ptr, i.e.
    the heads of models/motions/scenery the script references. Sorted, these
    partition the RDT into resource blocks.
  - a model/motion region = [start, next_resource_start). We then VERIFY the region
    is pointer-closed:
       (a) every reloc slot inside the region targets inside the region, and
       (b) no reloc slot outside the region targets into it, except the known
           external references (the importing op20 record's +0x10/+0x14).
    If (a)&(b) hold the extent is correct and the block is independently movable.
"""
import sys, struct
from scd import get_rdt, func_table, walk_sub

DATA = game_path("DC1", 'english/Data')
B = 0x80100000


def relocation_set(rdt):
    out = {}
    n = len(rdt)
    for o in range(0, n - 3, 4):
        v = struct.unpack_from('<I', rdt, o)[0]
        if B <= v < B + n:
            out[o] = v - B
    return out


def rdt_tail_bounds(rdt):
    """Offsets of the RDT-tail structures (func table + the 9 relocated header ptrs).
    These cap a trailing resource (e.g. motion) that has no op20/op23 head after it."""
    bounds = set()
    base, subs = func_table(rdt)
    if base:
        bounds.add(base)
    for hoff in range(0, 0x24, 4):                 # 9 RDT header dwords @ rdt+0
        v = struct.unpack_from('<I', rdt, hoff)[0]
        if B <= v < B + len(rdt):
            bounds.add(v - B)
    return bounds


def op20_record_refs(rdt):
    """Slot offsets of every op20 +0x10/+0x14 field (the EXPECTED inbound refs to a
    model/motion: these are the records we repoint on import)."""
    refs = set()
    base, subs = func_table(rdt)
    if not subs:
        return refs
    for i, s in enumerate(subs):
        e = subs[i + 1] if i + 1 < len(subs) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op == 0x20 and ln == 24:
                refs.add(p + 0x10)
                refs.add(p + 0x14)
    return refs


def resource_starts(rdt):
    """Targets of op20 +0x10/+0x14 and op23 +8 — the heads of referenced resources."""
    starts = set()
    base, subs = func_table(rdt)
    if not subs:
        return starts
    for i, s in enumerate(subs):
        e = subs[i + 1] if i + 1 < len(subs) else len(rdt)
        ops, _ = walk_sub(rdt, s, e)
        for p, op, ln in ops:
            if op == 0x20 and ln == 24:
                for fo in (0x10, 0x14):
                    v = struct.unpack_from('<I', rdt, p + fo)[0]
                    if B <= v < B + len(rdt):
                        starts.add(v - B)
            elif op == 0x23 and ln == 32:
                v = struct.unpack_from('<I', rdt, p + 8)[0]
                if B <= v < B + len(rdt):
                    starts.add(v - B)
    return starts


def region_extent(start, starts_sorted, rdt_len):
    """[start, next resource start) — the block bounded by the next referenced head."""
    for s in starts_sorted:
        if s > start:
            return start, s
    return start, rdt_len


def verify_closed(rdt, reloc, lo, hi, external_ok):
    """Return (ok, internal_slots, escapes_out, inbound_unexpected)."""
    internal = {o: t for o, t in reloc.items() if lo <= o < hi}
    escapes = {o: t for o, t in internal.items() if not (lo <= t < hi)}
    inbound = {o: t for o, t in reloc.items()
               if not (lo <= o < hi) and lo <= t < hi and o not in external_ok}
    return (not escapes and not inbound), internal, escapes, inbound


def extract(room, model_ptr, motion_ptr):
    rdt, _ = get_rdt(f"{DATA}/st{room}.dat")
    reloc = relocation_set(rdt)
    # boundaries = resource heads + RDT-tail structures (cap trailing motion)
    bounds = (resource_starts(rdt) | rdt_tail_bounds(rdt)
              | {model_ptr - B, motion_ptr - B})
    starts = sorted(bounds)
    ext_ok = op20_record_refs(rdt)                 # expected inbound to model/motion
    mo, mh = region_extent(model_ptr - B, starts, len(rdt))
    to, th = region_extent(motion_ptr - B, starts, len(rdt))
    print(f"\n=== st{room} rdt={len(rdt):#x} reloc_slots={len(reloc)} bounds={len(starts)} ===")
    for name, ptr, lo, hi in [("MODEL", model_ptr, mo, mh), ("MOTION", motion_ptr, to, th)]:
        ok, internal, esc, inb = verify_closed(rdt, reloc, lo, hi, ext_ok)
        print(f"  {name:6} {ptr:#010x}  [{lo:#07x}..{hi:#07x})  size={hi-lo:#x} ({hi-lo} B)")
        print(f"         internal_ptr_slots={len(internal)}  escapes_out={len(esc)}  "
              f"unexpected_inbound={len(inb)}  CLOSED={ok}")
        if esc:
            print(f"         ESCAPES: {[(hex(o),hex(t)) for o,t in list(esc.items())[:6]]}")
        if inb:
            print(f"         INBOUND: {[(hex(o),hex(t)) for o,t in list(inb.items())[:6]]}")
    return rdt, (mo, mh), (to, th)


if __name__ == "__main__":
    # corpse (humanoid control), raptor, pteranodon
    extract("50c", 0x8010fa7c, 0x80140a20)
    extract("10e", 0x80110e70, 0x80114e10)
    from enemies import enemy_records
    r, _ = get_rdt(f"{DATA}/st407.dat")
    rec = next(enemy_records(r))
    extract("407", rec[5], rec[6])
