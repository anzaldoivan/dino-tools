# DinoCrisis.Model

A standalone, MIT-licensed C# library that **reads and byte-exactly re-emits the Dino Crisis 1 & 2
`DCM` character/enemy model format**. No dependency on the DinoRand randomizer — it ships its own
copies of our LZSS + Gian-package code so it can be reused on its own.

It exists because no working Dino Crisis model tool does (the format was reverse-engineered and
byte-validated in [`CHARACTER-IMPORT-AUTORIG-RESEARCH.md` §7](../../docs/decisions/dc1/models/CHARACTER-IMPORT-AUTORIG-RESEARCH.md)).
Design and decisions: [`DCM-TOOLSET-PLAN.md`](../../docs/decisions/dc1/models/DCM-TOOLSET-PLAN.md). Structure
mirrors [`ref/biohazard-utils`](../../ref/biohazard-utils) (core lib + Builder-pattern format types).

## What it does

Skinned GLB is the primary Blender interchange format and supports the safe same-rig mesh/UV return
path. Wavefront OBJ remains supported as a **legacy export-only** inspection path; there is no OBJ
import or OBJ round-trip contract.

```csharp
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;

// DC1 room or character file (stNXX.dat / core*.dat) — brute-scans for every model.
foreach (var m in DcmLocator.FromDc1Package(File.ReadAllBytes("st605.dat")))
    Console.WriteLine($"{m.Header.PartCount} parts, {m.Triangles.Count} tris, base {m.LoadBase:X8}");

// DC2 enemy file (E*.DAT) — model rides the LZSS0 entry; base from reserve[0].
var raptor = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes("E00.DAT"));

// Identity read + byte-exact re-emit. Low-level builder mutations are structural only;
// use the same-rig importer below for its moved-vertex safety policy.
byte[] bytes = raptor.ToBuilder().Write();

// Legacy export-only OBJ for Blender/Noesis inspection (rest pose, FK-assembled, Y-up).
File.WriteAllText("raptor.obj", ObjExporter.Export(raptor));

// Textured OBJ (DC1): decodes the model's PSX CLUT texture from its package → .obj + .mtl + .tga.
TexturedObjExporter.Export(File.ReadAllBytes("core00.dat"), regina, outDir: "out", baseName: "regina");
```

The low-level `DcmTextureDecoder` also exposes the exact 64-byte by 32-row tile unpack/pack pair
(`UnswizzleGfx` and `SwizzleGfx`) used by compressed DC graphics. The pair preserves the rounded
storage band after a partial visible height; cross-rig atlas conversion remains a separate private
`DinoCrisis.Interchange` contract and is not part of the same-rig CLI importer.

OBJ assembly note: DCM vertices are bone-local. DC1's engine assigns consecutive raw-pool records
to positional runs using each part descriptor's byte `+7` RTPT-triple count. The exporter uses the
matching `SVECTOR.pad` annotation found on referenced stock positions, but static decode proves that
pad is not renderer input. DC2 positions a corner by its polygon-owning part. Both use
forward-kinematic world positions in the rigid rest pose.
Faces carry **per-vertex normals**
(`f v/vt/vn`): the DCM normal pool is parallel to the vertex pool (equal counts; polygons hold no
separate normal index), so each vertex's normal is emitted as a unit `vn`. Textured export decodes the
8-bit CLUT texture for **both games** — DC1 from the model's own Gian package, DC2 from its separate
sibling `E*.TEX` (`dcmtool obj E00.DAT out.obj --tex` finds `E00.TEX` automatically; `--tex-file
E00_02.TEX` selects a multi-page variant skin, whose model-matching page is auto-selected). Both 8bpp
and 4bpp (16-colour) pages decode (DC1 `st105`/`stA05` use 4bpp). The generic 16bpp pixel
decode/encode path is covered only by clean-room synthetic tests. No
authorized DC package fixture has witnessed a 16bpp model texture, so this is not a claimed
game-package or Blender round-trip capability.

## CLI (`dcmtool`)

```
dotnet run --project tools/DinoCrisis.Model.Cli -- <verb> <file> [...]

dcmtool info      <file>                  list the models in a package
dcmtool list      <dir>                   scan a Data directory for model-bearing packages
dcmtool dump      <file> [index]          per-part table for one model
dcmtool validate  <file>                  re-check structural invariants
dcmtool roundtrip <file>                  read+re-emit; report byte-exactness
dcmtool obj       <file> <out.obj> [index] [--no-flip-y] [--tex] [--tex-file <path>]
                                          export to OBJ (--tex: +.mtl +.tga; DC2 auto-finds its E*.TEX)
dcmtool gltf      <file> <out.glb> [index] [--no-flip-y] [--no-tex] [--tex-file <path>] [--scale <f>]
                                          export skinned glTF 2.0 (rig as joints + embedded PNG texture)
dcmtool import    <edited.glb> <target.dat> <out.dat> [index] [--ceiling <n>] [--tex-file <path>]
                  [--no-flip-y] [--scale <f>] [--allow-unsafe-dc2-moved-vertices]
                                          splice a Blender-edited glTF back over the target model
                                          (same-rig contract; DC1 reuses stock vertices, DC2 fits in place)
```

