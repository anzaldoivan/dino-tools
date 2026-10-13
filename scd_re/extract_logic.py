import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Auto-derive the DC1 progression-logic overlay from the SCD flag dataflow.

The decision record is DATA-BACKFILL-PLAN.md. In short: BioRand hand-authored its door
`requiresRoom` gates; we instead JOIN the binary's own flag dataflow:

    SetFlag(9, idx)  (producer event, in some room)   <-- group 9 = door-unlock state
    door record      (type 1/3, lock = idx)           <-- "opened by a story event elsewhere"

A story-locked door (type 1 or 3) opens only once GetFlag(9, lock) is set, and the engine
currently treats those types as FREE. So the room that runs SetFlag(9, lock) is a real,
missing progression requirement on that door edge. We emit it as a door `requiresRoom`.

Tiers (keep the live overlay provably safe — see DATA-BACKFILL-PLAN §4):
  * HIGH  -> exactly one *external* producer room (!= the door's own room): written live to
            data/dc1/map.json as rooms[srcRoom].doors[destSSRR].requiresRoom = [producerRoom].
  * REVIEW-> zero producers (flag set natively / by a cutscene) or many (ambiguous): recorded
            in data/dc1/map-requirements.md only, never activated.

Key-item door gates (types 6/7/8/>=9) are deliberately NOT emitted: GameDefinition.KeyItemsForDoor
already owns them, and a single source of truth avoids double-gating.

Usage:
    python tools/scd_re/extract_logic.py            # write map.json + map-requirements.md
    python tools/scd_re/extract_logic.py --dry-run  # print summary, write nothing
"""
import glob
import json
import os
import re
import sys
from collections import defaultdict

import scd
import doors as D

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
DATA = game_path("DC1", "english/Data")
MAP_JSON = os.path.join(ROOT, 'data', 'dc1', 'map.json')
LEDGER = os.path.join(ROOT, 'data', 'dc1', 'map-requirements.md')

ROOM_RE = re.compile(r'st([0-9a-c])([0-9a-f]{2})\.dat$', re.I)
GROUP_DOOR_UNLOCK = 9          # SetFlag/GetFlag group for door-unlock state (locks.py cont.13)
STORY_DOOR_TYPES = (1, 3)      # "opened by a story event elsewhere"; type 2 self-unlocks (shortcut)
LATCH_SETTER_TYPE = 2          # type-2 door: SetFlag(9,lock) on traverse (STATIC-SCD-RE cont.40)
SETFLAG = 0x25                 # SetFlag(group=b[1], index=b[2], value=b[3])


def room_code(path):
    m = ROOM_RE.search(os.path.basename(path))
    return int(m.group(1), 16) * 0x100 + int(m.group(2), 16)


def walk_ops(rdt):
    """Yield (sub_index, offset, opcode, bytes) over every subroutine of the room script."""
    _, subs = scd.func_table(rdt)
    starts = sorted(set(subs))
    for s in starts:
        end = min([x for x in starts if x > s] + [len(rdt)])
        pos = s
        while pos < end:
            ln = scd.oplen(rdt, pos)
            if not ln:
                break
            yield s, pos, rdt[pos], rdt[pos:pos + ln]
            pos += ln


GROUP_ITEM_HELD = 11          # GetFlag group for "item held" (0x52 = GetFlag(11, b[1]); 0x26 grp 11)
ITEM_LOOKBACK = 8             # ops to scan before a SetFlag for a held-item guard
NATIVE_SCREEN = 0x58          # op-0x58 = native-screen spawn (handler 0x429AD2, STATIC-SCD-RE cont.61)
SCREEN_DDK = 3                # screen type 3 = DDK; b[3] = Input-disc id; the native disc gate
                              # 0x4B182A requires GetFlag(11, id) AND GetFlag(11, id+7) (Code disc)
DDK_CODE_OFFSET = 7           # Code disc id = Input disc id + 7 (0x62..0x68 -> 0x69..0x6f)


def scan():
    """Return (producers, story_doors, in_scope_rooms, item_gated).

    producers[idx]      = {room_code: [offset, ...]}  for SetFlag(9, idx)
    story_doors         = [(src, dest, lock, type, offset), ...]  for type in STORY_DOOR_TYPES
    item_gated[(room,idx)] = {item_id, ...}  held-item checks guarding that room's SetFlag(9, idx)
                             (empty/absent ⇒ a free in-room switch, no progression item)
    """
    producers = defaultdict(lambda: defaultdict(list))
    latch_setters = defaultdict(lambda: defaultdict(list))  # type-2 door producers: lock -> {room: [off]}
    item_gated = defaultdict(set)
    story_doors = []
    rooms = set()
    for f in sorted(glob.glob(os.path.join(DATA, '[sS][tT][0-9a-c][0-9a-f][0-9a-f].dat'))):  # case-insensitive: catches St502.dat
        rdt, _ = scd.get_rdt(f)
        if rdt is None:
            continue
        r = room_code(f)
        rooms.add(r)
        try:
            ops = list(walk_ops(rdt))                 # materialise for held-item lookback
            # DDK screens per sub: op-0x58 type-3 launches the native DDK object, whose disc gate
            # (0x4B182A) requires BOTH GetFlag(11, id) and GetFlag(11, id+7). The guard is native,
            # so the SCD lookback below can't see it — credit the disc pair to every SetFlag(9,·)
            # in the same event sub (byte-verified 1:1, 7 sites, STATIC-SCD-RE cont.61 §C).
            ddk_by_sub = {}
            for _si, _off, op, b in ops:
                if op == NATIVE_SCREEN and len(b) >= 4 and b[2] == SCREEN_DDK:
                    ddk_by_sub[_si] = b[3]
            for i, (_si, off, op, b) in enumerate(ops):
                if op == SETFLAG and len(b) >= 4 and b[1] == GROUP_DOOR_UNLOCK:
                    idx = b[2]
                    producers[idx][r].append(off)
                    if _si in ddk_by_sub:
                        item_gated[(r, idx)].update(
                            (ddk_by_sub[_si], ddk_by_sub[_si] + DDK_CODE_OFFSET))
                    for _sj, _oj, opj, bj in ops[max(0, i - ITEM_LOOKBACK):i]:
                        if opj == 0x52 and len(bj) >= 2:                 # GetFlag(11, item)
                            item_gated[(r, idx)].add(bj[1])
                        elif opj == 0x26 and len(bj) >= 3 and bj[1] == GROUP_ITEM_HELD:
                            item_gated[(r, idx)].add(bj[2])
        except Exception as e:                       # noqa: BLE001 — skip undecodable room, keep going
            print(f'  warn: {os.path.basename(f)} script walk failed: {e}', file=sys.stderr)
        _, recs = D.room_doors(f)
        for (off, _sub, dest, lock, typ) in recs:
            if typ in STORY_DOOR_TYPES and lock:
                story_doors.append((r, dest, lock, typ, off))
            elif typ == LATCH_SETTER_TYPE and lock:
                # A type-2 door SetFlag(9,lock)s natively on traversal (STATIC-SCD-RE cont.40), so its
                # source room is a real producer of that lock — the reciprocal of a type-1 shortcut.
                latch_setters[lock][r].append(off)
    return producers, latch_setters, story_doors, rooms, item_gated


def classify(producers, latch_setters, story_doors, in_scope, item_gated):
    """Split derived gates into high-confidence / item-puzzle / free-switch / shortcut / native tiers."""
    high, puzzle_item, switch, shortcut, native = [], [], [], [], []
    for (src, dest, lock, typ, off) in story_doors:
        prod_rooms = set(producers.get(lock, {}))
        ext = sorted(prod_rooms - {src})
        items = sorted(item_gated.get((src, lock), set()))
        latch_rooms = sorted(set(latch_setters.get(lock, {})) - {src})  # type-2 producers, external
        gate = dict(src=src, dest=dest, lock=lock, type=typ, off=off,
                    producers=ext, self_producer=src in prod_rooms, items=items,
                    latch_rooms=latch_rooms)
        if src not in in_scope:
            continue                                 # demo/variant source: out of overlay scope
        if len(ext) == 1:
            high.append(gate)                        # one external producer room -> live requiresRoom
        elif not ext and gate['self_producer']:
            (puzzle_item if items else switch).append(gate)  # in-room unlock: item-gated vs free switch
        elif typ == 1 and latch_rooms:
            shortcut.append(gate)                    # door<->door shortcut: reciprocal type-2 door sets it
        else:
            native.append(gate)                      # no static producer (native/cutscene) or ambiguous
    return high, puzzle_item, switch, shortcut, native


def apply_to_map(high, puzzle_item):
    """Merge derived gates into map.json (minimal diff: only adds doors keys).

    high        -> rooms[src].doors[dest].requiresRoom = [producer]
    puzzle_item -> rooms[src].doors[dest].requires = [item, ...] (AND — e.g. the DDK
                   Input+Code disc pair, both required by the native gate 0x4B182A)
    """
    doc = json.load(open(MAP_JSON, encoding='utf-8'))
    rooms = doc['rooms']
    added = 0
    for g in high + puzzle_item:
        key = f'{g["src"]:04x}'
        if key not in rooms:
            continue
        room = rooms[key]
        door_map = room.setdefault('doors', {})
        dest_key = f'{g["dest"]:04x}'
        entry = door_map.setdefault(dest_key, {})
        if g in puzzle_item:
            entry['requires'] = list(g['items'])
        else:
            entry['requiresRoom'] = [f'{g["producers"][0]:04x}']
        added += 1
    json.dump(doc, open(MAP_JSON, 'w', encoding='utf-8'), indent=2, ensure_ascii=True)
    open(MAP_JSON, 'a', encoding='utf-8').write('\n')   # trailing newline, like the original
    return added


def write_ledger(high, puzzle_item, switch, shortcut, native):
    def row(g, last):
        return ('| {src:04x} | {dest:04x} | type {type} | 9/{lock} | ' + last + ' | door@0x{off:x} |'
                ).format(**g)

    def table(gates, header, last):
        head = [f'| Door room | Dest | Door type | Flag (grp/idx) | {header} | Provenance |',
                '|---|---|---|---|---|---|']
        return head + ([row(g, last) for g in gates] or ['| _(none)_ | | | | | |'])

    def prods(g):
        return ', '.join('{:04x}'.format(p) for p in g['producers']) or '—'

    def items(g):
        return ', '.join('0x{:02x}'.format(i) for i in g['items']) or '—'

    def latch(g):
        return ', '.join('{:04x}'.format(p) for p in g['latch_rooms']) or '—'

    for g in high + puzzle_item + switch + shortcut + native:
        g['_prods'] = prods(g)
        g['_items'] = items(g)
        g['_latch'] = latch(g)

    lines = [
        '# DC1 progression-gate provenance ledger',
        '',
        '**Auto-generated by `tools/scd_re/extract_logic.py`** (DATA-BACKFILL-PLAN.md). Each gate is a',
        'pure flag-dataflow join: a story-locked door (type 1/3) tested on `GetFlag(9, lock)`, matched',
        'to the room(s) that run `SetFlag(9, lock)` and to any `GetFlag(11, item)` held-item guard near',
        'that setter. Do not hand-edit — re-run the tool.',
        '',
        '`requiresRoom` arrays are AND; OR stays structural / door-type (DATA-BACKFILL-PLAN §3).',
        '',
        '## High-confidence gates (LIVE in map.json)',
        '',
        'Exactly one *external* producer room; written to `rooms[src].doors[dest].requiresRoom`.',
        '',
        *table(high, 'Producer room', '{_prods}'),
        '',
        '## Same-room item-gated puzzles (LIVE in map.json as door `requires`)',
        '',
        'Unlocked by an in-room `SetFlag(9,…)` guarded by a held-item check — either an SCD',
        '`GetFlag(11,item)` or the NATIVE DDK disc gate (`op-0x58` type-3 screen; `0x4B182A` requires',
        'the Input disc AND the Code disc = id+7 — STATIC-SCD-RE cont.61 §C). Written to',
        '`rooms[src].doors[dest].requires = [items…]` (AND semantics).',
        '',
        *table(puzzle_item, 'Held item(s)', '{_items}'),
        '',
        '## Free in-room switches (NOT a logic gate — no action needed)',
        '',
        'Unlocked by an in-room `SetFlag(9,…)` with **no** held-item check: a switch/examine/story event',
        'you can always trigger once you are in the room. Not randomizer-relevant — the engine\'s',
        'permissive type-1/3 handling already models these correctly (free once reachable).',
        '',
        *table(switch, 'Self event', '{_prods}'),
        '',
        '## Door↔door shortcuts (latch-modeled — NOT a map.json gate)',
        '',
        'A type-1 reader whose lock is set by its reciprocal **type-2 door** (`SetFlag(9,lock)` on',
        'traversal — STATIC-SCD-RE.md cont.40), i.e. an "open from the far side" shortcut. The producer is',
        'a door, not an SCD event, so the old scan filed these under "No static producer" — they are not',
        'native/unknown. Owned by the group-9 latch in `KeyItemPlacer.Reachable` (a type-2 traverse sets',
        'the lock; a type-1 door is passable only once latched), the single source of truth — so they are',
        '**not** emitted to `map.json` (that would double-gate). Listed here for provenance only.',
        '',
        *table(shortcut, 'Type-2 setter room(s)', '{_latch}'),
        '',
        '## No static producer (CE follow-up — NOT activated)',
        '',
        'The unlock flag is never `SetFlag` in any room script, set by no type-2 door, and set by no',
        '`DINO.exe`-visible producer (native code / cutscene), or has multiple ambiguous producers. Engine',
        'stays permissive (beatable); revisit only for door-rando correctness — see ROADMAP / DATA-BACKFILL-PLAN §7.',
        '',
        *table(native, 'Producers', '{_prods}'),
        '',
    ]
    open(LEDGER, 'w', encoding='utf-8').write('\n'.join(lines))


def main():
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    global ROOT, MAP_JSON, LEDGER
    ROOT = os.path.abspath(args.repo_root)
    MAP_JSON = os.path.join(ROOT, "data", "dc1", "map.json")
    LEDGER = os.path.join(ROOT, "data", "dc1", "map-requirements.md")
    dry = '--dry-run' in sys.argv
    if not os.path.isdir(DATA):
        sys.exit(f'vanilla data dir not found: {DATA} (set DINORAND_DC1_DIR)')
    producers, latch_setters, story_doors, in_scope, item_gated = scan()
    high, puzzle_item, switch, shortcut, native = classify(
        producers, latch_setters, story_doors, in_scope, item_gated)
    print(f'rooms scanned: {len(in_scope)}  story doors (type 1/3): {len(story_doors)}')
    print(f'HIGH-confidence requiresRoom gates (LIVE): {len(high)}')
    for g in high:
        print(f'  {g["src"]:04x} -> {g["dest"]:04x} (type {g["type"]}, flag 9/{g["lock"]}) '
              f'requiresRoom [{g["producers"][0]:04x}]')
    print(f'same-room item-gated puzzles (increment 2): {len(puzzle_item)}')
    print(f'free in-room switches (no logic gate): {len(switch)}')
    print(f'door<->door shortcuts (latch-modeled, not a map.json gate): {len(shortcut)}')
    print(f'no-static-producer gates (CE follow-up): {len(native)}')
    if dry:
        print('(dry run — wrote nothing)')
        return
    added = apply_to_map(high, puzzle_item)
    write_ledger(high, puzzle_item, switch, shortcut, native)
    print(f'wrote {added} gates to {os.path.relpath(MAP_JSON, ROOT)}')
    print(f'wrote ledger {os.path.relpath(LEDGER, ROOT)}')


if __name__ == '__main__':
    main()
