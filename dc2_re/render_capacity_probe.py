from __future__ import annotations
import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Report authored DC2 renderer-capacity evidence from a local Dino2.exe.

Read-only: the executable is hashed and disassembled in memory.  Output contains
addresses and instruction summaries, never executable bytes or copied routines.
"""


import hashlib
import argparse
import os
import re
import struct
import sys
from collections import Counter

from capstone import CS_ARCH_X86, CS_MODE_32, CS_OP_IMM, CS_OP_MEM, Cs

sys.path.insert(0, os.path.dirname(__file__))
from exe_xref import PE  # noqa: E402
from dc2pkg import GPC_LZSS0, parse  # noqa: E402


WATCHED = {
    0x876C58: "primitive_packet_cursor",
    0x876DCC: "display_buffer_index",
    0x874BC0: "ordering_table_bank_0",
    0x875BC0: "ordering_table_bank_1",
    0x87890C: "active_draw_environment",
}


def read_dword_va(image: bytes, pe: PE, va: int) -> int:
    file_offset = pe.va2fo(va)
    if file_offset is None:
        raise ValueError(f"VA 0x{va:08X} is not file-backed")
    return struct.unpack_from("<I", image, file_offset)[0]


def scan_rebirth(path: str) -> None:
    with open(path, "rb") as stream:
        image = stream.read()
    pe = PE(image)
    text_section = next(section for section in pe.secs if section[0] == ".text")
    _, section_va, _, raw_offset, raw_size = text_section
    text_bytes = image[raw_offset : raw_offset + raw_size]
    text_va = pe.imagebase + section_va
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    md.detail = True
    md.skipdata = True
    instructions = [insn for insn in md.disasm(text_bytes, text_va) if insn.id]
    watched = (0x876C58, 0x874BC0, 0x875BC0, 0x752F40, 0x7535EC, 0x753C7C, 0x753FF8)
    print(f"rebirth_size={len(image)}")
    print(f"rebirth_sha256={hashlib.sha256(image).hexdigest().upper()}")
    print(
        f"rebirth_text_map=va=0x{text_va:08X} file_offset=0x{raw_offset:X} "
        f"raw_size=0x{raw_size:X}"
    )
    for address in watched:
        print(
            f"rebirth_text_literal=0x{address:08X} "
            f"count={text_bytes.count(struct.pack('<I', address))}"
        )
    for base in (0x500000, 0x510000, 0x550000, 0x590000, 0x5E0000):
        direct_reset = b"\xC7\x05" + struct.pack("<II", 0x876C58, base)
        operand_refs = []
        for insn in instructions:
            if any(
                (operand.type == CS_OP_IMM and operand.imm == base)
                or (operand.type == CS_OP_MEM and operand.mem.disp == base)
                for operand in insn.operands
            ):
                operand_refs.append(insn)
        print(
            f"rebirth_cursor_base_literal=0x{base:08X} "
            f"raw_candidate_count={text_bytes.count(struct.pack('<I', base))} "
            f"instruction_operand_refs={len(operand_refs)} "
            f"direct_reset_count={text_bytes.count(direct_reset)}"
        )
        for insn in operand_refs:
            print(
                f"  va=0x{insn.address:08X} file_offset=0x{pe.va2fo(insn.address):X} "
                f"{insn.mnemonic} {insn.op_str}"
            )
    dispatch_table = 0x102FC2E0
    for opcode in (0x32, 0x34, 0x3A, 0x3C, 0x64, 0x66):
        handler = read_dword_va(image, pe, dispatch_table + opcode * 4)
        print(
            f"rebirth_packet_handler=0x{opcode:02X} va=0x{handler:08X} "
            f"file_offset=0x{pe.va2fo(handler):X}"
        )


def scan_models(data_dir: str) -> None:
    records = []
    for name in sorted(os.listdir(data_dir)):
        if not name.upper().endswith(".DAT"):
            continue
        path = os.path.join(data_dir, name)
        try:
            with open(path, "rb") as stream:
                entries, _ = parse(stream.read())
        except (AssertionError, OSError, struct.error, ValueError):
            continue
        for entry_index, entry in enumerate(entries):
            if entry.type != GPC_LZSS0 or len(entry.decomp) < 0x30:
                continue
            blob = entry.decomp
            base = struct.unpack_from("<I", entry.reserve, 0)[0]
            vertex, normal, triangle, quad = struct.unpack_from("<IIII", blob, 0)
            triangle_count, quad_count = struct.unpack_from("<HH", blob, 0x10)
            part_count = struct.unpack_from("<I", blob, 0x14)[0]
            if not (0 < part_count <= 64 and base <= vertex <= normal <= triangle <= quad):
                continue
            if 0x1C + part_count * 0x14 > len(blob):
                continue
            vertex_bytes = normal - vertex
            normal_bytes = triangle - normal
            if vertex_bytes % 8 or normal_bytes != vertex_bytes:
                continue
            if quad - triangle != triangle_count * 12:
                continue
            if quad - base + quad_count * 16 > len(blob):
                continue
            vertex_count = vertex_bytes // 8
            runs = sum(blob[0x1C + index * 0x14 + 7] for index in range(part_count))
            records.append((
                vertex_count, name, entry_index, part_count, triangle_count, quad_count, runs,
                base, vertex, normal, triangle, quad,
            ))

    print(f"dcm_census_models={len(records)}")
    if not records:
        return
    maximum = max(records)
    print(
        f"dcm_census_max_vertices={maximum[0]} file={maximum[1]} entry={maximum[2]} "
        f"parts={maximum[3]} triangles={maximum[4]} quads={maximum[5]} "
        f"transform_triplets={maximum[6]}"
    )
    mismatches = [record for record in records if record[0] != record[6] * 3]
    print(f"dcm_census_transform_run_mismatches={len(mismatches)}")
    for record in mismatches:
        print(
            f"  file={record[1]} entry={record[2]} vertices={record[0]} "
            f"transform_triplets={record[6]}"
        )
    actor_names = re.compile(r"^(?:E[0-9A][0-9A]|WEP_P|KOF_P|M_TITLE)", re.IGNORECASE)
    actor_records = [record for record in records if actor_names.match(record[1])]
    if actor_records:
        actor_maximum = max(actor_records)
        print(
            f"dcm_filename_selected_max_vertices={actor_maximum[0]} file={actor_maximum[1]} "
            f"entry={actor_maximum[2]} parts={actor_maximum[3]} "
            f"triangles={actor_maximum[4]} quads={actor_maximum[5]} "
            f"transform_triplets={actor_maximum[6]} classification=advisory_not_renderer_proof"
        )
    above_420 = [record for record in records if record[0] > 420]
    print(f"dcm_census_above_420={len(above_420)}")
    for record in sorted(above_420, reverse=True):
        print(
            f"  vertices={record[0]} file={record[1]} entry={record[2]} "
            f"load_base=0x{record[7]:08X} transform_triplets={record[6]} "
            f"advisory_primary_packet_bytes={64 * record[4] + 84 * record[5]}"
        )
    resource_names = {"RES02.DAT", "RES00.DAT", "RESULT.DAT"}
    print("dcm_tracked_resource_records=")
    for record in sorted((r for r in records if r[1].upper() in resource_names), reverse=True):
        print(
            f"  file={record[1]} entry={record[2]} load_base=0x{record[7]:08X} "
            f"vertex=0x{record[8]:08X} normal=0x{record[9]:08X} "
            f"triangle=0x{record[10]:08X} quad=0x{record[11]:08X} "
            f"vertices={record[0]} parts={record[3]} triangles={record[4]} quads={record[5]} "
            f"transform_triplets={record[6]} "
            f"advisory_primary_packet_bytes={64 * record[4] + 84 * record[5]}"
        )
    print("dcm_census_top_vertices=")
    for record in sorted(records, reverse=True)[:10]:
        print(
            f"  vertices={record[0]} file={record[1]} entry={record[2]} "
            f"parts={record[3]} triangles={record[4]} quads={record[5]} "
            f"transform_triplets={record[6]}"
        )


def main(path: str, data_dir: str | None = None, verbose: bool = False) -> int:
    with open(path, "rb") as stream:
        image = stream.read()

    pe = PE(image)
    text = next(section for section in pe.secs if section[0] == ".text")
    _, section_va, _, raw_offset, raw_size = text
    text_va = pe.imagebase + section_va

    md = Cs(CS_ARCH_X86, CS_MODE_32)
    md.detail = True

    refs: dict[int, list[tuple[int, str, str, bool]]] = {
        address: [] for address in WATCHED
    }
    for insn in md.disasm(image[raw_offset : raw_offset + raw_size], text_va):
        for operand_index, operand in enumerate(insn.operands):
            if operand.type != CS_OP_MEM:
                continue
            address = operand.mem.disp
            if address in refs:
                destination = operand_index == 0
                refs[address].append(
                    (insn.address, insn.mnemonic, insn.op_str, destination)
                )

    print(f"size={len(image)}")
    print(f"sha256={hashlib.sha256(image).hexdigest().upper()}")
    for name, va, virtual_size, file_offset, file_size in pe.secs:
        print(
            f"section={name} va=0x{pe.imagebase + va:08X} "
            f"virtual_size=0x{virtual_size:X} file_offset=0x{file_offset:X} "
            f"file_size=0x{file_size:X}"
        )

    print("packet_banks=0x00510000,0x00550000 stride=0x40000")
    print("ordering_tables=0x00874BC0,0x00875BC0 entries_each=0x400 bytes_each=0x1000")
    for address, name in WATCHED.items():
        records = refs[address]
        writers = [record for record in records if record[3]]
        direct_comparisons = [
            record for record in records if record[1] in {"cmp", "test"}
        ]
        print(
            f"references={name} address=0x{address:08X} count={len(records)} "
            f"destination_count={len(writers)} direct_comparison_count={len(direct_comparisons)}"
        )
        if name == "primitive_packet_cursor":
            deltas = Counter()
            for _, mnemonic, _, destination in records:
                if destination:
                    deltas[mnemonic] += 1
            print(
                "  destination_mnemonics="
                + ",".join(f"{key}:{value}" for key, value in sorted(deltas.items()))
            )
        selected = records if verbose else [
            record for record in records if 0x470000 <= record[0] < 0x471000
        ]
        for instruction_va, mnemonic, operands, _ in selected:
            print(
                f"  va=0x{instruction_va:08X} file_offset=0x{pe.va2fo(instruction_va):X} "
                f"{mnemonic} {operands}"
            )
    if data_dir is not None:
        scan_models(data_dir)
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("exe")
    parser.add_argument("data_dir", nargs="?")
    parser.add_argument("--rebirth-dll")
    parser.add_argument("--verbose", action="store_true")
    options = parser.parse_args()
    if options.rebirth_dll:
        scan_rebirth(options.rebirth_dll)
    raise SystemExit(
        main(
            options.exe,
            data_dir=options.data_dir,
            verbose=options.verbose,
        )
    )