Files: DC1 `stNXX.dat` / `core*.dat`, DC2 `E*.DAT` (the game is auto-detected). Exit codes: 0 ok,
1 usage, 2 runtime error, 3 a check failed / import refused — so `roundtrip`/`validate` work as CI
gates and an `import` refusal is scriptable.

## Blender round-trip (glTF)

Keep these evidence levels separate:

1. **Structural:** the .NET readers, writers and import contracts accept the model.
2. **Blender:** pinned Blender imports and re-exports the GLB, and Khronos glTF Validator accepts
   both sides; semantic checks show the permitted content survived.
3. **Native reload:** `dcmtool import` writes a package which reloads and passes `dcmtool validate`.
4. **In-game:** an authorized output was installed with a reversible backup and witnessed under
   animation. This is the only engine proof.

Historical witnesses report identity and UV-only circles for DC1 Regina and DC2 Dylan. They do not
prove that a current Blender-edited moved vertex is safe, nor do they replace a new in-game witness
for a materially different edit. The current automated Blender checks use synthetic, clean-room
GLBs; real-data and in-game checks remain separately gated. Design and engine findings are in
[`DC-MODEL-TOOLKIT-PLAN.md` §10–§11](../../docs/decisions/cross/DC-MODEL-TOOLKIT-PLAN.md).

### 1. Export

```
dcmtool gltf english/Data/core00.dat regina.glb 0
dcmtool gltf 4249140_DinoCrisis2/rebirth/Data/E00.DAT raptor.glb 0
```

Textured by default. DC1 decodes the texture from the model's own package; **DC2 needs the
sibling `E*.TEX`** next to the `.DAT` (auto-found) or an explicit `--tex-file <path>` — without
one the GLB exports untextured (WEP_* player files have no TEX; that's still UV-exact on
re-import). Prefer exporting from the pristine `.dinorand-bak` if the live file already carries
an injection.

### 2. Blender import

File → Import → glTF 2.0. Set **Bone Dir = "Blender"** (`bone_heuristic='BLENDER'`) so bone
orientations survive a later re-export; leave the rest at defaults. The model imports upright,
~1.7 m for a human (exporter scale 0.001, Y flipped to +Y-up).

