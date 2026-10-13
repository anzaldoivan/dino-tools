from pathlib import Path
import json
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from importlib.util import spec_from_file_location, module_from_spec
spec = spec_from_file_location('locks', Path(__file__).resolve().parents[1] / 'scripts/check-lockfiles.py')
locks = module_from_spec(spec)
spec.loader.exec_module(locks)

class LockGateTests(unittest.TestCase):
    def test_missing_and_drifted_locks_fail(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'test.csproj').write_text('<Project><ItemGroup><PackageReference Include="Example" Version="2.0.0" /></ItemGroup></Project>')
            with self.assertRaisesRegex(ValueError, 'Missing lockfile'):
                locks.check(root)
            (root / 'packages.lock.json').write_text(json.dumps({'dependencies': {'net8.0': {'Example': {'type': 'Direct', 'requested': '[1.0.0, )'}}}}))
            with self.assertRaisesRegex(ValueError, 'lock drift'):
                locks.check(root)
