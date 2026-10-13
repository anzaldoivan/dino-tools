import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Generate the normalized DC2 item/acquisition-source fixture.

The room corpus is read only and pristine `.dinorand-bak` siblings are preferred.
Run with --apply to update data/dc2/item-sources.json or --check in validation.
"""
import argparse
import glob
import json
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import decode_script as ds  # noqa: E402

DATA = game_path("DC2", "rebirth/Data")
OUT = os.path.join(REPO, "data", "dc2", "item-sources.json")
CATALOG = os.path.join(REPO, "data", "dc2", "items.json")
GUARDS = os.path.join(REPO, "data", "dc2", "door-guards.json")

# K122: ST10F clears Gas Mask ownership on entry, then either physical pickup
# routine restores it and commits the corresponding exit door.
GROUP9_REGRANTS = {
    ("ST10F", 2, 0x1c, 0x60ec): "ST10F:r1:op1c@0x60c2",
    ("ST10F", 3, 0x1c, 0x6176): "ST10F:r1:op1c@0x60c2",
}


def _load_blob(path):
    pristine = path + ".dinorand-bak"
    if os.path.isfile(pristine):
        path = pristine
    with open(path, "rb") as fh:
        package = fh.read()
    entries, _ = ds.parse(package)
    lz = [entry for entry in entries if entry.type == ds.GPC_LZSS0]
    return lz[0].decomp if lz else None


def _catalog():
    with open(CATALOG, encoding="utf-8") as fh:
        value = json.load(fh)
    rows = {int(row["id"], 16): row for row in value["catalog"]}
    return rows


def _hex_id(value):
    return f"0x{value & 0xffff:02x}" if value is not None else None


def _rewrite_class(item):
    if item is None:
        return None
    if 0x1a <= item <= 0x1f:
        return "health"
    if item == 0x2f:
        return "special_key_2f"
    if 0x21 <= item <= 0x2e or 0x30 <= item <= 0x34:
        return "generic_key"
    return None


def _room_source_id(room, vm_directory_index, opcode, offset):
    return f"{room}:r{vm_directory_index}:op{opcode:02x}@0x{offset:x}"


def _room_provenance(room, row, opcode):
    return {"room": room, "routine_ordinal": row["routine_ordinal"],
            "vm_directory_index": row["vm_directory_index"],
            "vm_directory_indices": row["vm_directory_indices"],
            "routine_start": row["routine_start"], "opcode": f"0x{opcode:02x}",
            "op_blob_off": row["op_off"]}


def _guard_authority():
    with open(GUARDS, encoding="utf-8") as fh:
        value = json.load(fh)
    expressions = {}
    for section in ("item_guards", "door_guards"):
        for record in value[section]:
            for guard in record["guards"]:
                expressions.setdefault((record["room"], int(guard["op07_off"], 16)),
                                       guard.get("expr"))
    starts = {(record["room"], int(record["op15_off"], 16)): record["guards"]
              for record in value["conditional_routine_starts"]}
    return expressions, starts


def _structural_spans(blob, routine_start, routine_end, target_off, room, expressions):
    result = []
    room_id = room[2:]
    for off, op, raw in ds.disasm(blob, routine_start, routine_end):
        if op != 0x07:
            continue
        span_end = off + struct.unpack_from("<h", raw, 2)[0]
        if off < target_off < span_end:
            expression = expressions.get((room_id, off))
            result.append({"op07_off": off, "span_end": span_end, "arg": raw[1],
                           "expression": expression})
    return result


def _classify_expression(expression, self_flag5=None):
    if expression is None:
        return {"classification": "unknown", "excluded": "guard_expression_unpublished"}
    simple = re.fullmatch(r"\(?flag\(6,(\d+)\)\)?", expression)
    if simple:
        return {"classification": "monotonic_story",
                "requirement": {"kind": "story_flag_set", "group": 6,
                                "id": int(simple.group(1))}}
    taken = re.fullmatch(r"\(?!\(flag\(5,(\d+)\)\)\)?", expression)
    if taken:
        flag_id = int(taken.group(1))
        if self_flag5 is None or self_flag5 == flag_id:
            return {"classification": "self_taken_clear", "lifecycle_flag5": flag_id}
        return {"classification": "negated_flag",
                "excluded": "negated_or_nonmonotonic_predicate"}
    char = re.search(r"CHARACTER\(\+0x108F\)\s*==\s*(\d+)", expression)
    if char and "!" not in expression and "&&" not in expression and "||" not in expression:
        return {"classification": "character",
                "requirement": {"kind": "character", "value": int(char.group(1))}}
    sub = re.search(r"SUBWEAPON[^=]*==\s*(\d+)", expression, re.IGNORECASE)
    if sub and "!" not in expression and "&&" not in expression and "||" not in expression:
        return {"classification": "subweapon",
                "requirement": {"kind": "subweapon", "value": int(sub.group(1))}}
    return {"classification": "unsupported_predicate",
            "excluded": "negated_nonmonotonic_or_compound_predicate"}


def _classify_start_guard(guard):
    pred = guard.get("pred", {})
    polarity = guard.get("polarity")
    if pred.get("kind") == "flag" and pred.get("group") == 6 and polarity == "iff_set":
        return {"classification": "monotonic_story",
                "requirement": {"kind": "story_flag_set", "group": 6, "id": pred["id"]}}
    return {"classification": "unsupported_start_predicate",
            "excluded": "conditional_start_not_monotonic"}


def _unique_requirements(values):
    tuples = {tuple(sorted(value.items())) for value in values}
    return [dict(items) for items in sorted(tuples, key=repr)]


def _constraint_summary(spans, start_guards=(), self_flag5=None):
    requirements = []
    lifecycle = []
    exclusions = []
    evidence = []
    for span in spans:
        classification = _classify_expression(span["expression"], self_flag5)
        span.update({k: v for k, v in classification.items() if k != "requirement"})
        if "requirement" in classification:
            requirements.append(classification["requirement"])
        if "lifecycle_flag5" in classification:
            lifecycle.append(classification["lifecycle_flag5"])
        if "excluded" in classification:
            exclusions.append(classification["excluded"])
        evidence.append(f"op07@0x{span['op07_off']:x}->0x{span['span_end']:x}:"
                        f"{span['classification']}")
    for guard in start_guards:
        classification = _classify_start_guard(guard)
        if "requirement" in classification:
            requirements.append(classification["requirement"])
        if "excluded" in classification:
            exclusions.append(classification["excluded"])
        evidence.append("propagated-start:" + classification["classification"])
    unique_requirements = _unique_requirements(requirements)
    return {"requirements": unique_requirements, "lifecycle_flag5": sorted(set(lifecycle)),
            "exclusions": sorted(set(exclusions)), "evidence": evidence, "spans": spans}


def _room_invocation_model(room, blob, start, size, expressions, start_guard_index):
    ranges, _ = ds.routines(blob, start, size)
    directory = ds.routine_directory(blob, start, size)
    start_to_indices = {}
    for index, routine_start in enumerate(directory):
        start_to_indices.setdefault(routine_start, []).append(index)
    edges = []
    for routine_ordinal, (routine_start, routine_end) in enumerate(ranges):
        indices = start_to_indices.get(routine_start, [])
        if len(indices) != 1:
            continue
        caller = indices[0]
        ops = ds.disasm(blob, routine_start, routine_end)
        idxof = {value[0]: index for index, value in enumerate(ops)}
        for off, op, raw in ops:
            target = mode = None
            kind = None
            if op == 0x08:
                target = struct.unpack_from("<H", raw, 2)[0]
                kind = "gosub"
            elif op in (0x15, 0x19, 0x22, 0x31, 0x39):
                operands, _ = ds.block_from_pushrun(ops, idxof[off])
                target, mode = operands.get(0x00, (None, None))
                if mode in (1, 2, 6):
                    target = None
                kind = {0x15: "start", 0x19: "restart", 0x22: "vm_switch",
                        0x31: "sat1_interaction", 0x39: "sat9_interaction"}[op]
            if target is None or not (0 <= target < len(directory)):
                continue
            spans = _structural_spans(blob, routine_start, routine_end, off, room, expressions)
            propagated = start_guard_index.get((room[2:], off), []) if op in (0x15, 0x19) else []
            constraints = _constraint_summary(spans, propagated)
            edges.append({"caller": caller, "target": target, "kind": kind,
                          "op_off": off, "constraints": constraints})

    states = {0: {"disposition": "unconditional", "class": "on_entry",
                  "requirements": [], "lifecycle_flag5": [], "exclusions": [],
                  "evidence": ["room-load bootstrap starts VM directory index 0"]}}
    rank = {"excluded": 0, "modelable": 1, "unconditional": 2}
    changed = True
    while changed:
        changed = False
        for edge in edges:
            caller = states.get(edge["caller"])
            if caller is None:
                continue
            constraints = edge["constraints"]
            exclusions = sorted(set(caller["exclusions"] + constraints["exclusions"]))
            requirements = _unique_requirements(
                caller["requirements"] + constraints["requirements"])
            lifecycle = sorted(set(caller["lifecycle_flag5"] + constraints["lifecycle_flag5"]))
            disposition = ("excluded" if exclusions else
                           "modelable" if requirements else "unconditional")
            candidate = {"disposition": disposition,
                         "class": "interaction" if "interaction" in edge["kind"] else "event",
                         "requirements": requirements, "lifecycle_flag5": lifecycle,
                         "exclusions": exclusions,
                         "evidence": caller["evidence"] +
                                     [f"{edge['kind']}@0x{edge['op_off']:x} r{edge['caller']}->r{edge['target']}"] +
                                     constraints["evidence"]}
            old = states.get(edge["target"])
            if old is None or rank[candidate["disposition"]] > rank[old["disposition"]]:
                states[edge["target"]] = candidate
                changed = True
    for index in range(len(directory)):
        states.setdefault(index, {"disposition": "excluded", "class": "no_start",
                                  "requirements": [], "lifecycle_flag5": [],
                                  "exclusions": ["no_proven_invocation_path"],
                                  "evidence": ["fixpoint found no path from VM directory index 0"]})
    return {"states": states, "ranges": ranges, "directory": directory,
            "start_to_indices": start_to_indices}


def _sat1(room, row):
    return {
        "source_id": _room_source_id(room, row["vm_directory_index"], 0x31, row["op_off"]),
        "source_type": "sat1_trigger",
        "provenance": _room_provenance(room, row, 0x31),
        "thread": row["thread"], "routine": row["routine"], "slot": row["slot"],
        "thread_operand": row["thread_operand"],
        "routine_operand": row["routine_operand"],
        "slot_operand": row["slot_operand"], "pos": row["pos"],
        "eligible_item_rewrite": False,
        "reason": "physical_vm_trigger_not_catalog_item",
    }


def _attach_ap_availability(row, room_model, blob, room, expressions):
    provenance = row["provenance"]
    vm_index = provenance["vm_directory_index"]
    invocation = room_model["states"].get(vm_index)
    routine_start = provenance["routine_start"]
    routine_end = next(end for start, end in room_model["ranges"] if start == routine_start)
    direct_spans = _structural_spans(blob, routine_start, routine_end,
                                     provenance["op_blob_off"], room, expressions)
    direct = _constraint_summary(direct_spans, self_flag5=row.get("flag5"))
    evidence = list(invocation["evidence"])
    evidence.extend(direct["evidence"] or ["no direct containing op07 span"])
    exclusions = sorted(set(invocation["exclusions"] + direct["exclusions"]))
    lifecycle = sorted(set(invocation["lifecycle_flag5"] + direct["lifecycle_flag5"]))
    if lifecycle and (row.get("flag5") is None or any(value != row["flag5"] for value in lifecycle)):
        exclusions.append("invocation_lifecycle_flag_does_not_match_source_flag5")
    requirements = _unique_requirements(invocation["requirements"] + direct["requirements"])
    disposition = ("excluded" if exclusions else
                   "modelable" if requirements else "unconditional")
    row["ap_availability"] = {
        "disposition": disposition,
        "requirements": requirements,
        "reason": (sorted(set(exclusions))[0] if exclusions else
                   "proven_monotonic_requirements" if requirements else
                   "proven_available_without_progression_predicate"),
        "invocation": {"class": invocation["class"],
                       "disposition": invocation["disposition"],
                       "lifecycle_flag5": invocation["lifecycle_flag5"],
                       "evidence": invocation["evidence"]},
        "direct_guard_spans": direct["spans"],
        "evidence": evidence,
    }


def _op35(room, row, catalog):
    item = row["item_id"]
    literal = row["item_operand"]["mode"] == 0
    in_catalog = item in catalog if item is not None else False
    if not literal:
        source_type, reason = "unsupported", "dynamic_item_operand"
    elif not in_catalog:
        source_type, reason = "unsupported", "catalog_id_out_of_range"
    elif item == 0x1e:
        source_type, reason = "op35_take", "cross_difficulty_remap_0x1e_to_0x1d"
    else:
        source_type, reason = "op35_take", None
    result = {
        "source_id": _room_source_id(room, row["vm_directory_index"], 0x35, row["op_off"]),
        "source_type": source_type, "record_class": "op35_take",
        "provenance": _room_provenance(room, row, 0x35),
        "catalog_id": _hex_id(item) if in_catalog else None,
        "raw_item_value": item, "item_operand": row["item_operand"],
        "kind": 1, "p3": row["p3"], "p3_operand": row["p3_operand"],
        "flag5": row["flag5"], "flag5_operand": row["flag5_operand"],
        "cleanup_secondary": row["cleanup_secondary"],
        "cleanup_secondary_operand": row["cleanup_secondary_operand"],
        "slot": row["slot"], "slot_operand": row["slot_operand"],
        "eligible_item_rewrite": literal and in_catalog and item != 0x1e,
        "rewrite_class": _rewrite_class(item) if in_catalog else None,
    }
    if in_catalog and _rewrite_class(item) == "health":
        result["grant_quantity"] = 1
    if reason:
        result["reason"] = reason
    return result


def _op2c(room, row, catalog):
    item = row["item_id"]
    literal = row["item_operand"]["mode"] == 0
    in_catalog = item in catalog if item is not None else False
    direct = row["kind"] == 1
    if not direct:
        source_type, reason = "unsupported", "op2c_non_grant_kind"
    elif not literal:
        source_type, reason = "unsupported", "dynamic_item_operand"
    elif not in_catalog:
        source_type, reason = "unsupported", "catalog_id_out_of_range"
    else:
        source_type, reason = "op2c_give", None
    result = {
        "source_id": _room_source_id(room, row["vm_directory_index"], 0x2c, row["op_off"]),
        "source_type": source_type,
        "record_class": "op2c_give" if direct else "op2c_non_grant",
        "provenance": _room_provenance(room, row, 0x2c),
        "catalog_id": _hex_id(item) if in_catalog else None,
        "raw_item_value": item, "item_operand": row["item_operand"],
        "kind": row["kind"], "kind_operand": row["kind_operand"],
        "p3": row["p3"], "p3_operand": row["p3_operand"],
        "flag5": row["flag5"], "flag5_operand": row["flag5_operand"],
        "cleanup_handle": row["cleanup_handle"], "cleanup_operand": row["cleanup_operand"],
        "eligible_item_rewrite": direct and literal and in_catalog,
        "rewrite_class": _rewrite_class(item) if in_catalog else None,
    }
    if direct and in_catalog and _rewrite_class(item) == "health":
        result["grant_quantity"] = 1
    if reason:
        result["reason"] = reason
    return result


def _group9(room, row, catalog):
    flag_id = row["flag_id"]
    literal = all(row[name]["mode"] == 0 for name in
                  ("group_operand", "flag_id_operand", "value_operand"))
    in_catalog = flag_id in catalog if flag_id is not None else False
    value = row["value"]
    if not literal or not in_catalog or value not in (0, 1):
        source_type = "unsupported"
        reason = ("dynamic_group9_operand" if not literal else
                  "catalog_id_out_of_range" if not in_catalog else "unsupported_group9_value")
    else:
        identity = (room, row["vm_directory_index"], row["opcode"], row["op_off"])
        source_type = ("group9_regrant" if identity in GROUP9_REGRANTS else
                       "group9_grant" if value == 1 else "group9_clear")
        reason = None
    result = {
        "source_id": _room_source_id(room, row["vm_directory_index"], row["opcode"], row["op_off"]),
        "source_type": source_type, "record_class": "group9_flag_write",
        "provenance": _room_provenance(room, row, row["opcode"]),
        "catalog_id": _hex_id(flag_id) if in_catalog else None,
        "group_operand": row["group_operand"], "flag_id_operand": row["flag_id_operand"],
        "value_operand": row["value_operand"], "value": value,
        "eligible_item_rewrite": False,
    }
    if reason:
        result["reason"] = reason
    clear_source = GROUP9_REGRANTS.get(
        (room, row["vm_directory_index"], row["opcode"], row["op_off"]))
    if clear_source:
        result["regrants_after"] = clear_source
    return result


def generate():
    catalog = _catalog()
    expressions, start_guard_index = _guard_authority()
    sources = []
    room_context = {}
    raw_stats = {"sat1_trigger": 0, "op35_take": 0, "op2c_total": 0,
                 "op2c_give": 0, "op2c_unsupported": 0}
    for path in sorted(glob.glob(os.path.join(DATA, "ST*.DAT"))):
        room = os.path.splitext(os.path.basename(path))[0]
        blob = _load_blob(path)
        section = ds.script_section(blob) if blob else None
        if not section:
            continue
        start, size = section
        room_context[room] = {
            "blob": blob,
            "model": _room_invocation_model(room, blob, start, size,
                                             expressions, start_guard_index),
        }
        sat1 = ds.item_commits(blob, start, size)
        takes = ds.take_item_commits(blob, start, size)
        gives = ds.scripted_give_commits(blob, start, size)
        raw_stats["sat1_trigger"] += len(sat1)
        raw_stats["op35_take"] += len(takes)
        raw_stats["op2c_total"] += len(gives)
        sources.extend(_sat1(room, row) for row in sat1)
        sources.extend(_op35(room, row, catalog) for row in takes)
        converted = [_op2c(room, row, catalog) for row in gives]
        raw_stats["op2c_give"] += sum(row["source_type"] == "op2c_give" for row in converted)
        raw_stats["op2c_unsupported"] += sum(row["source_type"] == "unsupported" for row in converted)
        sources.extend(converted)
        for row in ds.flag_set_commits(blob, start, size):
            if row["group"] == 9:
                sources.append(_group9(room, row, catalog))

    # Native/static sources have stable EXE identities rather than room/routine identities.
    for item_id, table_offset in ((0x33, 0), (0x34, 2)):
        sources.append({
            "source_id": f"native:shop@0x{0x71dc28 + table_offset:x}:item{item_id:02x}",
            "source_type": "shop_acquisition", "catalog_id": _hex_id(item_id),
            "provenance": {"exe_table": "0x71dc28", "entry_offset": table_offset},
            "eligible_item_rewrite": False, "reason": "optional_shop_item_out_of_progression",
        })
    sources.append({
        "source_id": "native:item-use-consumption@0x4925bf",
        "source_type": "native_consumption", "catalog_id": None,
        "provenance": {"exe_va": "0x4925bf", "cleanup_writes": ["0x4925dc", "0x4925e0", "0x4925e4", "0x4925e8"]},
        "eligible_item_rewrite": False,
        "reason": "generic_runtime_owned-item_clear; item id is runtime-selected",
    })
    for row in sources:
        if row.get("eligible_item_rewrite"):
            context = room_context[row["provenance"]["room"]]
            _attach_ap_availability(row, context["model"], context["blob"],
                                    row["provenance"]["room"], expressions)
    sources.sort(key=lambda row: row["source_id"])
    by_type = {}
    for row in sources:
        by_type[row["source_type"]] = by_type.get(row["source_type"], 0) + 1
    ap_dispositions = {}
    for row in sources:
        if "ap_availability" in row:
            key = row["ap_availability"]["disposition"]
            ap_dispositions[key] = ap_dispositions.get(key, 0) + 1
    return {
        "schema_version": 2,
        "_source": "tools/dc2_re/gen_item_sources.py; pristine-room-first static decode, K129.",
        "_contract": "SAT-1 is physical VM routing only. Only literal in-catalog op35 TAKE and kind-1 op2C GIVE rows may expose an item rewrite. Site-local p3, flag5, cleanup, opcode, room, routine and offsets are immutable expected values. op35 item 0x1e is excluded because GiveItem remaps it to 0x1d on Easy.",
        "_writer_contract": "Fail closed unless source_id, room/routine/op offset, opcode, item operand mode and expected value, and every pinned site operand match. Rewrite exactly the two-byte little-endian item push operand at item_operand.value_off. Never edit SAT-1 thread/routine, op2C kinds 2-5, dynamic operands, out-of-range values, site flags, cleanup handles, quantities or capacities.",
        "_field_semantics": {"p3": "kind-associated opaque field; stored then cleared, no pointer-proven item-path reader", "flag5": "group-5 taken/site flag", "cleanup": "site deactivation handle consumed by 0x49FE40; not EP/quantity/capacity", "amount": "hardcoded one at 0x49F87C", "capacity": "difficulty-derived 5/2/1 at 0x49F890..0x49F8B5"},
        "source_types": ["sat1_trigger", "op35_take", "op2c_give", "group9_grant", "group9_clear", "group9_regrant", "shop_acquisition", "native_consumption", "unsupported"],
        "catalog_ids": [_hex_id(value) for value in sorted(catalog)],
        "stats": dict(raw_stats, by_type=by_type, ap_dispositions=ap_dispositions),
        "sources": sources,
    }


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--check", action="store_true")
    parser.add_argument("--repo-root", required=True, help="DinoRand checkout for authored data")
    args = parser.parse_args()
    global REPO, OUT, CATALOG, GUARDS
    REPO = os.path.abspath(args.repo_root)
    OUT = os.path.join(REPO, "data", "dc2", "item-sources.json")
    CATALOG = os.path.join(REPO, "data", "dc2", "items.json")
    GUARDS = os.path.join(REPO, "data", "dc2", "door-guards.json")
    text = json.dumps(generate(), indent=2) + "\n"
    if args.check:
        current = open(OUT, encoding="utf-8").read() if os.path.isfile(OUT) else None
        if current != text:
            print(f"stale generated data: {os.path.relpath(OUT, REPO)}")
            return 1
        print(f"current: {os.path.relpath(OUT, REPO)}")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)
    stats = generate()["stats"]
    print(f"wrote {os.path.relpath(OUT, REPO)}: SAT1={stats['sat1_trigger']} "
          f"op35={stats['op35_take']} op2C={stats['op2c_total']} "
          f"eligible-op2C={stats['op2c_give']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
