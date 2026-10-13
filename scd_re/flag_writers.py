import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""Static census of NATIVE SetFlag (0x40752B) writers in DINO.exe (offline, capstone).

Purpose (STATIC-SCD-RE cont.47): derive door_catalog.py's toggled/runtime-register exclusion
set from the binary instead of hand-coding it. A door-gate flag with a NATIVE writer — a
`call 0x40752B` whose (group, index) are hardcoded or runtime-computed rather than authored
in room data — is runtime state (elevator/UI class, cont.46) the SCD census must not promote
to a monotonic progression gate.

SetFlag is cdecl(group, index[, value]) → pushed right-to-left, so walking BACK from the call
the first push is `group`, the second is `index`. Classification is by ARGUMENT SHAPE:
  - group imm + index imm      -> exact native flag (group, index)
  - group imm + index computed -> whole group is "native-suspect" (idx unknown; never guessed)
  - group computed             -> must be a KNOWN data-driven dispatcher site (below);
                                  anything else is unresolved and exclusion() refuses to run
Two verified exceptions (byte-cited, cont.47):
  - KNOWN_DATA_DRIVEN: sites whose (group, index) come from authored room data (SCD/AOT
    records) — the census already models those; they are NOT native writers.
  - LOOP_RANGES: sites whose index is a loop counter with immediate bounds read from the
    surrounding disasm — resolved to an exact range, not guessed.
The SCD op-0x48 handler (0x4286DF) has NO direct call site: its write goes through the SetVar
dispatcher 0x46AC0C (switch(key>>16), jump table 0x46B9DE), which inlines the bank write.

Usage:
  python tools/scd_re/flag_writers.py          # full classified site report
"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from dino import Dino
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

SETFLAG = 0x40752B
# call-site VA -> what authors its (group, index). All verified by disasm (cont.47).
KNOWN_DATA_DRIVEN = {
    0x42694C: "SCD op-0x25 set-flag handler 0x426924 (group/idx = record bytes)",
    0x44A147: "AOT action-2 set-flag 0x44A122 (slot+0x18/0x19/0x1a)",
    0x448FB4: "door type-2 self-latch 0x448F90 (group 9, lock = body+0x23)",
    0x44AA3E: "AOT action-7 handler 0x44AA00 (slot+0x28/0x29)",
    0x44BBD6: "flag-toggle object handler 0x44B90D (slot+0x18/0x19; clear arm)",
    0x44BBF4: "flag-toggle object handler 0x44B90D (slot+0x18/0x19; set arm)",
}
# call-site VA -> (group, lo, hi): `for (i=lo; i<hi; i++) SetFlag(group, i, 1)` — loop bounds
# read from the immediates at the site (mov [ebp-4], lo / cmp [ebp-4], hi).
LOOP_RANGES = {
    0x436D60: (0, 0xF0, 0xFE),
    0x43A86C: (0, 0xF0, 0xFE),
}


def _linear(d):
    text = [s for s in d.secs if s[0] == ".text"][0]
    _, vb, vs, pr, rs = text
    code = d.data[pr:pr + rs]
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    out, pc, end = [], vb, vb + rs
    while pc < end:
        off = pc - vb
        try:
            ins = next(md.disasm(code[off:off + 16], pc))
        except StopIteration:
            pc += 1
            continue
        out.append((ins.address, ins.mnemonic, ins.op_str, ins.size))
        pc += ins.size
    return out


def _imm(op_str):
    try:
        return int(op_str, 0)
    except ValueError:
        return None


def _args(instrs, i, want=2, window=14):
    """Walk back from call at index i; return the nearest `want` push operands
    (imm int or the op_str for computed), or None if a flow boundary intervenes."""
    args = []
    for j in range(i - 1, max(i - 1 - window, -1), -1):
        addr, mn, ops, _ = instrs[j]
        if mn == "push":
            v = _imm(ops)
            args.append(v if v is not None else ops)
            if len(args) == want:
                return args
        elif mn in ("call", "ret", "jmp") or mn.startswith("j"):
            break
    return None


def scan(d=None):
    """-> (nsites, exact, suspect, data_driven, unresolved)
    exact: {(g,i)}; suspect: {g: [va,...]}; data_driven: [(va, why)];
    unresolved: [(va, args)] (computed group at no known data-driven site)."""
    d = d or Dino()
    instrs = _linear(d)
    exact, suspect, data_driven, unresolved, nsites = set(), {}, [], [], 0
    for i, (addr, mn, ops, size) in enumerate(instrs):
        if mn != "call" or _imm(ops) != SETFLAG:
            continue
        nsites += 1
        if addr in KNOWN_DATA_DRIVEN:
            data_driven.append((addr, KNOWN_DATA_DRIVEN[addr]))
            continue
        if addr in LOOP_RANGES:
            g, lo, hi = LOOP_RANGES[addr]
            exact.update((g, i2) for i2 in range(lo, hi))
            continue
        args = _args(instrs, i)
        group, index = (args + [None, None])[:2] if args else (None, None)
        if isinstance(group, int) and isinstance(index, int):
            exact.add((group, index))
        elif isinstance(group, int):
            suspect.setdefault(group, []).append(addr)
        else:
            unresolved.append((addr, args))
    return nsites, exact, suspect, data_driven, unresolved


def exclusion():
    """Derived census-exclusion inputs for door_catalog.py:
    (exact_flags as {'g:i'}, suspect_groups as {int}). Raises if an unresolved
    computed-group site exists outside the known data-driven ones (never guess past it)."""
    _, exact, suspect, _, unresolved = scan()
    if unresolved:
        raise RuntimeError(
            "unresolved computed-group SetFlag site(s) outside known data-driven dispatchers: "
            + ", ".join(hex(a) for a, _ in unresolved))
    return {f"{g}:{i}" for g, i in exact}, set(suspect)


if __name__ == "__main__":
    nsites, exact, suspect, data_driven, unresolved = scan()
    print(f"call {SETFLAG:#x} sites: {nsites}")
    print(f"\ndata-driven (script/AOT-authored args — census-visible, NOT native) ({len(data_driven)}):")
    for a, n in data_driven:
        print(f"  {a:#010x}  {n}")
    print(f"\nexact native flags ({len(exact)}):")
    for g, i in sorted(exact):
        print(f"  {g}:{i}")
    print(f"\nnative-suspect groups (group imm, idx computed) ({len(suspect)}):")
    for g in sorted(suspect):
        print(f"  group {g}: sites {', '.join(hex(a) for a in suspect[g])}")
    print(f"\nUNRESOLVED computed-group sites ({len(unresolved)}):")
    for a, args in unresolved:
        print(f"  {a:#010x}  args={args}")
