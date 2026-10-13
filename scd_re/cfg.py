import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Control-flow / dominance analysis over a DC1 SCD init subroutine.

The "ADD an enemy" injector (RoomFile.AddEnemy) inserts a 0x20 at the *last*
opcode boundary before sub0's terminator and assumes it runs at room load. That
is false when an 0x04/0x10 subroutine-exit or a flag branch sits before that
point on every path. This tool builds a real CFG and answers the only question
that matters for a reliable spawn:

    "Is offset O guaranteed to execute on every entry of this subroutine?"

which is exactly: **O post-dominates the entry**. The set of such offsets is the
subroutine's *mandatory spine*; a dominance-safe injection point is the latest
mandatory opcode boundary (so setup has run, and the spawn is unconditional).

Edge semantics (verified from DINO.exe handlers, docs/STATIC-SCD-RE.md cont.17):
  0x0c goto       -> [target]                  (target = pos + s16[pos+2])
  0x0e cond-goto  -> [fallthrough, target]     (target = pos + s16[pos+2]);
                      special: byte[pos+1]==0 is UNCONDITIONAL -> [target] only
  0x0a loop-next  -> [fallthrough, back]       (back = pos - s16[pos+2])
  0x04 / 0x10     -> []   (subroutine exit / return: terminator, no successor)
  0x11            -> []   (end; never seen in corpus)
  0x05 goto-sub   -> []   (cont.50: re-points the running task to ANOTHER sub — a tail-call;
                           normal-path control never returns, so anything after it is dead code)
  0x0f gosub      -> [fallthrough]             (callee returns to pos+len)
  0x01 task-spawn -> [fallthrough]             (cont.50: spawns a parallel VM task — side effect)
  everything else -> [fallthrough]
A walk that runs off the end without a terminator ends at an implicit exit.

Usage:
    python tools/scd_re/cfg.py <st###.dat> [sub_index]
    python tools/scd_re/cfg.py --corpus [DIR]     # stats over every room
