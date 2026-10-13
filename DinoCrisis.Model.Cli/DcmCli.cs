using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;

namespace DinoCrisis.Model.Cli;

/// <summary>
/// <c>dcmtool</c> — a thin command-line front for <see cref="DinoCrisis.Model"/>. Verbs operate on any
/// Dino Crisis Gian package (DC1 <c>stNXX.dat</c>/<c>core*.dat</c>, DC2 <c>E*.DAT</c>) via
/// <see cref="DcmLocator.FromPackage"/>. All output goes to the supplied writer so the runner is
/// unit-testable; <c>Program.cs</c> just wires it to <see cref="Console.Out"/>. Returns a process exit
/// code (0 ok, 1 usage error, 2 runtime error, 3 a check failed).
/// </summary>
public static class DcmCli
{
    public static int Run(string[] args, TextWriter o)
    {
        if (args.Length == 0) { Usage(o); return 1; }
        string verb = args[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "info" => Info(args, o),
                "list" => ListDir(args, o),
                "dump" => Dump(args, o),
                "validate" => Validate(args, o),
                "roundtrip" => Roundtrip(args, o),
                "obj" => Obj(args, o),
                "gltf" => Gltf(args, o),
                "import" => Import(args, o),
                "help" or "-h" or "--help" => Usage(o, ok: true),
                _ => Unknown(verb, o),
            };
        }
        catch (FileNotFoundException ex) { o.WriteLine($"error: file not found: {ex.FileName ?? ex.Message}"); return 2; }
        catch (Exception ex) { o.WriteLine($"error: {ex.Message}"); return 2; }
    }

    private static int Usage(TextWriter o, bool ok = false)
    {
        o.WriteLine("dcmtool — read/inspect/export Dino Crisis 1 & 2 DCM models");
        o.WriteLine("usage:");
        o.WriteLine("  dcmtool info      <file>                  list the models in a package");
        o.WriteLine("  dcmtool list      <dir>                   scan a Data directory for model-bearing packages");
        o.WriteLine("  dcmtool dump      <file> [index]          per-part table for one model (default 0)");
        o.WriteLine("  dcmtool validate  <file>                  re-check every model's structural invariants");
        o.WriteLine("  dcmtool roundtrip <file>                  read+re-emit each model; report byte-exactness");
        o.WriteLine("  dcmtool obj       <file> <out.obj> [index] [--no-flip-y] [--tex] [--tex-file <path>]");
        o.WriteLine("                                            export to Wavefront OBJ (--tex also writes .mtl + .tga;");
        o.WriteLine("                                            DC2 finds its sibling E*.TEX, or use --tex-file)");
        o.WriteLine("  dcmtool gltf      <file> <out.glb> [index] [--no-flip-y] [--no-tex] [--tex-file <path>] [--scale <f>]");
        o.WriteLine("                                            export skinned glTF 2.0 (rig + embedded texture) for");
        o.WriteLine("                                            Blender; texture on by default (DC2 sibling E*.TEX)");
        o.WriteLine("  dcmtool import    <edited.glb> <target.dat> <out.dat> [index] [--ceiling <n>] [--tex-file <path>]");
        o.WriteLine("                                            [--no-flip-y] [--scale <f>] [--allow-unsafe-dc2-moved-vertices]");
        o.WriteLine("                                            splice a Blender-edited glTF back over the target model");
        o.WriteLine("                                            (same-rig contract — mesh/UV edits, rig untouched; moved/new");
        o.WriteLine("                                            vertices refused; DC2 unsafe opt-in is runtime-unverified)");
        o.WriteLine("files: DC1 stNXX.dat / core*.dat, DC2 E*.DAT (+ E*.TEX)");
        return ok ? 0 : 1;
    }

    private static int Unknown(string verb, TextWriter o)
    {
        o.WriteLine($"unknown verb '{verb}'");
        Usage(o);
        return 1;
    }

    private static IReadOnlyList<DcmModel> Load(string path, TextWriter o, out bool any)
    {
        var models = DcmLocator.FromPackage(File.ReadAllBytes(path));
        any = models.Count > 0;
        if (!any) o.WriteLine($"no DCM models found in {Path.GetFileName(path)}");
        return models;
    }

    private static int Info(string[] args, TextWriter o)
    {
        if (args.Length < 2) return Usage(o);
        var models = Load(args[1], o, out bool any);
        if (!any) return 3;
        o.WriteLine($"{Path.GetFileName(args[1])}: {models.Count} model(s)");
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            o.WriteLine($"  [{i}] {m.Variant.Game}  {m.Parts.Count} parts  {m.Triangles.Count} tris  " +
                        $"{m.Quads.Count} quads  {m.VertexCount} verts  base 0x{m.LoadBase:X8}");
        }
        return 0;
    }

    private static int Dump(string[] args, TextWriter o)
    {
        if (args.Length < 2) return Usage(o);
        var models = Load(args[1], o, out bool any);
        if (!any) return 3;
        int index = args.Length >= 3 && int.TryParse(args[2], out int idx) ? idx : 0;
        if (index < 0 || index >= models.Count) { o.WriteLine($"index {index} out of range (0..{models.Count - 1})"); return 1; }

        var m = models[index];
        o.WriteLine($"model [{index}] {m.Variant.Game}: {m.Parts.Count} parts, {m.Triangles.Count} tris, " +
                    $"{m.Quads.Count} quads, {m.VertexCount} verts, base 0x{m.LoadBase:X8}");
        o.WriteLine("part |    x     y     z | parent child | tris quads");
        for (int i = 0; i < m.Parts.Count; i++)
        {
            var p = m.Parts[i];
            o.WriteLine($"{i,4} | {p.X,5} {p.Y,5} {p.Z,5} | {p.Parent,6} {p.Child,5:X2} | {p.TriangleCount,4} {p.QuadCount,5}");
        }
        return 0;
    }

    private static int Validate(string[] args, TextWriter o)
    {
        if (args.Length < 2) return Usage(o);
        var models = Load(args[1], o, out bool any);
        if (!any) return 3;
        bool allOk = true;
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            bool ok = DcmModel.Validate(m);
            allOk &= ok;
            o.WriteLine($"  [{i}] {(ok ? "OK  " : "FAIL")} {m.Parts.Count} parts, " +
                        $"Σtri {m.Parts.Sum(p => p.TriangleCount)}=={m.Header.TriangleCount}, " +
                        $"Σquad {m.Parts.Sum(p => p.QuadCount)}=={m.Header.QuadCount}");
        }
        return allOk ? 0 : 3;
    }

    private static int Roundtrip(string[] args, TextWriter o)
    {
        if (args.Length < 2) return Usage(o);
        var models = Load(args[1], o, out bool any);
        if (!any) return 3;
        bool allExact = true;
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            bool exact = m.ToBuilder().Write().AsSpan().SequenceEqual(m.Raw);
            allExact &= exact;
            o.WriteLine($"  [{i}] {(exact ? "BYTE-EXACT" : "DIFFERS")}  ({m.Parts.Count} parts)");
        }
        return allExact ? 0 : 3;
    }

    private static int Obj(string[] args, TextWriter o)
    {
        if (args.Length < 3) return Usage(o);
        string file = args[1], outPath = args[2];
        OutputSafety.Check(outPath, file);
        bool flipY = !args.Contains("--no-flip-y");
        bool tex = args.Contains("--tex");

        // Parse the options after <out.obj>: positional [index], and "--tex-file <path>" (whose value
        // must not be mistaken for the positional index).
        string? texFileArg = null;
        var positional = new List<string>();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--tex-file") { texFileArg = i + 1 < args.Length ? args[++i] : null; continue; }
            if (args[i].StartsWith("--")) continue;
            positional.Add(args[i]);
        }
        int index = positional.Count >= 1 && int.TryParse(positional[0], out int idx) ? idx : 0;

        var models = Load(file, o, out bool any);
        if (!any) return 3;
        if (index < 0 || index >= models.Count) { o.WriteLine($"index {index} out of range (0..{models.Count - 1})"); return 1; }
        var m = models[index];

        if (tex)
        {
            string dir = Path.GetDirectoryName(outPath) is { Length: > 0 } d ? d : ".";
            string baseName = Path.GetFileNameWithoutExtension(outPath);

            // DC2 textures are a separate sibling E*.TEX (e.g. E00.DAT → E00.TEX); --tex-file overrides.
            byte[]? texBytes = null;
            if (m.Variant.Game == DcmGame.Dc2)
            {
                string? texPath = texFileArg ?? SiblingTex(file);
                if (texPath is null || !File.Exists(texPath))
                    o.WriteLine($"note: no .TEX found for {Path.GetFileName(file)} (looked for {Path.GetFileName(texPath ?? "E*.TEX")}); use --tex-file <path>");
                else
                    texBytes = File.ReadAllBytes(texPath);
            }

            bool textured = TexturedObjExporter.Export(File.ReadAllBytes(file), m, dir, baseName, flipY, texBytes);
            o.WriteLine(textured
                ? $"wrote {Path.Combine(dir, baseName)}.{{obj,mtl,tga}}: {m.Triangles.Count + m.Quads.Count} faces, textured [{index}] ({m.Variant.Game})"
                : $"wrote {Path.Combine(dir, baseName)}.obj UNTEXTURED (no resolvable texture; e.g. DC2 needs its .TEX) [{index}]");
            return 0;
        }

        ObjExporter.Export(m, outPath, flipY);
        o.WriteLine($"wrote {outPath}: {m.Triangles.Count + m.Quads.Count} faces from model [{index}] ({m.Variant.Game})");
        return 0;
    }

    /// <summary>Scan a directory's packages for DCM models — the "what can I export?" overview.
    /// Skips files that aren't parseable Gian packages or carry no valid model.</summary>
    private static int ListDir(string[] args, TextWriter o)
    {
        if (args.Length < 2) return Usage(o);
        if (!Directory.Exists(args[1])) { o.WriteLine($"error: not a directory: {args[1]}"); return 2; }

        int packages = 0, total = 0;
        foreach (var path in Directory.EnumerateFiles(args[1]).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // Model-bearing packages in both games are .dat/.DAT; skip textures/movies/etc. cheaply.
            if (!path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
            IReadOnlyList<DcmModel> models;
            try { models = DcmLocator.FromPackage(File.ReadAllBytes(path)); }
            catch { continue; }
            if (models.Count == 0) continue;

            packages++;
            total += models.Count;
            o.WriteLine($"{Path.GetFileName(path)}: {models.Count} model(s)");
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                o.WriteLine($"  [{i}] {m.Variant.Game}  {m.Parts.Count,2} parts  {m.Triangles.Count,4} tris  " +
                            $"{m.Quads.Count,4} quads  {m.VertexCount,4} verts");
            }
        }
        o.WriteLine($"{packages} package(s), {total} model(s)");
        return total > 0 ? 0 : 3;
    }

    /// <summary>Export a model to skinned glTF 2.0 (GLB) via <see cref="Interchange.GltfInterchange"/> —
    /// rig preserved as joints, texture embedded as PNG (DC-MODEL-TOOLKIT-PLAN P1).</summary>
    private static int Gltf(string[] args, TextWriter o)
    {
        if (args.Length < 3) return Usage(o);
        string file = args[1], outPath = args[2];
        OutputSafety.Check(outPath, file);
        bool flipY = !args.Contains("--no-flip-y");
        bool tex = !args.Contains("--no-tex");

        string? texFileArg = null;
        float scale = Interchange.GltfInterchange.DefaultScale;
        var positional = new List<string>();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--tex-file") { texFileArg = i + 1 < args.Length ? args[++i] : null; continue; }
            if (args[i] == "--scale")
            {
                if (i + 1 >= args.Length || !float.TryParse(args[++i],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale))
                { o.WriteLine("error: --scale needs a number"); return 1; }
                continue;
            }
            if (args[i].StartsWith("--")) continue;
            positional.Add(args[i]);
        }
        int index = positional.Count >= 1 && int.TryParse(positional[0], out int idx) ? idx : 0;

        byte[] fileBytes = File.ReadAllBytes(file);
        var models = DcmLocator.FromPackage(fileBytes);
        if (models.Count == 0) { o.WriteLine($"no DCM models found in {Path.GetFileName(file)}"); return 3; }
        if (index < 0 || index >= models.Count) { o.WriteLine($"index {index} out of range (0..{models.Count - 1})"); return 1; }
        var m = models[index];

        byte[]? texBytes = null;
        if (tex && m.Variant.Game == DcmGame.Dc2)
        {
            string? texPath = texFileArg ?? SiblingTex(file);
            if (texPath is null || !File.Exists(texPath))
                o.WriteLine($"note: no .TEX found for {Path.GetFileName(file)}; exporting untextured (use --tex-file <path>)");
            else
                texBytes = File.ReadAllBytes(texPath);
        }

        bool textured = tex && Interchange.GltfInterchange.ExportGlb(fileBytes, m, outPath, scale, flipY, texBytes);
        if (!tex)
            Interchange.GltfInterchange.FromDcm(m, null, scale, flipY,
                Path.GetFileNameWithoutExtension(outPath)).SaveGLB(outPath);
        o.WriteLine($"wrote {outPath}: {m.Parts.Count} joints, {m.Triangles.Count + m.Quads.Count} polys, " +
                    $"{(textured ? "textured" : "UNTEXTURED")} [{index}] ({m.Variant.Game})");
        return 0;
    }

    /// <summary>Splice an edited glTF back over a DC1 model (DC-MODEL-TOOLKIT-PLAN P2 same-rig
    /// import): glTF → <see cref="Interchange.GltfImporter"/> (contract validation + inverse
    /// transforms) → <see cref="Interchange.Dc1ModelSplicer"/> (skeleton+animation preserved).</summary>
    private static int Import(string[] args, TextWriter o)
    {
        if (args.Length < 4) return Usage(o);
        string glbPath = args[1], targetPath = args[2], outPath = args[3];
        OutputSafety.Check(outPath, glbPath, targetPath);
        bool flipY = !args.Contains("--no-flip-y");
        bool allowUnsafeDc2MovedVertices = args.Contains("--allow-unsafe-dc2-moved-vertices");
        float scale = Interchange.GltfInterchange.DefaultScale;
        int ceiling = Interchange.Dc1ModelSplicer.LiftedVertexCeiling;
        string? texFileArg = null;

        var positional = new List<string>();
        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--tex-file") { texFileArg = i + 1 < args.Length ? args[++i] : null; continue; }
            if (args[i] == "--ceiling")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out ceiling))
                { o.WriteLine("error: --ceiling needs a number"); return 1; }
                continue;
            }
            if (args[i] == "--scale")
            {
                if (i + 1 >= args.Length || !float.TryParse(args[++i],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale))
                { o.WriteLine("error: --scale needs a number"); return 1; }
                continue;
            }
            if (args[i].StartsWith("--")) continue;
            positional.Add(args[i]);
        }
        int index = positional.Count >= 1 && int.TryParse(positional[0], out int idx) ? idx : 0;

        byte[] fileBytes = File.ReadAllBytes(targetPath);
        var models = DcmLocator.FromPackage(fileBytes);
        if (models.Count == 0) { o.WriteLine($"no DCM models found in {Path.GetFileName(targetPath)}"); return 3; }
        if (index < 0 || index >= models.Count) { o.WriteLine($"index {index} out of range (0..{models.Count - 1})"); return 1; }
        var target = models[index];
        if (allowUnsafeDc2MovedVertices && target.Variant.Game != DcmGame.Dc2)
        {
            o.WriteLine("error: --allow-unsafe-dc2-moved-vertices applies only to DC2 imports");
            return 1;
        }

        // Texture context for the UV inversion: DC1 from the package itself, DC2 from the
        // sibling E*.TEX (or --tex-file), mirroring the gltf verb.
        Textures.DcmTexture? tex;
        if (target.Variant.Game == DcmGame.Dc2)
        {
            string? texPath = texFileArg ?? SiblingTex(targetPath);
            tex = texPath is not null && File.Exists(texPath)
                ? Textures.DcmTextureDecoder.TryDecodeDc2(File.ReadAllBytes(texPath), target)
                : null;
        }
        else
        {
            var pkg = Containers.GianPackage.TryParse(fileBytes);
            tex = pkg is null ? null : Textures.DcmTextureDecoder.TryDecode(pkg, fileBytes, target);
        }
        if (tex is null) o.WriteLine("note: target texture not decodable — UVs imported without page reconstruction");

        try
        {
            var mesh = Interchange.GltfImporter.ReadSameRig(glbPath, target, tex, scale, flipY);
            foreach (var w in mesh.Warnings) o.WriteLine($"warning: {w}");

            int dc1Ordinal = -1;
            var result = target.Variant.Game == DcmGame.Dc2
                ? Interchange.Dc2ModelSplicer.Splice(
                    fileBytes, target.LoadBase, mesh, allowUnsafeDc2MovedVertices)
                : Interchange.Dc1ModelSplicer.Splice(
                    fileBytes,
                    _ => ++dc1Ordinal == index,
                    mesh,
                    ceiling);

            File.WriteAllBytes(outPath, result.Package);
            o.Write(result.Report);
            o.WriteLine($"wrote {outPath}");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            WriteUnsafeWarning();
            o.WriteLine($"refused: {ex.Message}");
            return 3;
        }
        catch (InvalidOperationException ex)
        {
            WriteUnsafeWarning();
            o.WriteLine($"refused: {ex.Message}");
            return 3;
        }

        void WriteUnsafeWarning()
        {
            if (allowUnsafeDc2MovedVertices)
                o.WriteLine("WARNING: unsafe DC2 moved/new vertex opt-in selected; runtime binding is unverified.");
        }
    }

    /// <summary>The sibling <c>E*.TEX</c> next to a DC2 model file (<c>E00.DAT</c> → <c>E00.TEX</c>),
    /// trying the game's uppercase casing then lowercase; null if neither exists.</summary>
    private static string? SiblingTex(string datPath)
    {
        foreach (var ext in new[] { ".TEX", ".tex" })
        {
            string p = Path.ChangeExtension(datPath, ext);
            if (File.Exists(p)) return p;
        }
        return Path.ChangeExtension(datPath, ".TEX");  // report the canonical name even when absent
    }
}
