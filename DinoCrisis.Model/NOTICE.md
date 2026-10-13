# Clean-room provenance — DinoCrisis.Model

This library is an independent, MIT-licensed implementation. It contains **no code copied** from any
licensed SDK.

- The `DCM` binary layout it implements is a **fact** (struct field offsets), independently
  byte-validated from real game files via DinoRand's own loader in
  `docs/dc1/CHARACTER-IMPORT-AUTORIG-RESEARCH.md` §7. The format was *documented* in
  `ref/DC2-Mod-SDK/extractor/DinoCrisis2.h` (Gemini-Loboto3, **CC BY-NC-ND**); that header is cited
  as documentation only — its code is not used, reproduced, or redistributed here.
- `Containers/Lzss.cs` and `Containers/GianPackage.cs` are ported from **our own** MIT code in
  `src/DinoRand.FileFormats` (the DinoRand project), copied to keep this library standalone.
- The reader/writer **structure** (a core library of Builder-pattern format types) is modeled on
  `ref/biohazard-utils` (Ted John, **MIT**); no biohazard-utils code is copied — patterns only.
