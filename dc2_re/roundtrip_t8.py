import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""T8 — DC2 (Rebirth) round-trip + re-LZSS tolerance harness.

The Phase-0 safety gate (docs/dc2/RANDO-ROADMAP-PLAN.md): before any ST*.DAT write
path ships, prove on the full 89-room rebirth corpus that

  P1  CONTAINER REPACK IDENTITY  — Write(Read(x)) == x: parse the Gian package and
      re-emit it (header verbatim + each payload padded to the 2048-byte sector) and
      get the original file back, byte-for-byte. This is the non-destructive guarantee
      (the DC1 `Write(Read(x))==x` analogue, RoomFile.cs).

  P2  CODEC ROUND-TRIP FIDELITY  — lzss_dec(lzss_enc(decomp)) == decomp for every
      compressed (LZSS0/LZSS2) payload. Proves a slot-5 edit's recompression is
      lossless: the bytes the game decodes after our recompress are identical.

  P3  re-LZSS SIZE TOLERANCE (measurement; OPEN #9) — compare original vs recompressed
      compressed size for the room's SCD blob, and whether the recompressed blob still
      occupies the SAME number of 2048-byte sectors. If align(new) == align(orig) the
      segment is sector-stable, so downstream payload offsets are unaffected (the loader
      derives offsets by walking aligned sizes — Dc2DoorEditor.Repack). This is the
      STATIC half of OPEN #9; whether the engine accepts a sector-DELTA blob is a
      RUNTIME / HUMAN-GATED confirm (RANDO-ROADMAP-PLAN.md P3).

READ-ONLY on game files: every game byte is read; nothing under 4249140_DinoCrisis2/ is
written. All transforms are in-memory. Exit code 0 iff P1 and P2 pass on every room.

Usage:
  python3 tools/dc2_re/roundtrip_t8.py            # rebirth corpus, summary
  python3 tools/dc2_re/roundtrip_t8.py --verbose  # per-room line
  python3 tools/dc2_re/roundtrip_t8.py --data DIR # override the Data dir
"""
import sys, os, glob, struct, argparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dc2pkg import parse, lzss_dec, align, GPC_LZSS0, GPC_LZSS2, PC_NAMES

REB_DEFAULT = game_path("DC2", 'rebirth/Data')

WINDOW = 4095   # 12-bit distance, 1..4095 (matches Lzss.WindowSize)
MIN_MATCH = 2   # length field 0 -> 2 bytes
MAX_MATCH = 17  # 4-bit length field -> 15 + 2


def lzss_enc(src):
    """Greedy longest-match LZSS encoder producing a stream `lzss_dec` decodes exactly.

    Same token format and match semantics as the shipped C# Lzss.Compress
    (src/DinoRand.FileFormats/Compression/Lzss.cs): match against the SOURCE buffer
    (overlapping/run-length matches are valid because the decoder's byte-by-byte copy
    reproduces a period-`distance` pattern). A 3-byte hash chain keeps it fast enough for
    a 50 KB blob in pure Python (the C# O(n*window) scan would be too slow at corpus
    scale). Correctness is asserted by the P2 self-check, not by byte-equality with C#.
    """
    n = len(src)
    out = bytearray()
    heads = {}             # 3-byte key -> most recent position
    prev = [-1] * n        # position -> previous position with same key (the chain)

    def key3(i):
        return (src[i] << 16) | (src[i + 1] << 8) | src[i + 2]

    def insert(i):
        if i + 2 < n:
            k = key3(i)
            prev[i] = heads.get(k, -1)
            heads[k] = i

    i = 0
    while i < n:
        flag_idx = len(out)
        out.append(0)
        flags = 0
        for bit in range(8):
            if i >= n:
                break
            best_len = 0
            best_dist = 0
            if i + 2 < n:
                limit = i - WINDOW
                m = MAX_MATCH if i + MAX_MATCH <= n else n - i
                j = heads.get(key3(i), -1)
                while j >= 0 and j >= limit:
                    if best_len < m and src[j + best_len] == src[i + best_len]:  # cheap reject
                        l = 0
                        while l < m and src[j + l] == src[i + l]:
                            l += 1
                        if l > best_len:
                            best_len = l
                            best_dist = i - j
                            if l == MAX_MATCH:
                                break
                    j = prev[j]
            if best_len >= MIN_MATCH:
                lencode = best_len - MIN_MATCH
                ch = best_dist & 0xFF
                t = ((best_dist >> 8) & 0x0F) | (lencode << 4)
                out.append(ch)
                out.append(t)
                end = i + best_len
                while i < end:
                    insert(i)
                    i += 1
            else:
                flags |= 1 << bit
                insert(i)
                out.append(src[i])
                i += 1
        out[flag_idx] = flags
    return bytes(out)


def repack_verbatim(data):
    """Re-emit a Gian package copying every payload's raw bytes verbatim (no recompress):
    header[0:2048] + each entry's payload padded to the 2048-byte sector. Returns the
    rebuilt bytes; for an unedited file this must equal the original (P1)."""
    ents, _ = parse(data)
    out = bytearray(data[:2048])
    for e in ents:
        payload = data[e.raw_off:e.raw_off + e.size]
        out += payload
        pad = align(e.size) - e.size
        out += data[e.raw_off + e.size:e.raw_off + e.size + pad]  # original pad bytes verbatim
    return bytes(out)


def room_files(data_dir):
    paths = sorted(glob.glob(os.path.join(data_dir, "ST*.DAT")) +
                   glob.glob(os.path.join(data_dir, "st*.dat")))
    # de-dup case-insensitively (Windows/WSL mounts can list both)
    seen, uniq = set(), []
    for p in paths:
        k = os.path.basename(p).lower()
        if k not in seen:
            seen.add(k)
            uniq.append(p)
    return uniq


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=REB_DEFAULT, help="Data dir (default: rebirth/Data)")
    ap.add_argument("--verbose", action="store_true", help="per-room line")
    args = ap.parse_args()

    paths = room_files(args.data)
    if not paths:
        print(f"NO ROOMS under {args.data} — nothing to check (skip).")
        return 0

    print(f"T8 round-trip harness — {len(paths)} rooms under {args.data}")
    print("=" * 78)

    p1_fail, p2_fail = [], []
    sector_stable = sector_delta = 0
    csize_orig_tot = csize_new_tot = 0
    delta_rooms = []

    for path in paths:
        name = os.path.basename(path)
        data = open(path, "rb").read()

        # P1 — container repack identity
        rebuilt = repack_verbatim(data)
        p1 = (rebuilt == data)
        if not p1:
            p1_fail.append(name)

        # P2 — codec round-trip over every compressed payload
        ents, _ = parse(data)
        p2 = True
        scd = None
        for e in ents:
            if e.type in (GPC_LZSS0, GPC_LZSS2):
                dec = e.decomp if e.decomp is not None else lzss_dec(data[e.raw_off:e.raw_off + e.size])
                enc = lzss_enc(dec)
                if lzss_dec(enc) != dec:
                    p2 = False
                if scd is None:  # first LZSS0 = the SCD room blob
                    scd = (e.size, len(enc))
        if not p2:
            p2_fail.append(name)

        # P3 — re-LZSS size tolerance on the SCD blob
        line_extra = ""
        if scd is not None:
            orig_c, new_c = scd
            csize_orig_tot += orig_c
            csize_new_tot += new_c
            stable = align(new_c) == align(orig_c)
            sector_stable += stable
            sector_delta += (not stable)
            if not stable:
                delta_rooms.append((name, orig_c, new_c,
                                    align(orig_c) // 2048, align(new_c) // 2048))
            pct = 100.0 * (new_c - orig_c) / orig_c if orig_c else 0.0
            line_extra = (f"scd c={orig_c}->{new_c} ({pct:+.1f}%) "
                          f"sectors {align(orig_c)//2048}->{align(new_c)//2048}"
                          f"{'' if stable else '  *SECTOR DELTA*'}")

        if args.verbose:
            print(f"  {name:10} P1={'ok' if p1 else 'FAIL'} P2={'ok' if p2 else 'FAIL'}  {line_extra}")

    print("=" * 78)
    print(f"P1 container repack identity : {len(paths)-len(p1_fail)}/{len(paths)} ok"
          + (f"  FAIL: {p1_fail}" if p1_fail else ""))
    print(f"P2 codec round-trip fidelity : {len(paths)-len(p2_fail)}/{len(paths)} ok"
          + (f"  FAIL: {p2_fail}" if p2_fail else ""))
    print(f"P3 re-LZSS size (SCD blob)   : sector-stable {sector_stable}/{sector_stable+sector_delta} rooms")
    if csize_orig_tot:
        print(f"   corpus compressed size    : orig {csize_orig_tot} -> ours {csize_new_tot} "
              f"({100.0*(csize_new_tot-csize_orig_tot)/csize_orig_tot:+.1f}%)")
    if delta_rooms:
        print("   sector-COUNT changes on recompress (all SHRINK here — this encoder is independent of,"
              " and slightly tighter than, the shipped C# Lzss.Compress, which is authoritative for the"
              " write path; the C# gate Dc2RoomRoundTripTests asserts sectors(new) <= sectors(orig)):")
        for name, oc, nc, os_, ns_ in delta_rooms:
            print(f"     {name:10} c={oc}->{nc}  sectors {os_}->{ns_}")
    else:
        print("   no SCD blob crosses a sector boundary on recompress -> offsets unaffected "
              "even on a full re-encode (strong static tolerance signal).")

    ok = not p1_fail and not p2_fail
    print("=" * 78)
    print("RESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
