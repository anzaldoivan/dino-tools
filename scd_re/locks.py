import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
"""DC1 door lock -> key-item resolver + graph reachability validator (PROVEN 2026-06-20 cont.13).

Builds on doors.py. The central cont.13 result: a door's required KEY ITEM is selected by
the door TYPE byte (record +0x28 == AOT body +0x24), NOT by the lock flag (+0x27). The lock
flag is merely the group-9 "this door is now unlocked" STATE bit.

Door interaction handler (DINO.exe): AOT type-0 (door) sub-action dispatch at 0x448f02 keys on
byte[body+0x24] (the door type):
    cmp type,8 ; ja 0x449213(generic) ; else jmp [type*4 + 0x449384]
Jump table 0x449384 (types 0..8):
    0: 0x448f2b  open immediately (ungated normal door)
    1: 0x448f30  GetFlag(9,lock): open if set, else "locked" msg -- no self-unlock
    2: 0x448f90  GetFlag(9,lock): open if set, else SetFlag(9,lock)+msg -- self-unlocks (shortcut)
    3: 0x4490a5  GetFlag(9,lock): open if set, else "locked" msg -- opened by a story event elsewhere
    4,5: 0x449213 generic (GetFlag(11,type) with type 4/5 -- not used by any real door)
    6,7,8: 0x449105  KEY CARD ladder: held level = max of GetFlag(11,0x38)->6, 0x39->7, 0x3a->8;
                     unlock iff door type <= held level (SetFlag(9,lock)); else "need higher card"
Generic handler 0x449213 (door type >= 9): GetFlag(9,lock) open if set; else GetFlag(11, type):
    if held -> SetFlag(9,lock,1) [unlock] + SetFlag(11,type,0) [consume] + open; else "locked".
    i.e. for type>=9 the TYPE BYTE *is* the required key-item id.

Group 11 (0xb) is the inventory/"item held" flag group, indexed by item id (proven: the card
door reads GetFlag(11,0x38/0x39/0x3a) = Key Card Lv C/B/A item ids). Group 9 is door-unlock state.

So the required-key model is a pure function of door type:
    type < 6  -> free (no key item; normal/shortcut/story door)
    type 6    -> Key Card Lv C (0x38) or higher
    type 7    -> Key Card Lv B (0x39) or higher
    type 8    -> Key Card Lv A (0x3a)
    type >=9  -> item id == type byte  (corpus: 0x2e Entrance Key, 0x30 BG Area Key, 0x31 C.O. Area Key)

Validated against placements.md: 0x2e Entrance Key gates 0107->0400; 0x30 BG Area Key gates
0112->010e; 0x31 C.O. Area Key gates 0600->0603; 0x3a Key Card Lv A gates the B3 Special Weapons
Storage / port doors (type-8: 606->607, 606<->611, 608<->614, 611->60d).
"""
import glob, os, re, struct
from collections import deque
from doors import room_doors

DATA = game_path("DC1", "english/Data")
START = 0x010d
ROOM_RE = re.compile(r'st([0-9a-c])([0-9a-f]{2})\.dat$', re.I)

# Key Card ladder: card item id granting each door-level (index 0 -> type 6, etc).
CARD_LADDER = {6: 0x38, 7: 0x39, 8: 0x3a}


def required_keys(door_type):
    """Item ids that satisfy a door of this type (OR semantics). () == free door."""
    if 6 <= door_type <= 8:
        # a card of level >= door_type opens it; higher cards open lower doors.
        return tuple(CARD_LADDER[lvl] for lvl in (6, 7, 8) if lvl >= door_type)
    if 9 <= door_type <= 0xfc:
        return (door_type,)            # the type byte is literally the required item id
    return ()                          # 0..5 free door; 0xfd/fe/ff special non-door action


def load_graph():
    """edges[src_code] = list of (dest_code, lock, door_type). Only rooms.json-style codes."""
    edges = {}
    for path in sorted(glob.glob(os.path.join(DATA, '[sS][tT]*.dat'))):  # case-insensitive: catches St502.dat
        m = ROOM_RE.match(os.path.basename(path))
        if not m:
            continue
        code = (int(m.group(1), 16) << 8) | int(m.group(2), 16)
        _, doors = room_doors(path)
        edges.setdefault(code, [])
        for (_p, _sub, dest, lock, dtype) in doors:
            edges[code].append((dest, lock, dtype))
    return edges


def reachable(edges, held):
    """BFS from START crossing free doors + doors satisfied by the held item set."""
    seen = {START}
    q = deque([START])
    while q:
        c = q.popleft()
        for (dest, lock, dtype) in edges.get(c, []):
            req = required_keys(dtype)
            if req and not (set(req) & held):
                continue
            if dest not in seen:
                seen.add(dest)
                q.append(dest)
    return seen


def gated_doors(edges):
    """Yield (src, dest, lock, dtype, required_keys) for every key-item-gated door."""
    for src, ds in edges.items():
        for (dest, lock, dtype) in ds:
            req = required_keys(dtype)
            if req:
                yield (src, dest, lock, dtype, req)


if __name__ == '__main__':
    edges = load_graph()
    print(f'rooms: {len(edges)}   start: {START:#06x}')
    print('\n== key-item-gated doors (by door type) ==')
    for (src, dest, lock, dtype, req) in sorted(gated_doors(edges)):
        rk = '/'.join(f'{k:#04x}' for k in req)
        print(f'  {src:#06x} -> {dest:#06x}  lock={lock:#04x} type={dtype:#04x}  needs {rk}')

    none = reachable(edges, set())
    allk = reachable(edges, {0x2e, 0x30, 0x31, 0x38, 0x39, 0x3a})
    story = {c for c in edges if (c >> 8) <= 6}  # stages 1..6 = story rooms
    print(f'\nreachable empty-handed: {len(none & story)}/{len(story)} story rooms')
    print(f'reachable with all door keys: {len(allk & story)}/{len(story)} story rooms')
    gained = sorted(allk - none)
    print('rooms unlocked only by holding door keys:',
          ' '.join(f'{c:#06x}' for c in gained) or '(none)')
    for goal in (0x060d, 0x060a, 0x0610):
        print(f'goal {goal:#06x}: empty-handed={goal in none}  with-keys={goal in allk}')