> **Display-fidelity correction (validated 2026-07-21):** the exporter no longer performs an extra
> triangle reversal after reflecting Y. It emits a native-equivalent single-sided material and an
> explicit nearest/clamp sampler (`magFilter=9728`, `minFilter=9728`, `wrapS=wrapT=33071`). The
> corrected Regina GLB has 595 of 596 geometric face normals agreeing with averaged custom normals.
> Triangle 417 (owner 8; native order 205/213/224) is the truthful exception: its three stock corner
> normals have mixed face-relative signs (two agree and one opposes), so it remains the sole averaged
> disagreement rather than being forced or hidden. Blender 5.2 imports the sampler as Closest/Extend
> and re-exports explicit nearest-mipmap-nearest/clamp (`9728/9984/33071/33071`); texels and visible
> UVs remain unchanged. See the complete evidence in
> [`MODEL-EXPORTER-ROUNDTRIP-DECISION.md`](../MODEL-EXPORTER-ROUNDTRIP-DECISION.md#2026-07-21-display-fidelity-correction).

### Automated Blender and validator checks

The supported automation line is **exactly Blender 5.2.0 LTS**. The default portable executable is
`%LOCALAPPDATA%\DinoRand\deps\blender\5.2.0\blender.exe`; an explicit
`DINORAND_BLENDER_EXE` overrides discovery. The reviewed launcher invokes it with
`--factory-startup --offline-mode --background --disable-autoexec --python-exit-code 1`, imports with
`bone_heuristic='BLENDER'`, and writes only caller-selected GLB and deterministic JSON result paths.
Blender is GPL-3.0-or-later; the pinned official Windows archive and SHA-256 are recorded in
[`MODEL-EXPORTER-ROUNDTRIP-DECISION.md`](../MODEL-EXPORTER-ROUNDTRIP-DECISION.md#blender).

Khronos validation uses the official Apache-2.0 npm package
`gltf-validator@2.0.0-dev.3.10`, pinned by `tools/model-pipeline/package-lock.json` with integrity
`sha512-odJ4k0tRkGXiDGn78yDBg+fBbAIvBnXxh3RwAta0emSxGtyagFE8B4xELB1oYe3S5RD8Ci3uZAsZaascH2LAEQ==`.
Its default dependency root is
`%LOCALAPPDATA%\DinoRand\deps\gltf-validator\2.0.0-dev.3.10`; an explicit
`DINORAND_GLTF_VALIDATOR_DIR` overrides discovery. `validate-gltf.mjs` emits its report to stdout
with timestamps disabled and creates no adjacent sidecar.

Validator execution is itself reproducible: setup pins the official portable Node.js 24.16.0 LTS
archive (SHA-256
`edaca9bd58ec8e92037dac4e877d52f6b8f430b81c18b57e264b4e2fb111cd56`) and its bundled npm
11.13.0 under `%LOCALAPPDATA%\DinoRand\deps\node\24.16.0`. Tests resolve that `node.exe` directly;
`DINORAND_NODE_EXE` is the explicit override. Node.js is MIT-licensed with bundled dependency
notices.

From a fresh Windows environment with PowerShell and `curl.exe` (no preinstalled Node/npm needed),
install only the missing exact pins, then verify them without changing `PATH`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\model-pipeline\setup-dependencies.ps1 -Action Install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\model-pipeline\setup-dependencies.ps1 -Action Check
```

`Install` downloads Blender and Node.js only from their recorded official URLs, refuses either
SHA-256 mismatch, and runs the pinned npm's
`npm ci --ignore-scripts --no-audit --no-fund` from the exact package lock in a disposable directory
before moving completed dependencies into their versioned user caches. Existing mismatched cache
targets are refused rather than overwritten. `Check` emits machine-readable JSON and performs no
network access. Network/download approval is an execution-time prerequisite for `Install`.

The integration tests validate the generated synthetic DC1/DC2 GLB, run the pinned headless Blender
import/re-export, validate Blender's GLB, compare semantic snapshots, and delete the unique temporary
directory:

```powershell
dotnet test tools/DinoCrisis.Interchange.Tests/DinoCrisis.Interchange.Tests.csproj `
  --filter "FullyQualifiedName~ExternalToolValidatorTests|FullyQualifiedName~BlenderPipelineRoundTripTests"
```

### 3. The same-rig edit contract

The importer accepts **mesh and UV edits only** — the rig is the game's and must come back
untouched:

- **Never rename, re-parent, add, or delete the `joint_NN` bones** (watch for Blender's `.001`
  suffixes from accidental duplication). The skeleton hierarchy is validated bone-by-bone.
- Keep the mesh skinned to the armature (vertex groups = the `joint_NN` names). Binding is
  rigid: each vertex belongs to exactly one joint. DC1 vertex joints can differ from the part that
  owns a polygon using them; changing those assignments tears shared hand, neck, and limb seams.
- Do not edit or delete the secondary UV layer. `TEXCOORD_1` is reserved machine metadata carrying
  each corner's native polygon part and exact stock-pool index. The visible texture uses only the
  primary UV layer (`TEXCOORD_0`). The importer refuses damaged polygon-part metadata; DC1 moved or
  newly created vertices still fail the stock-pool safety gate.
- Keep stock normal directions on stock vertices. glTF unitizes normals, so coincident stock
  records whose directions become indistinguishable are refused instead of being guessed; the
  zero-normal placeholder is accepted with either Y-axis convention.
- **Keep each polygon's UV island inside one 256×256 page window** of the original texture. A
  PSX poly addresses one texture page; UVs a stock page can't express are refused
  (`§11` finding 1: only stock tpage words are ever emitted).
- Don't move the whole object or rescale it — vertices are re-expressed bone-locally in int16
  and a global drift is refused with the offending distance.

### 4. Blender export

Retain both UV maps when exporting. The secondary map is native round-trip metadata, not texture UVs.

File → Export → glTF 2.0 (`.glb`): Include → **Skinning** on, armature + mesh in one skinned
object, **rest pose** (don't bake a posed armature; leave "+Y up" on). Nothing else is required —
UV seams, triangulation, and quad splits are reconciled on import.

### 5. Import back

```
dcmtool import regina-edit.glb english/Data/core00.dat.dinorand-bak out/core00.new.dat 0
dcmtool import raptor-edit.glb .../Data/E00.DAT out/E00.new.DAT 0          # uses sibling E00.TEX
```

- **DC1** accepts UV and polygon-topology edits only when geometry reuses the stock vertex pool at
  its stock positions. Moved or new vertices are refused: the engine does not transform appended
  player-model records correctly. `--ceiling` remains a compatibility input for already-supported
  native layouts, not permission to bypass this refusal.
- **DC2** targets must fit **strictly in place** (the resident arena has no append headroom); the
  refusal reports the exact byte deficit. Quad reconstitution (`QuadMerge`) is what makes stock
  models fit back at all — glTF triangulates every quad. Moved vertices are refused by default;
  the explicit unsafe opt-in is for controlled research only and remains runtime-unverified.
- Install by replacing the game file (keep a `.dinorand-bak` first); `dcmtool validate` the
  output before installing.

### Refusal diagnostics (exit 3, each names the fix)

Wrong/extra/missing skin or `joint_NN` names, edited rest transforms or inverse binds, invalid or
non-rigid skin weights, unskinned or non-triangle primitives, UV islands spanning texture pages,
mesh drifted beyond int16 bone-local range, vertex ceiling exceeded, DC1 moved/new vertices, or a
DC2 in-place deficit. The DC2 moved/new-vertex refusal can only be bypassed with the explicit
`--allow-unsafe-dc2-moved-vertices` research option, which emits a runtime-unverified warning.

**Known limitation (verdict retained 2026-07-22):** edits that *move or add* vertices remain refused
by the production DC1 importer. Static decode closes the source rule—file part byte `+7` is the
RTPT-triple count for consecutive position/normal records. A separate offline
`Dc1RunAwareCanonicalizer` can build and validate explicit target-owned runs, but it is not wired to
`dcmtool import` or installation and remains runtime-pending. Tail-appended records outside authored runs
are never transformed and their polygons render at a stale fixed screen position (CE-witnessed;
root cause and static closure in
[`APPEND-VERTEX-TRANSFORM-COVERAGE-RCA.md`](../../docs/decisions/dc1/models/APPEND-VERTEX-TRANSFORM-COVERAGE-RCA.md)).
Until the runtime gate succeeds and production integration is reviewed, keep supported DC1 edits
to UVs and polygon topology over the stock
vertex set. DC2 identity is structurally covered, but moved-vertex behavior is runtime-unverified
even when the explicit unsafe research opt-in is used.

## Layout (both games)

`obj_cnt@0x14`, part-descriptor stride `0x14` (xyz@0, parent@6, legacy `Child` API byte@7—DC1
RTPT-triple count, triPtr@8, quadPtr@0xc,
triCnt@0x10, quadCnt@0x12); header `vertex_ptr@0 / normal_ptr@4 / tri_ptr@8 / quad_ptr@0xc` + totals.
DC1: header `0x18`, per-poly clut/tpage (tri 16 B / quad 20 B). DC2: header `0x1c` (header-level
tpage/clut), tri 12 B / quad 16 B. Captured as data in `DcmVariant`.

## Tests

Run both tool suites:

```powershell
dotnet test tools/DinoCrisis.Model.sln
dotnet test tools/DinoCrisis.Interchange.sln
```

Synthetic fixtures run anywhere. User-owned real-data tests resolve DC1 and DC2 independently from
`DINORAND_DC1_DIR` and `DINORAND_DC2_DIR`; an unavailable game is reported as **skipped**, never as a
passing early return, and does not suppress the other game's fixtures. No copyrighted fixture is
committed. Historical `_ZZ_*.cs` probes remain available locally but are compile-excluded from the
maintained test assembly; ordinary test discovery must not run their artifact builders.

## Roadmap

Shipped (Inc 0–10): reader + byte-exact round-trip; `dcmtool` CLI (`info|list|dump|validate|
roundtrip|obj|gltf|import`); OBJ export with per-vertex normals (`vn`); DC1 (8bpp + 4bpp) and DC2
(single + multi-page `E*.TEX`) textured export; and a **re-layout `DcmModelBuilder`** (`Serialize` +
`AddPart`/`RemovePart` + `AddVertices`/`AddVertex`) — the static substrate for mesh editing.
Shipped (DC-MODEL-TOOLKIT P1+P2, in `tools/DinoCrisis.Interchange`): skinned GLB export and the
same-rig mesh/UV re-import above (`GltfInterchange`/`GltfImporter`/`QuadMerge` +
`Dc1ModelSplicer`/`Dc2ModelSplicer`). The mesh **rebuild onto a
foreign rig** (Tier 4, `SpeciesModelTransplant`) stays deferred behind cross-rig motion retargeting
(a runtime question). Animation-clip interchange remains out of scope. A witnessed 16bpp DC package
and a general multi-page atlas remain future work. See
[`DC-MODEL-TOOLKIT-PLAN.md`](../../docs/decisions/cross/DC-MODEL-TOOLKIT-PLAN.md) and
[`DCM-TOOLSET-PLAN.md`](../../docs/decisions/dc1/models/DCM-TOOLSET-PLAN.md).

License: MIT. See [`NOTICE.md`](NOTICE.md) for clean-room provenance.
