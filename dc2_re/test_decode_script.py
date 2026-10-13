import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Regression tests for decode_script.py slot-5 routine discovery + item decode.

Pins two decoder fixes: (2026-06-27 p2) the init/entry routine at +0x1c must be
discovered (the old reader started at +0x20 and dropped it, hiding most AOT commits);
(2026-07-10, K81) the routine table is a SELF-BOUNDING DIRECTORY — entry i is the u32
at opbase+i*4, valid while i*4 < min(offset seen so far), no count field and no
ascending requirement. The interim init+ascending-array heuristic had a 20-entry cap
(truncating ST003/205/402/503/607) and grew a phantom routine on single-entry
directories (ST103, hiding its two doors).

These tests use the real rebirth room corpus. They are skipped if it is not present.
Run: python -m unittest tools.dc2_re.test_decode_script   (from repo root)
 or: python tools/dc2_re/test_decode_script.py
"""
import os, sys, struct, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DATA = os.environ.get(
    "DC2_DATA", os.path.join(REPO, "4249140_DinoCrisis2", "rebirth", "Data"))
os.environ.setdefault("DC2_DATA", DATA)
sys.path.insert(0, HERE)

import decode_script as ds  # noqa: E402
from dc2pkg import parse, GPC_LZSS0  # noqa: E402

HAVE_DATA = os.path.isdir(DATA) and os.path.isfile(os.path.join(DATA, "ST106.DAT"))


def blob(name):
    with open(os.path.join(DATA, name), "rb") as fh:
        d = fh.read()
    ents, _ = parse(d)
    lz = [e for e in ents if e.type == GPC_LZSS0]
    return lz[0].decomp if lz else None


def routine_offsets(name):
    """Routine start offsets relative to the slot-5 section start."""
    d = blob(name)
    start, size = ds.script_section(d)
    routs, _ = ds.routines(d, start, size)
    return [s - start for s, _ in routs], (d, start, size)


def items(name):
    d = blob(name)
    start, size = ds.script_section(d)
    return ds.item_commits(d, start, size), (d, start, size)


def take_items(name):
    d = blob(name)
    start, size = ds.script_section(d)
    return ds.take_item_commits(d, start, size), (d, start, size)


def scripted_gives(name):
    d = blob(name)
    start, size = ds.script_section(d)
    return ds.scripted_give_commits(d, start, size), (d, start, size)


def raw_enemy_preloads(d):
    """Every immediate-category enemy preload in the blob, by raw byte pattern:
    `push cat; push 0; op-0x23` = 05 00 CC 00 | 05 00 00 00 | 23 00 (CC = category 2..0xF).
    op-0x23 (0x489190) is LoadEnemyCategory (docs/dc2/EXE-SYMBOLS.md) — it makes the enemy's
    E-file (model + animation clips) resident. Returns [(blob_off_of_push, cat)]. Deliberately
    strict (immediate-cat form only, no false positives); the descriptor-ptr form of op-0x23 is
    out of scope for this regression."""
    out = []
    for i in range(len(d) - 10):
        if (d[i] == 5 and d[i + 1] == 0 and d[i + 3] == 0
                and d[i + 4:i + 9] == b"\x05\x00\x00\x00\x23" and d[i + 9] == 0
                and 2 <= d[i + 2] <= 0x0F):
            out.append((i, d[i + 2]))
    return out


def init_routine_range(d):
    """(start, end) absolute blob offsets of the INIT/entry routine — the one at
    off0 = table[+0x1c] (section+0x1c+off0). When off0 does not resolve to a routine start
    (the ST001 regression: off0 repointed to an appended stub), returns a degenerate empty
    range so the reachability check fails."""
    start, size = ds.script_section(d)
    off0 = struct.unpack_from("<I", d, start + 0x1c)[0]
    init = start + 0x1c + off0
    routs, _ = ds.routines(d, start, size)
    for s, e in routs:
        if s == init:
            return s, e
    return init, init


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class RoutineDiscovery(unittest.TestCase):
    def test_vm_directory_index_is_not_byte_sorted_ordinal(self):
        d = blob("ST002.DAT")
        start, size = ds.script_section(d)
        directory = ds.routine_directory(d, start, size)
        sorted_starts = [s for s, _e in ds.routines(d, start, size)[0]]
        self.assertEqual(directory[0] - start, 0x64)
        self.assertEqual(directory[1] - start, 0x44)
        self.assertEqual([s - start for s in sorted_starts[:2]], [0x44, 0x64])

    def test_duplicate_vm_directory_targets_are_censused(self):
        import glob
        duplicates = []
        for path in sorted(glob.glob(os.path.join(DATA, "ST*.DAT"))):
            d = blob(os.path.basename(path))
            section = ds.script_section(d)
            if not section:
                continue
            directory = ds.routine_directory(d, *section)
            for target in sorted(set(directory)):
                indices = [i for i, value in enumerate(directory) if value == target]
                if len(indices) > 1:
                    duplicates.append((os.path.basename(path), target - section[0], indices))
        self.assertEqual(duplicates, [],
                         "duplicate VM directory targets require an indices-list identity")

    def test_init_routine_included_when_in_order(self):
        # ST106 init offset 0x48 < array head 0x588 -> looks ascending but was still
        # dropped because the table read started at +0x20. Must now be present.
        offs, _ = routine_offsets("ST106.DAT")
        self.assertIn(0x48, offs, "ST106 init routine @+0x48 must be discovered")
        self.assertEqual(len(offs), 11, "ST106 has 11 routines (init + 10)")

    def test_init_routine_included_when_out_of_order(self):
        # ST002 table: init@+0x1c=0x48 sits AFTER array head @+0x20=0x28 in value, so
        # in raw table order the init is out of order. Offsets here are section-relative
        # (= opbase 0x1c + table value): init -> 0x1c+0x48 = 0x64; array head -> 0x44.
        # The fix must (a) include the init (0x64) and (b) keep the array head (0x44),
        # and (c) sort so the smaller (0x44) precedes the init (0x64).
        offs, _ = routine_offsets("ST002.DAT")
        self.assertIn(0x64, offs, "ST002 out-of-order init (table 0x48) must be discovered")
        self.assertIn(0x44, offs, "ST002 array head (table 0x28) must be kept")
        self.assertEqual(offs, sorted(offs), "routine starts must be sorted by offset")
        self.assertLess(offs.index(0x44), offs.index(0x64), "smaller routine sorts first")

    def test_single_routine_room_unaffected(self):
        # ST104 is a genuine 1-routine room (init only); the fix must not multiply it.
        offs, _ = routine_offsets("ST104.DAT")
        self.assertEqual(offs, [0x20], "ST104 is a single init routine @+0x20")

    def test_no_routine_overlap(self):
        # Sorted bounds must yield strictly non-overlapping, ordered ranges.
        d = blob("ST106.DAT")
        start, size = ds.script_section(d)
        routs, _ = ds.routines(d, start, size)
        for (s0, e0), (s1, _e1) in zip(routs, routs[1:]):
            self.assertLessEqual(e0, s1, "routine ranges must not overlap")

    def test_single_entry_directory_no_phantom_routine(self):
        # 2026-07-10 fix (self-bounding directory): ST103's directory is ONE entry
        # (init offset 4, script starts at +0x20). The old heuristic read the first
        # opcode word as a second table entry, splitting off a phantom routine at
        # +0x21 and truncating the real routine to 1 op (hiding ST103's two doors).
        offs, _ = routine_offsets("ST103.DAT")
        self.assertEqual(offs, [0x20], "ST103 is a single init routine @+0x20")

    def test_directory_not_capped_at_20_entries(self):
        # 2026-07-10 fix: the old reader stopped after 20 array entries, dropping the
        # tail routines of big rooms. ST402 has 30 routines; the tail holds 2 real
        # item commits (id4, id1).
        offs, _ = routine_offsets("ST402.DAT")
        self.assertEqual(len(offs), 30, "ST402 has 30 routines (was capped at 21)")
        self.assertIn(0x7924, offs, "ST402 last routine @+0x7924 must be discovered")


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class ItemDecode(unittest.TestCase):
    def test_sat1_is_a_physical_trigger_not_a_catalog_item(self):
        got = items("ST106.DAT")[0]
        self.assertTrue(got)
        for trigger in got:
            self.assertIn("thread", trigger)
            self.assertIn("routine", trigger)
            self.assertIn("routine_ordinal", trigger)
            self.assertIn("vm_directory_index", trigger)
            self.assertIn("vm_directory_indices", trigger)
            self.assertIn("routine_start", trigger)
            self.assertNotIn("id", trigger)
            self.assertNotIn("count", trigger)

    def test_st104_has_no_scripted_item(self):
        # ST104 genuinely scripts 0 items even after the fix (its live Med Pak S is a
        # stale record). Guards against a future over-eager fix inventing one.
        self.assertEqual(items("ST104.DAT")[0], [], "ST104 has no op-0x31 item")

    def test_sat1_operand_offsets_preserve_physical_identity(self):
        for room in ("ST106.DAT", "ST109.DAT", "ST402.DAT", "ST605.DAT"):
            its, (d, _s, _sz) = items(room)
            self.assertTrue(its, f"{room} should have items")
            for it in its:
                self.assertEqual(d[it["thread_operand"]["value_off"]], it["thread"])
                self.assertEqual(d[it["routine_operand"]["value_off"]], it["routine"])

    def test_generated_sat1_fixture_uses_true_vm_directory_identity(self):
        import gen_placements
        generated = gen_placements.gen_items()
        self.assertEqual(generated["schema_version"], 2)
        rows = [row for room in generated["rooms"].values() for row in room]
        self.assertEqual(len(rows), 168)
        self.assertTrue(all("routine_index" not in row for row in rows))
        self.assertTrue(all(
            f":r{row['vm_directory_index']}:op31@" in row["source_id"]
            for row in rows))

    def test_op35_take_item_contract(self):
        # SAT-5/op-0x35 is the catalog-bearing field pickup path. Its item literal is
        # independently editable; flag5/p3/cleanup are site-local and must stay pinned.
        import glob
        rows = []
        for p in glob.glob(os.path.join(DATA, "ST*.DAT")):
            rows.extend(take_items(os.path.basename(p))[0])
        self.assertEqual(len(rows), 71)
        self.assertEqual(sum(r["item_operand"]["mode"] == 0 for r in rows), 70)
        self.assertEqual(sum(r["item_operand"]["mode"] != 0 for r in rows), 1)
        for row in rows:
            self.assertEqual(row["kind"], 1)
            self.assertIn("flag5_operand", row)
            self.assertIn("p3_operand", row)
            self.assertIn("cleanup_secondary_operand", row)

    def test_op2c_kind1_is_the_only_direct_give_class(self):
        import glob
        rows = []
        for p in glob.glob(os.path.join(DATA, "ST*.DAT")):
            rows.extend(scripted_gives(os.path.basename(p))[0])
        self.assertEqual(len(rows), 47)
        self.assertEqual(sum(r["kind"] == 1 and 0 <= r["item_id"] <= 0x39 for r in rows), 5)
        self.assertEqual(sum(r["kind"] == 1 and not 0 <= r["item_id"] <= 0x39 for r in rows), 21)
        self.assertEqual(sum(r["kind"] != 1 for r in rows), 21)
        for row in rows:
            self.assertEqual(row["direct_give"], row["kind"] == 1)


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class DoorDecode(unittest.TestCase):
    def _doors(self, name):
        d = blob(name)
        start, size = ds.script_section(d)
        return ds.door_commits(d, start, size), d

    def test_dest_id_is_identity_st_id(self):
        # dest_id = f"{w:03X}" (dest word value == destination st_id). Anchored by the live
        # CreateFileA trace in DOOR-ROUTING-RE.md: ST101 -> ST102 (0x0102) + ST100 (0x0100);
        # ST102 -> ST101 (0x0101). (The old stage=low "ST101->ST201" was a byte-order bug.)
        ids = {dr["dest_id"] for dr in self._doors("ST101.DAT")[0] if dr["dest_mode"] == 0}
        self.assertIn("102", ids)
        self.assertIn("100", ids)
        self.assertIn("101", {dr["dest_id"] for dr in self._doors("ST102.DAT")[0] if dr["dest_mode"] == 0})

    def test_dest_byte_offset_is_editable_word(self):
        # The redirect lever: blob[dest_off] (LE u16) must equal dest_word for literal doors.
        for room in ("ST101.DAT", "ST102.DAT", "ST106.DAT"):
            doors, d = self._doors(room)
            for dr in doors:
                if dr["dest_mode"] == 0 and dr["dest_off"] is not None:
                    got = struct.unpack_from("<H", d, dr["dest_off"])[0]
                    self.assertEqual(got, dr["dest_word"] & 0xffff,
                                     f"{room}: blob[0x{dr['dest_off']:x}] must be the dest word")

    def test_st103_doors_recovered(self):
        # The doors hidden by the phantom-routine bug: ST103 -> ST105 + ST104, normal
        # usable doors (flags literal 2). Resolves the 104->103 / 105->103 one-way
        # asymmetry the old graph showed.
        doors, _ = self._doors("ST103.DAT")
        got = {(dr["dest_id"], dr["flags"]) for dr in doors if dr["dest_mode"] == 0}
        self.assertEqual(got, {("105", 2), ("104", 2)})

    def test_flags_and_type_literals(self):
        # T-B (2026-07-10): every op-0x30 commit carries mode-0 flags (block+0x44) and
        # door-type (block+0x08) literals. ST101's ST102 door type word 0x0107 is the
        # live-confirmed anchor (runtime record rec+0x1e = 07 01, DOOR-ROUTING-RE).
        doors, _ = self._doors("ST101.DAT")
        st102 = next(dr for dr in doors if dr["dest_id"] == "102")
        self.assertEqual(st102["door_type"] & 0xffff, 0x0107)
        self.assertEqual(st102["flags_mode"], 0)
        import glob as _g
        for p in _g.glob(os.path.join(DATA, "ST*.DAT")):
            for dr in self._doors(os.path.basename(p))[0]:
                self.assertEqual(dr["flags_mode"], 0, "flags must be a mode-0 literal")
                self.assertEqual(dr["door_type_mode"], 0, "door_type must be a mode-0 literal")
                self.assertIn(((dr["flags"] | 9) & 0xc), (0x8, 0xc))

    def test_dest_id_maps_to_real_rooms(self):
        # The identity encoding must connect every literal door to a room that exists on disk.
        import glob as _g
        paths = _g.glob(os.path.join(DATA, "ST*.DAT"))
        real = {os.path.basename(p)[2:].split(".")[0].upper()
                for p in paths if len(os.path.basename(p)[2:].split(".")[0]) == 3}
        bad = [(os.path.basename(p), dr["dest_id"]) for p in paths
               for dr in self._doors(os.path.basename(p))[0]
               if dr["dest_mode"] == 0 and dr["dest_id"] not in real]
        self.assertEqual(bad, [], "every literal door must map to a real room id")


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class CorpusRegression(unittest.TestCase):
    """Locks the corrected corpus totals on the current rebirth data."""

    @classmethod
    def setUpClass(cls):
        import glob
        cls.item_total = 0
        cls.door_rooms = set()
        cls.invalid_total = 0
        for p in sorted(glob.glob(os.path.join(DATA, "ST*.DAT"))):
            d = blob(os.path.basename(p))
            sec = ds.script_section(d)
            if not sec:
                continue
            start, size = sec
            cls.item_total += len(ds.item_commits(d, start, size))
            routs, _ = ds.routines(d, start, size)
            for s, e in routs:
                ops = ds.disasm(d, s, e)
                if any(x[1] == 0x30 for x in ops):
                    cls.door_rooms.add(os.path.basename(p))
                cls.invalid_total += sum(
                    1 for x in ops
                    if x[1] not in ds.LEN and x[1] not in (ds.RETURN, ds.JUMP)
                    and x[1] not in ds.NOP_OPS)

    def test_item_total(self):
        # 168 since the 2026-07-10 routine-directory fix (was 165; 25 pre-init-fix):
        # +3 items recovered from previously capped tail routines (ST003, ST402 x2).
        self.assertEqual(self.item_total, 168, "op-0x31 items across corpus")

    def test_doors_in_most_rooms(self):
        # 84 since the 2026-07-10 routine-directory fix: ST103's phantom-routine bug
        # hid its two doors (-> ST104/ST105), leaving it falsely door-less.
        self.assertEqual(len(self.door_rooms), 84, "rooms scripting op-0x30 doors")

    def test_no_new_invalid_ops(self):
        self.assertEqual(self.invalid_total, 5, "invalid ops must stay at the 5 pre-existing")


import json  # noqa: E402


class ConnectivityGraph(unittest.TestCase):
    """Locks data/dc2/door-connectivity.json (gen_connectivity.py) against drift."""

    @classmethod
    def setUpClass(cls):
        p = os.path.join(REPO, "data", "dc2", "door-connectivity.json")
        if not os.path.isfile(p):
            raise unittest.SkipTest("door-connectivity.json not generated")
        with open(p) as fh:
            cls.g = json.load(fh)
        rp = os.path.join(REPO, "data", "dc2", "rooms.json")
        with open(rp) as fh:
            cls.real = {r["id"] for r in json.load(fh)["rooms"]}

    def test_room_set_is_89_and_matches_sources(self):
        v = self.g["_validation"]
        self.assertTrue(v["expected_89"])
        self.assertTrue(v["matches_rooms_json"] and v["matches_stage_room_map"])
        self.assertEqual(len(self.g["rooms"]), 89)

    def test_every_outbound_dest_is_a_real_room(self):
        for rid, rec in self.g["rooms"].items():
            for e in rec["outbound"]:
                self.assertIn(e["dest"], self.real, f"{rid}->{e['dest']} not a real room")

    def test_bidirectional_flag_is_consistent(self):
        # e['bidirectional'] for a->b must equal "b lists a as an outbound dest".
        outs = {rid: {e["dest"] for e in rec["outbound"]}
                for rid, rec in self.g["rooms"].items()}
        for rid, rec in self.g["rooms"].items():
            for e in rec["outbound"]:
                self.assertEqual(e["bidirectional"], rid in outs.get(e["dest"], set()),
                                 f"{rid}->{e['dest']} bidirectional flag mismatch")

    def test_dead_ends_have_no_outbound(self):
        for rid in self.g["summary"]["dead_end_rooms_no_outbound"]:
            self.assertEqual(self.g["rooms"][rid]["out_degree"], 0)


class RoomData(unittest.TestCase):
    """Locks data/dc2/room-data.json (gen_room_data.py) — the consolidated per-room DB."""

    @classmethod
    def setUpClass(cls):
        p = os.path.join(REPO, "data", "dc2", "room-data.json")
        if not os.path.isfile(p):
            raise unittest.SkipTest("room-data.json not generated")
        with open(p, encoding="utf-8") as fh:
            cls.d = json.load(fh)

    def test_89_rooms_with_canonical_keys(self):
        self.assertEqual(len(self.d["rooms"]), 89)
        need = {"id", "file", "stage", "room", "zone", "spawns", "doors",
                "connectivity", "items", "enemies_runtime"}
        for rid, r in self.d["rooms"].items():
            self.assertTrue(need <= set(r), f"{rid} missing fields")

    def test_rollup_counts_match_sources(self):
        # totals updated by the 2026-07-10 routine-directory fix (+3 items, +4 spawns,
        # +ST103 as a door room).
        c = self.d["_counts"]
        self.assertEqual(c["rooms"], 89)
        self.assertEqual(c["total_items"], 168)
        self.assertEqual(c["total_spawns"], 441)
        self.assertEqual(c["rooms_with_doors"], 84)

    def test_zone_catalog_is_the_10_names(self):
        cat = self.d["_zone_catalog"]
        self.assertEqual(len(cat), 10)
        self.assertIn("Jungle", {z["zone_en"] for z in cat})

    def test_all_89_rooms_have_a_zone(self):
        self.assertEqual(self.d["_counts"]["zones_resolved"], 89)
        for rid, r in self.d["rooms"].items():
            self.assertTrue(r["zone"], f"{rid} missing zone")
            self.assertIn(r["zone_area_id"], range(0, 11))

    def test_st4_is_the_split_stage(self):
        # area_id 0x491c70: ST4 rooms 0-6,11 = Missile Silo; rooms 7-10 = Habitat.
        z = {rid: r["zone"] for rid, r in self.d["rooms"].items() if r["stage"] == 4}
        self.assertEqual(z["402"], "Missile Silo")          # room 2
        self.assertEqual(z["407"], "Habitat Support Facility")  # room 7
        self.assertEqual(z["40A"], "Habitat Support Facility")  # room 10
        self.assertEqual(z["40B"], "Missile Silo")          # room 11 -> falls back

    def test_zone_backfill_agrees_across_files(self):
        # rooms.json + stage-room-map.json must carry the SAME byte-derived zone as
        # room-data.json (backfill_zones.py / zone_of). Locks the three consistent.
        from gen_room_data import zone_of
        with open(os.path.join(REPO, "data", "dc2", "rooms.json"), encoding="utf-8") as fh:
            rj = json.load(fh)["rooms"]
        with open(os.path.join(REPO, "data", "dc2", "stage-room-map.json"), encoding="utf-8") as fh:
            srm = json.load(fh)["rooms"]
        for r in rj:
            self.assertEqual(r.get("zone"), zone_of(r["stage"], r["room"])[1],
                             f"rooms.json {r['id']} zone drift")
        for r in srm:
            self.assertEqual(r.get("zone"), zone_of(r["stage"], r["room"])[1],
                             f"stage-room-map.json {r['st_id']} zone drift")

    def test_map_json_st_ids_partition_89(self):
        # map.json each zone's st_ids must be byte-derived and together partition the 89.
        from gen_room_data import zone_of
        with open(os.path.join(REPO, "data", "dc2", "map.json"), encoding="utf-8") as fh:
            mp = json.load(fh)
        seen = []
        rid_stage = {r["id"]: (r["stage"], r["room"])
                     for r in self.d["rooms"].values()}
        for z in mp["zones"]:
            for rid in z["st_ids"]:
                seen.append(rid)
                self.assertEqual(zone_of(*rid_stage[rid])[1], z["zone_en"],
                                 f"map.json {rid} in wrong zone {z['zone_en']}")
        self.assertEqual(len(seen), 89)
        self.assertEqual(len(set(seen)), 89, "map.json st_ids must be a clean partition")

    def test_zone_counts_match_exe_derivation(self):
        # The 4 byte-exact zones (also exact vs the XGameMania guide) — locks the
        # area-id -> zone-name array order against drift.
        c = self.d["_zone_room_counts"]
        self.assertEqual(c["Military Facility"], 6)
        self.assertEqual(c["Research Facility"], 7)
        self.assertEqual(c["3rd Energy Facility"], 9)
        self.assertEqual(c["Missile Silo"], 8)
        self.assertEqual(sum(c.values()), 89)


EXE = os.path.join(REPO, "4249140_DinoCrisis2", "rebirth", "Dino2.exe")
try:
    from exe_xref import PE  # noqa: E402
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_OP_MEM  # noqa: E402
    HAVE_EXE = os.path.isfile(EXE)
except Exception:
    HAVE_EXE = False


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class ScdVmExpr(unittest.TestCase):
    """Pins the op-0x03 (ALU/expression) decode against the corpus: operator=byte[IP+1]
    must always index the 26-entry table (0..0x19) — a misdecode would surface operators
    >0x19. Also locks the dominant stack-ptr ('pop N') form."""

    def test_op03_operators_in_table_range(self):
        import glob
        stackform = total = 0
        for p in sorted(glob.glob(os.path.join(DATA, "ST*.DAT"))):
            d = blob(os.path.basename(p))
            sec = ds.script_section(d)
            if not sec:
                continue
            start, size = sec
            routs, _ = ds.routines(d, start, size)
            for s, e in routs:
                for off, op, raw in ds.disasm(d, s, e):
                    if op != 0x03 or len(raw) < 6:
                        continue
                    total += 1
                    self.assertLessEqual(raw[1], 0x19,
                                         f"op-0x03 operator 0x{raw[1]:x} out of ALU table (0..0x19)")
                    if raw[2] == 0xff:
                        stackform += 1
        self.assertGreater(total, 30000, "op-0x03 is the most common non-push opcode")
        self.assertGreater(stackform / total, 0.85, "majority of op-0x03 is the slot==0xff stack-ptr form")


@unittest.skipUnless(HAVE_EXE, "Dino2.exe / capstone not available")
class ScdVmStackModelExe(unittest.TestCase):
    """Pins the static decode against Dino2.exe: the ALU operator table and the EXACT set
    of opcodes that write the operand-stack pointer [entity+0x84]."""

    @classmethod
    def setUpClass(cls):
        with open(EXE, "rb") as fh:
            cls.exe = fh.read()
        cls.pe = PE(cls.exe)
        cls.md = Cs(CS_ARCH_X86, CS_MODE_32)
        cls.md.detail = True

    def _mnem(self, va, n=40):
        f = self.pe.va2fo(va)
        out = []
        for ins in self.md.disasm(self.exe[f:f + n], va):
            out.append(ins.mnemonic)
            if ins.mnemonic in ("ret", "retn"):
                break
        return out

    def test_alu_table_operators(self):
        import struct as _s
        base = 0x48bc20
        fo = self.pe.va2fo(base)
        tgt = [_s.unpack_from("<I", self.exe, fo + i * 4)[0] for i in range(26)]
        # spot-check the arithmetic/logic core by the defining instruction in each stub
        want = {0: "add", 1: "sub", 2: "imul", 5: "or", 6: "and", 7: "xor", 8: "not"}
        for op, mn in want.items():
            self.assertIn(mn, self._mnem(tgt[op]),
                          f"ALU operator 0x{op:02x} stub must contain '{mn}'")
        # comparators end in set*  (GT/LT/GE/LE/EQ/NE)
        for op in (0x0d, 0x0e, 0x0f, 0x10, 0x11, 0x12):
            self.assertTrue(any(m.startswith("set") for m in self._mnem(tgt[op])),
                            f"ALU operator 0x{op:02x} must be a comparator (set*)")

    def test_exact_stack_pointer_writers(self):
        # Exactly these six opcodes may write [reg+0x84] (the operand-stack ptr).
        import json as _j
        with open(os.path.join(REPO, "data", "dc2", "scd-vm-opcodes.json")) as fh:
            ops = _j.load(fh)["opcodes"]
        writers = set()
        for o in ops:
            if o.get("nop"):
                continue
            va = int(o["handler"], 16)
            f = self.pe.va2fo(va)
            for ins in self.md.disasm(self.exe[f:f + 600], va):
                if ins.mnemonic in ("ret", "retn"):
                    break
                if ins.mnemonic == "mov" and ins.operands and ins.operands[0].type == CS_OP_MEM \
                        and ins.operands[0].mem.disp == 0x84 and ins.operands[0].mem.base != 0:
                    writers.add(o["op"])
        self.assertEqual(writers, {0x03, 0x04, 0x05, 0x06, 0x08, 0x19},
                         "only these 6 opcodes may move the operand-stack pointer")


@unittest.skipUnless(HAVE_DATA, "rebirth room corpus not available")
class InitEnemyPreloadPreserved(unittest.TestCase):
    """A room's op-0x23 enemy preload must stay reachable from the INIT routine (off0 @
    section+0x1c). Regression for the ST001 on-entry crash: an edit repointed off0 to a scene-cut
    stub, orphaning its `LoadEnemyCategory(0x0c)` preload, so the 0x0c wave enemy spawned with no
    resident animation set -> NULL-clip AV at SetAnimClip 0x48DE55 (params [0,5], 100% on entry).
    Full RCA: docs/dc2/DC2-ST001-PRELOAD-STRIP-CRASH-RCA.md. Keeping off0 pointing at the routine
    that runs the preload is the fix."""

    def test_st001_init_preloads_enemy_category_0c(self):
        # ST001's native enemy is wave-descriptor TYPE 0x0c (NOT 0x08/Allosaurus); its init must
        # preload category 0x0c so the spawned wave enemy has resident anim.
        d = blob("ST001.DAT")
        cats = [c for _off, c in raw_enemy_preloads(d)]
        self.assertIn(0x0C, cats,
                      "ST001 must carry a LoadEnemyCategory(0x0c) preload (native enemy = 0x0c)")
        s, e = init_routine_range(d)
        for off, cat in raw_enemy_preloads(d):
            self.assertTrue(s <= off < e,
                            f"ST001 op-0x23 preload of cat 0x{cat:x} @0x{off:x} is orphaned from the "
                            f"init routine [0x{s:x},0x{e:x}) -> on-entry NULL-clip crash. off0 must "
                            f"reach the preload (keep it at the pristine init routine).")

    def test_every_room_init_reaches_its_enemy_preloads(self):
        # Corpus-wide, data-driven: NO room may leave an immediate-cat enemy preload unreachable
        # from its init routine. Covers all 14 rooms that use this preload form, across the
        # cutscene-flagged / cutscene-uncertain / non-cutscene tiers alike (not cutscene-scoped).
        import glob
        for p in sorted(glob.glob(os.path.join(DATA, "ST*.DAT"))):
            name = os.path.basename(p)
            d = blob(name)
            if d is None:
                continue
            preloads = raw_enemy_preloads(d)
            if not preloads:
                continue
            s, e = init_routine_range(d)
            for off, cat in preloads:
                self.assertTrue(s <= off < e,
                                f"{name}: op-0x23 preload cat 0x{cat:x} @0x{off:x} not reachable "
                                f"from init [0x{s:x},0x{e:x}) -> would crash on room entry")


@unittest.skipUnless(os.path.isfile(os.path.join(REPO, "data", "dc2", "door-gating.json")), "DinoRand authored door-gating fixture not available")
class GatingModelInvariant(unittest.TestCase):
    """K107 negative-verdict guard: DC2 has NO offline-authorable key-item door gate.
    Reads the COMMITTED data/dc2/door-gating.json (no room corpus needed, runs on CI),
    and pins the two structural facts that make requiresItems/requiresRooms empty, so a
    future accidental fabrication (Law 2 violation) fails here. If a real held-key-item or
    cross-room-producer door gate is ever DECODED, update this test alongside the finding."""

    @classmethod
    def setUpClass(cls):
        import json
        with open(os.path.join(REPO, "data", "dc2", "door-gating.json")) as fh:
            cls.dg = json.load(fh)

    def _setter_rooms(self, grp, fid, val=1):
        return {rid for rid, routs in self.dg["rooms"].items()
                for f in routs for g, i, v in f["sets"] if (g, i, v) == (grp, fid, val)}

    def test_flag_7_16_is_ubiquitous_not_a_producer(self):
        # flag(7,16) guards the 101<->102 door-unlock routine; it is an engine "in-game"
        # latch set in many rooms, NOT a progression producer -> requiresRooms must not
        # treat it as one (gen_ap_logic._dc2_unlock_rooms drops flags with >1 setter room).
        self.assertGreater(len(self._setter_rooms(7, 16)), 1,
                           "flag(7,16) must stay ubiquitous (multi-room) so it is not a producer")

    def test_no_locked_door_unlock_guard_tests_a_held_key_item(self):
        # No locked door's script_set unlock routine is guarded by a group-9 (held key-item)
        # test -> there is no traceable held-key-item -> door gate (Law 2). Doors unlock via
        # equip-class (op-0x3a key_use) or story flags (groups 6/7).
        for edge, rec in self.dg["locked_doors"].items():
            for u in rec["unlocked_by"]:
                routs = self.dg["rooms"][u["room"]]
                guards = routs[u["routine"]]["tests"] if u["kind"] == "script_set" else []
                self.assertFalse(any(g == 9 and i < 0x40 for g, i in guards),
                                 f"{edge}: unlock guarded by a held key-item test {guards} "
                                 "-- a real requiresItems gate; update K107 if intentional")


if __name__ == "__main__":
    unittest.main(verbosity=2)
