import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""I1 probe for docs/dc2/DC2-STARTING-LOADOUT-PLAN.md.

1. Asserts the four new-game-bootstrap byte sequences (EXE-SYMBOLS.md 0x450D60) at their
   file offsets in the rebirth Dino2.exe (raw = VA - 0x400000 for .text).
2. Report-only sweep of the bootstrap body 0x450D60..0x450F00 for other immediate-store
   patterns (mov word/byte/dword [reg+disp32], imm) — candidate ammo/inventory init sites.

Usage: python3 start_loadout_probe.py [path/to/Dino2.exe]
"""
import sys
from pathlib import Path

DEFAULT_EXE = Path(__file__).resolve().parents[2].parent / "dinorand" / "4249140_DinoCrisis2/rebirth/Dino2.exe"

SITES = [
    # (file offset, bytes, label)
    (0x50D68, bytes.fromhex("BA01010000"),          "Dylan equip mov edx,0x0101 (VA 0x450D68)"),
    (0x50E96, bytes.fromhex("66C780B6100000 0202".replace(" ", "")), "Regina preset mov word [eax+0x10B6],0x0202 (VA 0x450E96)"),
    (0x50EB7, bytes.fromhex("66C782BA100000 0A05".replace(" ", "")), "David preset mov word [edx+0x10BA],0x050A (VA 0x450EB7)"),
    (0x50E7D, bytes.fromhex("C6808F1000 0001".replace(" ", "")),     "char byte forced 1 (VA 0x450E7D)"),
    # Dylan equip-word stores (the lever's rewrite windows; edx imm 0x50D68 is dual-use START ROOM)
    (0x50E84, bytes.fromhex("8b0db86d8700668991b4100000"),           "Dylan equip store [scene+0x10B4] (VA 0x450E84)"),
    (0x50E9F, bytes.fromhex("8b0db86d8700b8b0040000668991b8100000"), "Dylan equip store [scene+0x10B8] + EP imm (VA 0x450E9F)"),
    # inventory-init 0x496750 template record sites (I3 decode: scene+0x10BC records)
    (0x96786, bytes.fromhex("889c24c0000000889c24c1000000"), "Dylan shotgun record present+id, template A (VA 0x496786)"),
    (0x968B6, bytes.fromhex("885c2454885c2455"),             "Dylan shotgun record present+id, template B (VA 0x4968B6)"),
    (0x969E9, bytes.fromhex("885c240c885c240d"),             "Dylan shotgun record present+id, template C (VA 0x4969E9)"),
    (0x967BA, bytes.fromhex("c68424cd00000002"),             "Regina handgun record id imm, template A (VA 0x4967BA)"),
    (0x968D5, bytes.fromhex("c644246102"),                   "Regina handgun record id imm, template B (VA 0x4968D5)"),
    (0x969FE, bytes.fromhex("c644241902"),                   "Regina handgun record id imm, template C (VA 0x4969FE)"),
]

SWEEP_LO, SWEEP_HI = 0x50D60, 0x50F00  # bootstrap body, file offsets
EXPECTED_LEN = 1_204_224


def sweep(exe: bytes):
    """Naive scan for immediate stores to [reg+disp32] in the bootstrap. Report-only."""
    hits = []
    i = SWEEP_LO
    while i < SWEEP_HI:
        b = exe[i:i + 12]
        # 66 C7 /0 disp32 imm16 : mov word [reg+disp32], imm16
        if b[0] == 0x66 and b[1] == 0xC7 and (b[2] & 0xC0) == 0x80:
            disp = int.from_bytes(b[3:7], "little")
            imm = int.from_bytes(b[7:9], "little")
            hits.append((i, f"mov word [reg+0x{disp:X}], 0x{imm:04X}"))
            i += 9
            continue
        # C7 /0 disp32 imm32 : mov dword [reg+disp32], imm32
        if b[0] == 0xC7 and (b[1] & 0xC0) == 0x80:
            disp = int.from_bytes(b[2:6], "little")
            imm = int.from_bytes(b[6:10], "little")
            hits.append((i, f"mov dword [reg+0x{disp:X}], 0x{imm:08X}"))
            i += 10
            continue
        # C6 /0 disp32 imm8 : mov byte [reg+disp32], imm8
        if b[0] == 0xC6 and (b[1] & 0xC0) == 0x80:
            disp = int.from_bytes(b[2:6], "little")
            hits.append((i, f"mov byte [reg+0x{disp:X}], 0x{b[6]:02X}"))
            i += 7
            continue
        i += 1
    return hits


def main():
    exe_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_EXE
    exe = exe_path.read_bytes()
    ok = True
    if len(exe) != EXPECTED_LEN:
        print(f"FAIL length {len(exe)} != {EXPECTED_LEN} (unrecognized build)")
        ok = False
    for off, want, label in SITES:
        got = exe[off:off + len(want)]
        status = "ok  " if got == want else "FAIL"
        if got != want:
            ok = False
        print(f"{status} 0x{off:05X} {label}: {got.hex(' ')}")

    print(f"\nsweep 0x{SWEEP_LO:05X}..0x{SWEEP_HI:05X} (candidate ammo/inventory init writes, report-only):")
    for off, desc in sweep(exe):
        va = 0x400000 + off
        print(f"  VA 0x{va:06X}  {desc}")

    print("\nPASS" if ok else "\nFAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
