using System.IO;
using DinoCrisis.Model.Cli;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class CliTests
{
    private static (int code, string output) Run(params string[] args)
    {
        var sw = new StringWriter();
        int code = DcmCli.Run(args, sw);
        return (code, sw.ToString());
    }

    // ---- ungated (run anywhere) ----

    [Fact]
    public void NoArgs_PrintsUsage_NonZero()
    {
        var (code, output) = Run();
        Assert.Equal(1, code);
        Assert.Contains("usage:", output);
    }

    [Fact]
    public void UnknownVerb_NonZero()
    {
        var (code, output) = Run("frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("unknown verb", output);
    }

    [Fact]
    public void Help_IsZeroAndPrintsUsage()
    {
        var (code, output) = Run("help");
        Assert.Equal(0, code);
        Assert.Contains("dcmtool", output);
    }

    [Fact]
    public void MissingFile_RuntimeError()
    {
        var (code, _) = Run("info", "/no/such/file.dat");
        Assert.Equal(2, code);
    }

    [Fact]
    public void Gltf_MissingArgs_PrintsUsage()
    {
        var (code, output) = Run("gltf", "only-one-arg.dat");
        Assert.Equal(1, code);
        Assert.Contains("usage:", output);
    }

    [Fact]
    public void List_NotADirectory_RuntimeError()
    {
        var (code, output) = Run("list", "/no/such/dir");
        Assert.Equal(2, code);
        Assert.Contains("not a directory", output);
    }

    [Fact]
    public void Import_MissingArgs_PrintsUsage()
    {
        var (code, output) = Run("import", "edited.glb", "core00.dat");
        Assert.Equal(1, code);
        Assert.Contains("usage:", output);
    }

    // ---- gated (need DC1 game data) ----

    [Dc1Fact("st605.dat")]
    public void Info_RealFile_ListsModels()
    {
        var (code, output) = Run("info", FixtureGate.Dc1File("st605.dat"));
        Assert.Equal(0, code);
        Assert.Contains("parts", output);
        Assert.Contains("Dc1", output);
    }

    [Dc1Fact("st605.dat")]
    public void Roundtrip_RealFile_AllByteExact()
    {
        var (code, output) = Run("roundtrip", FixtureGate.Dc1File("st605.dat"));
        Assert.Equal(0, code);                  // 0 only if every model re-emits byte-exactly
        Assert.Contains("BYTE-EXACT", output);
        Assert.DoesNotContain("DIFFERS", output);
    }

    [Dc1Fact("st605.dat")]
    public void Obj_RealFile_WritesUsableObj()
    {
        string outPath = Path.Combine(Path.GetTempPath(), $"dcmtool_test_{Guid.NewGuid():N}.obj");
        try
        {
            var (code, output) = Run("obj", FixtureGate.Dc1File("st605.dat"), outPath);
            Assert.Equal(0, code);
            Assert.Contains("faces", output);
            Assert.True(File.Exists(outPath));
            Assert.Contains("\nf ", File.ReadAllText(outPath));
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Dc1Fact("core00.dat")]
    public void ObjTex_RealFile_WritesTexturedSet()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dcmcli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (code, output) = Run("obj", TestData.Pristine(FixtureGate.Dc1File("core00.dat")),
                                     Path.Combine(dir, "regina.obj"), "--tex");
            Assert.Equal(0, code);
            Assert.Contains("textured", output);
            Assert.True(File.Exists(Path.Combine(dir, "regina.tga")));
            Assert.True(File.Exists(Path.Combine(dir, "regina.mtl")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Dc2Fact("E00.DAT", "E00.TEX")]
    public void ObjTex_Dc2RealFile_AutoLocatesSiblingTex()
    {
        string e00 = FixtureGate.Dc2File("E00.DAT");
        string tex = FixtureGate.Dc2File("E00.TEX");
        string dir = Path.Combine(Path.GetTempPath(), $"dcmcli_dc2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Copy the PRISTINE pair under their real names so sibling discovery still sees
            // "E00.DAT" + "E00.TEX" (the live E00.DAT may hold an injected model).
            File.Copy(TestData.Pristine(e00), Path.Combine(dir, "E00.DAT"));
            File.Copy(tex, Path.Combine(dir, "E00.TEX"));

            // no --tex-file: the CLI must find the sibling E00.TEX next to E00.DAT.
            var (code, output) = Run("obj", Path.Combine(dir, "E00.DAT"), Path.Combine(dir, "raptor.obj"), "--tex");
            Assert.Equal(0, code);
            Assert.Contains("textured", output);
            Assert.DoesNotContain("UNTEXTURED", output);
            Assert.True(new FileInfo(Path.Combine(dir, "raptor.tga")).Length > 1000);
            Assert.True(File.Exists(Path.Combine(dir, "raptor.mtl")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Dc1Fact("core00.dat")]
    public void Gltf_RealFile_WritesSkinnedGlb()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dcmcli_gltf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string outPath = Path.Combine(dir, "regina.glb");
            var (code, output) = Run("gltf",
                TestData.Pristine(FixtureGate.Dc1File("core00.dat")), outPath);
            Assert.Equal(0, code);
            Assert.Contains("15 joints", output);
            Assert.Contains("textured", output);
            // GLB magic ("glTF") proves the writer produced a real binary glTF.
            byte[] head = new byte[4];
            using (var fs = File.OpenRead(outPath)) fs.ReadExactly(head);
            Assert.Equal("glTF"u8.ToArray(), head);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Dc1Fact("st605.dat")]
    public void List_RealDataDir_FindsModels()
    {
        var (code, output) = Run("list", FixtureGate.Dc1DataDirectory());
        Assert.Equal(0, code);
        Assert.Contains("st605.dat", output);
        Assert.Contains("model(s)", output);
    }

    [Dc1Fact("core00.dat")]
    public void Import_RealRoundTrip_SplicesAndReloads()
    {
        string core = TestData.Pristine(FixtureGate.Dc1File("core00.dat"));
        string dir = Path.Combine(Path.GetTempPath(), $"dcmcli_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Find the Regina model's index so gltf-export and import target the same model.
            var (icode, info) = Run("info", core);
            Assert.Equal(0, icode);
            int index = -1;
            foreach (var line in info.Split('\n'))
                if (line.Contains("15 parts") && line.Contains("314 tris"))
                { index = int.Parse(line.TrimStart()[1..line.TrimStart().IndexOf(']')]); break; }
            Assert.True(index >= 0, $"Regina model not found in info output:\n{info}");

            string glb = Path.Combine(dir, "regina.glb");
            string outDat = Path.Combine(dir, "core00.injected.dat");
            Assert.Equal(0, Run("gltf", core, glb, index.ToString()).code);

            var (code, output) = Run("import", glb, core, outDat, index.ToString());
            Assert.Equal(0, code);
            Assert.Contains("spliced:", output);
            Assert.Contains("skeleton+animation verbatim", output);

            // The injected package must reload and validate through the standard verbs.
            Assert.Equal(0, Run("validate", outDat).code);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Dc1Fact("st605.dat")]
    public void Validate_RealFile_AllOk()
    {
        var (code, output) = Run("validate", FixtureGate.Dc1File("st605.dat"));
        Assert.Equal(0, code);
        Assert.Contains("OK", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
