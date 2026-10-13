from pathlib import Path
import struct
import sys
import unittest
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scd_re'))
import scd


def package(*entries):
    """DC1 package from (type, payload, load_address) triples; load 0 = no address."""
    header = bytearray(2048)
    body = bytearray()
    for i, (typ, payload, load) in enumerate(entries):
        x = load & 0xFFFF
        y = (0x8000 | ((load >> 16) & 0x7FFF)) if load else 0
        struct.pack_into('<IIHH', header, i * 16, typ, len(payload), x, y)
        body += payload + b'\0' * (-len(payload) % 2048)
    return bytes(header) + bytes(body)


class RoomDataSelection(unittest.TestCase):
    RDT = b'synthetic room data'

    def test_room_data_is_the_entry_loaded_at_the_rdt_base_not_the_last(self):
        b = package((1, b'tex', 0), (0, self.RDT, scd.PSX_RDT_BASE),
                    (3, b'snd', 0), (4, b'bank', 0), (0, b'table', 0x801FE9C0))
        self.assertEqual(1, scd.rdt_index(b))

    def test_falls_back_to_the_last_entry_without_a_load_address(self):
        b = package((1, b'tex', 0), (0, self.RDT, 0))
        self.assertEqual(1, scd.rdt_index(b))

    def test_get_rdt_reads_the_selected_entry(self):
        import tempfile, os
        b = package((1, b'tex', 0), (0, self.RDT, scd.PSX_RDT_BASE), (0, b'table', 0x801FE9C0))
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'synthetic.pkg')
            with open(path, 'wb') as f:
                f.write(b)
            rdt, typ = scd.get_rdt(path)
        self.assertEqual(self.RDT, rdt)
        self.assertEqual(0, typ)


if __name__ == '__main__':
    unittest.main()
