from pathlib import Path
import hashlib, json
root = Path(__file__).resolve().parents[1]
core = root / "vendor/biohazard"
manifest = json.loads((core / "provenance.json").read_text())
expected = set(manifest["files"])
actual = {str(p.relative_to(root)) for p in core.rglob("*") if p.is_file() and not any(s in p.parts for s in ("bin", "obj"))} - {"vendor/biohazard/LICENSE", "vendor/biohazard/provenance.json", "vendor/biohazard/packages.lock.json"}
if actual != expected:
    raise SystemExit(f"Snapshot inventory mismatch: {actual ^ expected}")
for name, hashes in manifest["files"].items():
    if hashlib.sha256((root / name).read_bytes()).hexdigest() != hashes["snapshot_sha256"]:
        raise SystemExit(f"Snapshot hash mismatch: {name}")
print("Source snapshot provenance hashes verified")
