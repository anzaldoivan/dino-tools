import importlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import tool_paths

class SafetyTests(unittest.TestCase):
    def test_investigators_import_without_game_files_or_processes(self):
        code = r"""
import importlib, os, pathlib, sys
root = pathlib.Path(sys.argv[1])
def deny(event, args):
    if event == "open" and isinstance(args[0], (str, bytes, os.PathLike)):
        path = os.fsdecode(args[0]).lower()
        if path.endswith((".exe", ".dat", ".tex", ".iso", ".img", ".dll", ".dmp")):
            raise AssertionError("Game file opened during import: " + path)
    if event in ("subprocess.Popen", "os.system"):
        raise AssertionError("Process launched during import")
sys.addaudithook(deny)
for directory in ("scd_re", "dc2_re"):
    sys.path.insert(0, str(root / directory))
    for path in sorted((root / directory).glob("*.py")):
        if not path.name.startswith("test_"):
            importlib.import_module(path.stem)
"""
        result = subprocess.run([sys.executable, "-c", code, str(ROOT)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr + result.stdout)

    def test_output_refuses_install_and_symlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            install = Path(tmp) / "install"
            install.mkdir()
            with patch.dict(os.environ, {"DINORAND_DC1_DIR": str(install)}):
                with self.assertRaises(ValueError):
                    tool_paths.refuse_install_output(install / "output.dat")
                if os.name != "nt":
                    alias = Path(tmp) / "alias"
                    alias.symlink_to(install, target_is_directory=True)
                    with self.assertRaises(ValueError):
                        tool_paths.refuse_install_output(alias / "output.dat")
                tool_paths.refuse_install_output(Path(tmp) / "safe.dat")

    def test_generator_requires_repository_argument(self):
        result = subprocess.run([sys.executable, str(ROOT / "dc2_re/gen_item_sources.py"), "--check"], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("--repo-root", result.stderr)

    def test_scanner_rejects_child_asset(self):
        with tempfile.TemporaryDirectory() as tmp:
            subprocess.run(["git", "init", "-q", tmp], check=True)
            forbidden = Path(tmp) / "forbidden.TEX"
            forbidden.write_text("synthetic forbidden extension")
            subprocess.run(["git", "-C", tmp, "add", "forbidden.TEX"], check=True)
            result = subprocess.run(["bash", str(ROOT / "scripts/check-no-copyrighted-files.sh")], cwd=tmp, capture_output=True)
            self.assertNotEqual(0, result.returncode)
