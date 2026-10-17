import sys as _path_sys
from pathlib import Path as _ToolPath
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path
#!/usr/bin/env python3
"""Tests for decode_dbs.py and decode_cameras.py (K142-K146).

The synthetic test needs no game files; the corpus tests use the rebirth install
and are skipped without it. Run: python tools/dc2_re/test_decode_room_view.py
"""
import os, struct, glob, unittest
_path_sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import decode_dbs as dbs
import decode_cameras as cam

HAVE = os.path.isfile(os.path.join(cam.REB, "ST101.DAT"))


def pkg(entries):
    hdr, body = b"", b""
    for t, res, payload in entries:
        hdr += struct.pack("<II", t, len(payload)) + res.ljust(24, b"\0")
        body += payload.ljust((len(payload) + 0x7FF) & ~0x7FF, b"\0")
    return hdr.ljust(0x800, b"\0") + body


class Synthetic(unittest.TestCase):
    def test_two_packages_and_mask(self):
        jpg = b"\xff\xd8\xff\xc0\x00\x11\x08" + struct.pack(">HH", 240, 320) + b"\0" * 8
        cells = bytes(648)                       # one all-zero 18x18 cell: one LZSS run would do
        lz = b"\xff" + cells[:8]                 # 8 literals; enough to prove the path
        tab = struct.pack("<IBBh", 1, 5, 0x1E, 280)
        a = pkg([(0, struct.pack("<I", 0x6B9500), tab), (0, struct.pack("<I", 0x6C1500), jpg),
                 (2, b"", bytes(512)), (0, struct.pack("<I", 8), lz)])
        b = pkg([(0, b"", bytes(4)), (0, b"", jpg)])
        pk, end = dbs.packages(a + b)
        self.assertEqual((len(pk), end, pk[1][0]), (2, len(a + b), len(a)))
        bg = dbs.background(a + b, pk[0])
        self.assertEqual(bg["tiles"], [(80, 224, 280)])   # y uses the low nibble only
        self.assertEqual(dbs.jpeg_size(bg["jpeg"]), (320, 240))
        self.assertEqual(len(bg["cells"]), 8)
        self.assertEqual(dbs.background(a + b, pk[1])["tiles"], [])


@unittest.skipUnless(HAVE, "rebirth corpus not present")
class Corpus(unittest.TestCase):
    def test_dbs_census(self):
        c = dbs.census(sorted(glob.glob(os.path.join(cam.REB, "*.DBS"))))
        self.assertEqual((c["files"], c["packages"], c["unclosed"], c["cells_bad"]), (90, 1169, 0, 0))
        self.assertEqual(c["jpeg (320, 240)"], 1169)
        self.assertEqual(c["tiles"], 107779)

    def test_camera_i_is_background_i(self):
        total = 0
        for r in cam.room_names():
            v = cam.decode(cam.room_blob(r))
            data = _ToolPath(glob.glob(os.path.join(cam.REB, r + ".DBS"))[0]).read_bytes()
            pk, _ = dbs.packages(data)
            self.assertEqual(len(v["cameras"]), len(pk), r)
            ends = [p[0] for p in pk[1:]] + [len(data)]
            for c, p, e in zip(v["cameras"], pk, ends):
                self.assertEqual((c["dbs_offset"], c["dbs_size"]), (p[0], e - p[0]), r)
            total += len(pk)
        self.assertEqual(total, 1169)

    def test_st101_camera_0(self):
        v = cam.decode(cam.room_blob("ST101"))
        self.assertEqual(v["cameras"][0], {"eye": [390, -2609, -3470], "target": [-1984, 1750, 5929],
                                           "proj": 230, "roll": 0, "dbs_offset": 0, "dbs_size": 0x14800})
        self.assertEqual([z["camera"] for z in v["zones"]], [0, 1, 4, 0, 0])


if __name__ == "__main__":
    unittest.main()