"""
import os
import struct
import sys

_HERE = os.path.dirname(__file__)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
from scd import get_rdt, func_table, oplen  # noqa: E402

GOTO, COND, LOOP, EXIT04, RET10, END11, GOSUB = 0x0c, 0x0e, 0x0a, 0x04, 0x10, 0x11, 0x0f
GOTOSUB05 = 0x05  # cont.50: re-points the RUNNING task to another sub (tail-call); control never
                  # falls through on the normal path (only the GetFlag(10,9) global-disable no-op
                  # gate does), so it is a terminator edge-wise. Before cont.52 this was modelled
                  # as plain fall-through and `safe` could land in dead code past the tail-call
                  # (st102 sub0: recommended 0x3A170; correct is 0x3A164).
TERMINATORS = {EXIT04, RET10, END11, GOTOSUB05}
# Opcodes a record insertion must not displace (mirror of ScriptCfg.IsControlOpcode):
# branches/loop/terminators + the 0x05 goto-sub (displacing the tail-call rewires the sub).
CONTROL = {EXIT04, GOTOSUB05, LOOP, GOTO, COND, RET10, END11}

_DEFAULT_DIRS = [
    game_path("DC1", 'english/Data'),
    game_path("DC1", 'english/Data'),
]


def s16(rdt, pos):
    return struct.unpack_from("<h", rdt, pos)[0]


def build_cfg(rdt, start, end):
    """Linear-decode [start,end) into nodes and successor edges.

    Returns (offsets, succ, ok):
      offsets  sorted list of opcode offsets (CFG nodes)
      succ     dict offset -> list of successor offsets (an out-of-range or
               post-terminator successor is dropped; [] marks a terminator/exit)
      ok       False if the walk derailed on trailing non-code data
    """
    offsets = []
    succ = {}
    pos = start
    ok = True
    while pos < end:
        op = rdt[pos]
        ln = oplen(rdt, pos)
        if not ln or pos + ln > end:
            ok = False
            break
        offsets.append(pos)
        nxt = pos + ln
        ft = nxt if nxt < end else None  # fall-through (None => implicit exit)
        if op in TERMINATORS:
            succ[pos] = []
        elif op == GOTO:
            tgt = pos + s16(rdt, pos + 2)
            succ[pos] = [tgt] if start <= tgt < end else []
        elif op == COND:
            tgt = pos + s16(rdt, pos + 2)
            edges = []
            uncond = rdt[pos + 1] == 0  # bit-index 0 => always-branch (cont.17)
            if not uncond and ft is not None:
                edges.append(ft)
            if start <= tgt < end:
                edges.append(tgt)
            succ[pos] = edges
        elif op == LOOP:
            back = pos - s16(rdt, pos + 2)
            edges = []
            if ft is not None:
                edges.append(ft)
            if start <= back < end:
                edges.append(back)
            succ[pos] = edges
        else:  # gosub + every ordinary opcode: fall through
            succ[pos] = [ft] if ft is not None else []
        pos = nxt
    # drop edges to offsets that aren't real node starts (mis-aligned targets)
    valid = set(offsets)
    for o in offsets:
        succ[o] = [t for t in succ[o] if t in valid]
    return offsets, succ, ok


def reachable(offsets, succ, entry):
    seen = set()
    stack = [entry]
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        stack.extend(succ.get(n, []))
    return seen


def post_dominators(offsets, succ, entry):
    """Offsets that lie on EVERY path from entry to an exit (post-dominate entry).

    A virtual EXIT sink collects every terminator and every node with no
    in-range successor. pdom(n) computed by the classic iterative intersection;
    the mandatory spine is { n : entry in pdom(n) executed-before... } — here we
    return the set S of nodes such that every entry->EXIT path crosses the node,
    i.e. nodes that post-dominate `entry`.
    """
    nodes = [o for o in offsets if o in reachable(offsets, succ, entry)]
    nodeset = set(nodes)
    EXIT = -1
    # successors within reachable graph; terminators/dead-ends -> EXIT
    s = {}
    for n in nodes:
        outs = [t for t in succ[n] if t in nodeset]
        s[n] = outs if outs else [EXIT]
    # predecessors over nodes ∪ {EXIT}
    alln = nodes + [EXIT]
    preds = {n: [] for n in alln}
    for n in nodes:
        for t in s[n]:
            preds[t].append(n)
    # iterative post-dominators: pdom(EXIT)={EXIT}; pdom(n)={n} ∪ ∩ pdom(succ)
    full = set(alln)
    pdom = {n: set(alln) for n in alln}
    pdom[EXIT] = {EXIT}
    changed = True
    while changed:
        changed = False
        for n in nodes:
            inter = set(full)
            for t in s[n]:
                inter &= pdom[t]
            new = {n} | inter
            if new != pdom[n]:
                pdom[n] = new
                changed = True
    # n post-dominates entry iff n in pdom[entry]
    return {n for n in pdom[entry] if n != EXIT}, nodeset


def analyze_sub(rdt, start, end):
    offsets, succ, ok = build_cfg(rdt, start, end)
    if not offsets:
        return None
    entry = offsets[0]
    rset = reachable(offsets, succ, entry)
    pdom, _ = post_dominators(offsets, succ, entry)
    # tail = AddEnemy's current pick: largest 4-aligned opcode boundary > entry
    tail = max((o for o in offsets if o > entry and (o & 3) == 0), default=None)
    # recommended safe injection: latest mandatory (post-dom of entry), 4-aligned,
    # strictly after entry (so setup ran), excluding the entry itself and any control
    # opcode (a record must not displace a branch/loop/terminator/goto-sub — cont.52)
    safe = max((o for o in pdom if o > entry and (o & 3) == 0 and rdt[o] not in CONTROL),
               default=None)
    return {
        "start": start, "end": end, "ok": ok,
        "n_ops": len(offsets), "n_reach": len(rset),
        "tail": tail, "tail_mandatory": tail in pdom if tail is not None else False,
        "safe": safe, "n_mandatory": len(pdom),
        "offsets": offsets, "succ": succ, "pdom": pdom, "reach": rset,
    }


def _enemy_offsets(rdt, start, end):
    out = []
    pos = start
    while pos < end:
        ln = oplen(rdt, pos)
        if not ln or pos + ln > end:
            break
        if rdt[pos] == 0x20:
            out.append(pos)
        pos += ln
    return out


def run_one(path, sub_idx=0):
    rdt, _ = get_rdt(path)
    if rdt is None:
        print(f"{path}: no RDT")
        return
    base, starts = func_table(rdt)
    if not starts or sub_idx >= len(starts):
        print(f"{path}: no sub {sub_idx}")
        return
    start = starts[sub_idx]
    end = starts[sub_idx + 1] if sub_idx + 1 < len(starts) else len(rdt)
    a = analyze_sub(rdt, start, end)
    if not a:
        print(f"{path}: empty sub")
        return
    name = os.path.basename(path)
    print(f"{name} sub{sub_idx}  [{start:#x},{end:#x})  ops={a['n_ops']} "
          f"reachable={a['n_reach']}  parsed_ok={a['ok']}")
    print(f"  mandatory spine (post-dominates entry): {a['n_mandatory']} nodes")
    th = f"{a['tail']:#x}" if a['tail'] is not None else "none"
    sf = f"{a['safe']:#x}" if a['safe'] is not None else "none"
    print(f"  AddEnemy tail offset = {th}  mandatory={a['tail_mandatory']}  "
          f"({'OK' if a['tail_mandatory'] else 'UNDOMINATED -> may not spawn'})")
    print(f"  recommended safe offset = {sf}")
    ens = _enemy_offsets(rdt, start, end)
    if ens:
        dom = [hex(o) for o in ens if o in a['pdom']]
        und = [hex(o) for o in ens if o not in a['pdom']]
        print(f"  existing 0x20 in sub: {[hex(o) for o in ens]}")
        print(f"    dominated={dom}  undominated={und}")


def run_corpus(directory):
    import glob
    files = sorted(glob.glob(os.path.join(directory, "st*.dat")) +
                   glob.glob(os.path.join(directory, "ST*.dat")))
    seen = set()
    tot = sub0_enemy = tail_bad = empty_safe = 0
    bad_rooms = []
    for path in files:
        key = os.path.basename(path).lower()
        if key in seen:
            continue
        seen.add(key)
        rdt, _ = get_rdt(path)
        if rdt is None:
            continue
        base, starts = func_table(rdt)
        if not starts:
            continue
        start = starts[0]
        end = starts[1] if len(starts) > 1 else len(rdt)
        a = analyze_sub(rdt, start, end)
        if not a:
            continue
        tot += 1
        ens = _enemy_offsets(rdt, start, end)
        if ens:
            sub0_enemy += 1
        if a['tail'] is not None and not a['tail_mandatory']:
            tail_bad += 1
            bad_rooms.append((key, a))
        if a['safe'] is None:
            empty_safe += 1
    print(f"rooms analyzed         : {tot}")
    print(f"rooms with 0x20 in sub0: {sub0_enemy}")
    print(f"tail offset UNDOMINATED : {tail_bad}  "
          f"(AddEnemy may silently no-spawn)")
    print(f"no safe offset at all   : {empty_safe}")
    print("\nUndominated-tail rooms (current AddEnemy unreliable):")
    for key, a in bad_rooms:
        sf = f"{a['safe']:#x}" if a['safe'] is not None else "NONE"
        print(f"  {key:14} tail={a['tail']:#x} -> safe={sf}  "
              f"(spine={a['n_mandatory']}/{a['n_ops']} ops)")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(0)
    if args[0] == "--corpus":
        d = args[1] if len(args) > 1 else next((p for p in _DEFAULT_DIRS if os.path.isdir(p)), None)
        if not d:
            sys.exit("no Data dir found; pass one explicitly")
        run_corpus(d)
    else:
        run_one(args[0], int(args[1]) if len(args) > 1 else 0)
