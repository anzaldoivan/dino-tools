from pathlib import Path
import random
import struct
import sys
import unittest
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'dc2_re'))
from dc2pkg import lzss_dec, parse
from roundtrip_t8 import lzss_enc, repack_verbatim

class SyntheticFormats(unittest.TestCase):
    def test_lzss_roundtrips_empty_literal_overlap_and_window_boundary(self):
        rng = random.Random(12345)
        for data in [b'', b'a', b'abc' * 2000, bytes(range(256)) * 30, rng.randbytes(5000)]:
            with self.subTest(size=len(data)):
                self.assertEqual(data, lzss_dec(lzss_enc(data)))

    def test_synthetic_package_roundtrip_preserves_padding_and_payload(self):
        payload = b'authored synthetic payload'
        header = bytearray(2048)
        struct.pack_into('<II', header, 0, 1, len(payload))
        struct.pack_into('<II', header, 32, 0xFFFFFFFF, 0)
        package = bytes(header) + payload + b'\0' * (2048 - len(payload))
        entries, variant = parse(package)
        self.assertEqual('DC2', variant)
        self.assertEqual(payload, entries[0].data)
        self.assertEqual(package, repack_verbatim(package))
