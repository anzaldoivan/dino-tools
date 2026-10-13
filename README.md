# dino-tools

Standalone model libraries, dcmtool, and maintained DC1/DC2 static investigators.
Dino Crisis is a trademark of Capcom. Unofficial and unaffiliated with Capcom;
no game assets are distributed. Use your own legally purchased GOG DRM-free
installation. Protected Steam builds are unsupported.

## Build and test

Install the exact SDK from global.json, Python 3, and requirements.txt.

```sh
git config core.hooksPath .githooks
python3 scripts/check-provenance.py
bash scripts/check-no-copyrighted-files.sh
dotnet restore DinoTools.sln --locked-mode
dotnet build DinoTools.sln -c Release --no-restore
dotnet test DinoTools.sln -c Release --no-build
python3 -m unittest discover -s tests
dotnet run --project DinoCrisis.Model.Cli -- help
```

No DinoRand checkout, Biohazard checkout or game installation is needed for the
synthetic tests. Real-install tests visibly skip when fixtures are unavailable.
Set DINORAND_DC1_DIR / DINORAND_DC2_DIR in the environment or an ignored .env;
these are the only installation-discovery variables. Roots may be the game
installation or its english/rebirth directory. Explicit investigator input paths
remain supported. Never run process helpers in CI.

## Access and output behavior

* dcmtool info/list/dump/validate/roundtrip read input files and print findings.
* obj/gltf export to an explicit output path; import writes an explicit output
  package. Input overwrites and configured-install destinations are refused.
* scd_re and dc2_re modules inspect explicitly selected files or configured
  installs. They do not open game files or processes during module import.
  Disassemblers print user-owned input analysis; no disassembly is shipped.
* scd_re/extract_logic.py and dc2_re/gen_item_sources.py require --repo-root for DinoRand authored JSON
  inputs and output. The item generator uses --check/--apply; the logic generator
  uses --dry-run to read only. Both write only to that explicit checkout.
* dc2_re/psx_iso.py extracts user-owned image files to explicit output destinations;
  configured-install outputs, input overwrites and traversal outside the output are refused.
* model-pipeline writes synthetic temporary round trips and dependency caches.
  Windows CI installs exact checksum-verified tools and fails if unavailable.
* ce_re/mem.ps1 is a Windows-only VM_READ helper. Dot-sourcing only defines
  functions. A bounded runtime authorization is required before Open-Dino with
  an explicit process ID. Close-Dino closes only the supplied session handle.

The verified original tools backup retains private probes, deprecated patchers,
exports, caches and research notes locally. migration-inventory.json explicitly
lists investigators not yet maintained in this standalone repository; they must
be reviewed and tested before later migration. See THIRD-PARTY-NOTICES.md and
vendor/biohazard/provenance.json for dependency ownership and update policy.

## Dependency and pin updates

Review source provenance before updating the vendor snapshot. Run restore with
--force-evaluate intentionally when changing dependencies and review lockfiles.
Configure hooks separately in this repository and its parent. DinoRand pins a
reviewed commit through a gitlink; use git submodule update --init tools, never
floating --remote updates. Public CI never uploads game-derived artifacts.
