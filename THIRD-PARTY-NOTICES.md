# Third-party notices

Dino Crisis is a trademark of Capcom. This project is unofficial and unaffiliated
with Capcom. It distributes no game assets and requires your own legally purchased
GOG DRM-free installation; protected Steam builds are unsupported. No protection
bypass is provided.

DinoCrisis.Model containers derive from DinoRand's MIT implementation; the root
LICENSE preserves its copyright notice. See DinoCrisis.Model/NOTICE.md.

`vendor/biohazard` is an MIT core-source snapshot of Ted John's Biohazard Utils,
https://github.com/biorand/biohazard-utils at
`caa5db3fb39c722e6783628e6024229337e4edef`. Its license is retained alongside the
source. `provenance.json` records upstream and imported hashes; the behavioral source
change is the six read-only TIM properties listed there. Source text is normalized
to UTF-8/LF without trailing whitespace; both original and normalized hashes are recorded. No utilities, templates,
models, textures, audio, compiled resources or upstream Git history are included.
Updates require an explicit source/provenance review and refreshed hashes.

NuGet dependencies and transitive versions are recorded in per-project lockfiles.
SharpGLTF (MIT) provides glTF interchange. AFSLib (MIT), NVorbis (MIT), and
Microsoft's System.Memory/System.Text.Json/NETStandard.Library support the core.
xUnit (Apache-2.0) and Microsoft's test SDK support tests.
The npm lock pins Khronos glTF Validator (Apache-2.0).
Blender (GPL-3.0-or-later) and Node.js (MIT and bundled third-party licenses) are
optional external tools downloaded into a user cache, never redistributed here.
Python investigators use Capstone (BSD) and pefile (MIT).
