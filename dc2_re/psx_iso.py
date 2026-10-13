import sys as _path_sys
from pathlib import Path as _ToolPath
from pathlib import Path
_path_sys.path.insert(0, str(_ToolPath(__file__).resolve().parents[1]))
from tool_paths import game_path, refuse_install_output
#!/usr/bin/env python
"""Minimal PSX (MODE2/2352) ISO9660 reader/extractor.

Raw CD sectors are 2352 bytes: 16 sync/header + 8 subheader + 2048 user data
+ 280 EDC/ECC. ISO9660 user data starts at offset 24 within each sector.
Logical Block Number N -> file offset N*2352 + 24, length 2048.

Usage:
  python psx_iso.py <track1.bin> list
  python psx_iso.py <track1.bin> extract <NAME>[;1] <outpath>
  python psx_iso.py <track1.bin> extract-all <outdir>
"""
import sys, os, struct

RAW = 2352
UD_OFF = 24      # user-data offset within a MODE2/2352 sector
UD_LEN = 2048

def read_lbn(f, lbn, nbytes):
    """Read nbytes of logical ISO data starting at logical block lbn."""
    out = bytearray()
    sec = lbn
    while len(out) < nbytes:
        f.seek(sec * RAW + UD_OFF)
        out += f.read(UD_LEN)
        sec += 1
    return bytes(out[:nbytes])

def parse_dir_records(data):
    """Yield (name, lbn, length, is_dir) from a directory extent buffer."""
    i = 0
    while i < len(data):
        rlen = data[i]
        if rlen == 0:
            # advance to next sector boundary within the extent
            nxt = (i // UD_LEN + 1) * UD_LEN
            if nxt <= i:
                break
            i = nxt
            continue
        rec = data[i:i+rlen]
        ext_lbn = struct.unpack('<I', rec[2:6])[0]
        ext_len = struct.unpack('<I', rec[10:14])[0]
        flags = rec[25]
        nlen = rec[32]
        name = rec[33:33+nlen]
        if nlen == 1 and name in (b'\x00', b'\x01'):
            nm = '.' if name == b'\x00' else '..'
        else:
            nm = name.decode('latin1')
        yield nm, ext_lbn, ext_len, bool(flags & 0x02)
        i += rlen

def walk(f, lbn, length, prefix=''):
    data = read_lbn(f, lbn, length)
    for nm, elbn, elen, is_dir in parse_dir_records(data):
        if nm in ('.', '..'):
            continue
        path = prefix + '/' + nm
        if is_dir:
            yield path, elbn, elen, True
            yield from walk(f, elbn, elen, path)
        else:
            yield path, elbn, elen, False

def get_root(f):
    pvd = read_lbn(f, 16, UD_LEN)
    assert pvd[1:6] == b'CD001', 'Not an ISO9660 PVD at sector 16: %r' % pvd[1:6]
    root_rec = pvd[156:156+34]
    root_lbn = struct.unpack('<I', root_rec[2:6])[0]
    root_len = struct.unpack('<I', root_rec[10:14])[0]
    return root_lbn, root_len

def main():
    img = sys.argv[1]
    cmd = sys.argv[2]
    with open(img, 'rb') as f:
        rlbn, rlen = get_root(f)
        if cmd == 'list':
            for path, lbn, length, is_dir in walk(f, rlbn, rlen):
                kind = 'DIR ' if is_dir else 'FILE'
                print('%s lbn=%-8d len=%-10d %s' % (kind, lbn, length, path))
        elif cmd == 'extract':
            target = sys.argv[3].lstrip('/').upper()
            out = str(refuse_install_output(sys.argv[4], img))
            for path, lbn, length, is_dir in walk(f, rlbn, rlen):
                if is_dir:
                    continue
                p = path.lstrip('/').upper()
                if p == target or p.split(';')[0] == target.split(';')[0]:
                    with open(out, 'wb') as o:
                        o.write(read_lbn(f, lbn, length))
                    print('wrote %s (%d bytes) from lbn=%d' % (out, length, lbn))
                    return
            print('NOT FOUND: %s' % target)
        elif cmd == 'extract-all':
            outdir = str(refuse_install_output(sys.argv[3], img))
            for path, lbn, length, is_dir in walk(f, rlbn, rlen):
                if is_dir:
                    os.makedirs(outdir + path, exist_ok=True)
                    continue
                op = str(refuse_install_output(outdir + path.split(';')[0], img))
                if not Path(op).is_relative_to(Path(outdir).resolve()):
                    raise ValueError('Refusing path outside extraction output')
                os.makedirs(os.path.dirname(op), exist_ok=True)
                with open(op, 'wb') as o:
                    o.write(read_lbn(f, lbn, length))
            print('extracted all to %s' % outdir)

if __name__ == '__main__':
    main()
