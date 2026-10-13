import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Contract tests for the normalized DC2 item-source fixture."""
import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

import gen_item_sources as gen  # noqa: E402


@unittest.skipUnless(os.path.isfile(os.path.join(gen.DATA, "ST106.DAT")),
                     "rebirth room corpus not available")
class ItemSourceGeneration(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = gen.generate()
        cls.rows = cls.fixture["sources"]

    def test_typed_corpus_counts(self):
        stats = self.fixture["stats"]
        self.assertEqual(stats["sat1_trigger"], 168)
        self.assertEqual(stats["op35_take"], 71)
        self.assertEqual(stats["op2c_give"], 5)
        self.assertEqual(stats["op2c_unsupported"], 42)

    def test_every_writer_eligible_source_has_one_ap_disposition(self):
        eligible = [row for row in self.rows if row.get("eligible_item_rewrite")]
        self.assertEqual(len(eligible), 51)
        for row in eligible:
            availability = row["ap_availability"]
            self.assertIn(availability["disposition"],
                          ("unconditional", "modelable", "excluded"))
            self.assertTrue(availability["evidence"])
            self.assertIn("invocation", availability)
        self.assertEqual(self.fixture["stats"]["ap_dispositions"],
                         {"unconditional": 41, "excluded": 8, "modelable": 2})

    def test_e1_modelable_and_excluded_source_ids_are_pinned(self):
        by_disposition = {}
        for row in self.rows:
            if "ap_availability" in row:
                by_disposition.setdefault(row["ap_availability"]["disposition"], set()).add(
                    row["source_id"])
        self.assertEqual(by_disposition["modelable"], {
            "ST304:r0:op35@0x7d7c",
            "ST705:r0:op35@0x5892",
        })
        self.assertEqual(by_disposition["excluded"], {
            "ST203:r0:op35@0x2a5e",
            "ST205:r13:op35@0xfe9a",
            "ST205:r23:op35@0x10d38",
            "ST304:r20:op2c@0x9b9a",
            "ST401:r2:op2c@0x62b2",
            "ST603:r3:op35@0x4762",
            "ST607:r20:op35@0xe914",
            "ST808:r2:op2c@0xe566",
        })

    def test_direct_guard_spans_are_explicit(self):
        eligible = [row for row in self.rows if row.get("eligible_item_rewrite")]
        guarded = [row for row in eligible if row["ap_availability"]["direct_guard_spans"]]
        self.assertEqual(len(guarded), 5)
        for row in guarded:
            for span in row["ap_availability"]["direct_guard_spans"]:
                self.assertIn("op07_off", span)
                self.assertIn("span_end", span)
                self.assertIn("classification", span)

    def test_provenance_separates_ordinal_from_vm_directory_index(self):
        for row in self.rows:
            provenance = row.get("provenance", {})
            if "room" not in provenance:
                continue
            self.assertIn("routine_ordinal", provenance)
            self.assertIn("vm_directory_index", provenance)
            self.assertIn("vm_directory_indices", provenance)
            self.assertIn("routine_start", provenance)
            self.assertNotIn("routine_index", provenance)
        st102 = next(row for row in self.rows
                     if row["provenance"].get("room") == "ST102"
                     and row["provenance"].get("op_blob_off") == 0xb082)
        self.assertEqual(st102["provenance"]["routine_ordinal"], 1)
        self.assertEqual(st102["provenance"]["vm_directory_index"], 0)
        self.assertEqual(st102["source_id"], "ST102:r0:op35@0xb082")

    def test_modelable_predicates_are_monotonic_only(self):
        allowed = {"story_flag_set", "character", "subweapon"}
        for row in self.rows:
            availability = row.get("ap_availability")
            if availability and availability["disposition"] == "modelable":
                self.assertTrue(availability["requirements"])
                self.assertTrue(all(req["kind"] in allowed
                                    for req in availability["requirements"]))

    def test_stable_source_identity_is_unique(self):
        ids = [row["source_id"] for row in self.rows]
        self.assertEqual(len(ids), len(set(ids)))

    def test_only_safe_literal_catalog_sources_are_rewriteable(self):
        eligible = [row for row in self.rows if row.get("eligible_item_rewrite")]
        self.assertTrue(eligible)
        for row in eligible:
            self.assertIn(row["source_type"], ("op35_take", "op2c_give"))
            self.assertEqual(row["item_operand"]["mode"], 0)
            self.assertIn(row["catalog_id"], self.fixture["catalog_ids"])
        self.assertFalse(any(row.get("catalog_id") == "0x1e" for row in eligible))

    def test_unsupported_records_remain_explicit(self):
        reasons = {row.get("reason") for row in self.rows if row["source_type"] == "unsupported"}
        self.assertIn("dynamic_item_operand", reasons)
        self.assertIn("catalog_id_out_of_range", reasons)
        self.assertIn("op2c_non_grant_kind", reasons)

    def test_every_key_has_a_classified_source(self):
        key_ids = {
            f"0x{value:02x}" for value in range(0x21, 0x35)
        }
        classified = {
            row["catalog_id"] for row in self.rows
            if row.get("catalog_id") in key_ids
        }
        self.assertEqual(classified, key_ids)

    def test_gas_mask_restore_sites_are_regrants(self):
        regrants = {
            row["source_id"] for row in self.rows
            if row["source_type"] == "group9_regrant"
        }
        self.assertEqual(regrants, {
            "ST10F:r2:op1c@0x60ec",
            "ST10F:r3:op1c@0x6176",
        })

    def test_catalog_identity_and_key_contract_are_unique(self):
        catalog = gen._catalog()
        self.assertEqual(sorted(catalog), list(range(0x3a)))
        with open(gen.CATALOG, encoding="utf-8") as fh:
            raw = json.load(fh)
        ids = [row["id"] for row in raw["catalog"]]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual(raw["keyItemIds"], [f"0x{value:02x}" for value in range(0x21, 0x35)])

    def test_rewrite_classes_pin_quantity_and_special_key_isolation(self):
        eligible = [row for row in self.rows if row.get("eligible_item_rewrite")]
        for row in eligible:
            item_id = int(row["catalog_id"], 16)
            if 0x1a <= item_id <= 0x1f:
                self.assertEqual(row["rewrite_class"], "health")
                self.assertEqual(row["grant_quantity"], 1)
            elif item_id == 0x2f:
                self.assertEqual(row["rewrite_class"], "special_key_2f")
                self.assertNotEqual(row["rewrite_class"], "generic_key")
            else:
                self.assertEqual(row["rewrite_class"], "generic_key")


@unittest.skipUnless(os.path.isfile(os.path.join(REPO, "data", "dc2", "item-sources.json")), "DinoRand authored item-source fixture not available")
class CommittedFixture(unittest.TestCase):
    def test_committed_fixture_matches_generator(self):
        path = os.path.join(REPO, "data", "dc2", "item-sources.json")
        with open(path, encoding="utf-8") as fh:
            self.assertEqual(json.load(fh), gen.generate())


if __name__ == "__main__":
    unittest.main(verbosity=2)
