using System.Numerics;
using DinoCrisis.Interchange;
using DinoCrisis.Model.Cli;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class CoreSafetyCliTests
{
    [Fact]
    public void Help_documents_explicit_dc2_moved_vertex_unsafe_flag()
    {
        var output = new StringWriter();

        int code = DcmCli.Run(new[] { "help" }, output);

        Assert.Equal(0, code);
        Assert.Contains("--allow-unsafe-dc2-moved-vertices", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dc2_moved_vertex_flag_is_required_and_emits_warning_when_selected()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dinorand-core-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] blob = SyntheticBlob.Build(DcmVariant.Dc2);
            byte[] compressed = Lzss.Compress(blob);
            byte[] package = new GianPackageBuilder()
                .Add(GianEntryType.Lzss0, compressed,
                    (ushort)(SyntheticBlob.Base & 0xFFFF), (ushort)(SyntheticBlob.Base >> 16), 0, 0)
                .BuildDc2();
            string targetPath = Path.Combine(dir, "target.DAT");
            File.WriteAllBytes(targetPath, package);

            var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc2);
            var gltf = GltfInterchange.FromDcm(target);
            var positions = gltf.LogicalMeshes[0].Primitives[0]
                .GetVertexAccessor("POSITION")!.AsVector3Array();
            positions[0] += new Vector3(GltfInterchange.DefaultScale, 0, 0);
            string glbPath = Path.Combine(dir, "edited.glb");
            gltf.SaveGLB(glbPath);

            var normalOutput = new StringWriter();
            int normalCode = DcmCli.Run(
                new[] { "import", glbPath, targetPath, Path.Combine(dir, "normal.DAT") }, normalOutput);
            Assert.Equal(3, normalCode);
            Assert.Contains("moved/new", normalOutput.ToString(), StringComparison.OrdinalIgnoreCase);

            var unsafeOutput = new StringWriter();
            int unsafeCode = DcmCli.Run(
                new[]
                {
                    "import", glbPath, targetPath, Path.Combine(dir, "unsafe.DAT"),
                    "--allow-unsafe-dc2-moved-vertices",
                },
                unsafeOutput);
            Assert.Equal(3, unsafeCode); // synthetic identity budget has no room for the appended record
            Assert.Contains("WARNING", unsafeOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Refused by default", unsafeOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Dc1_import_index_selects_the_exact_model_when_signatures_are_identical()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dinorand-cli-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
            byte[] package = new GianPackageBuilder()
                .AddData(blob)
                .AddData(blob)
                .Build();
            string targetPath = Path.Combine(dir, "target.dat");
            string outputPath = Path.Combine(dir, "output.dat");
            string glbPath = Path.Combine(dir, "edit.glb");
            File.WriteAllBytes(targetPath, package);

            var targets = DcmLocator.FromPackage(package);
            Assert.Equal(2, targets.Count);
            var gltf = GltfInterchange.FromDcm(targets[1]);
            var uvs = gltf.LogicalMeshes[0].Primitives[0]
                .GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
            uvs[0] += new Vector2(1f / 256f, 0);
            gltf.SaveGLB(glbPath);

            var output = new StringWriter();
            int code = DcmCli.Run(
                new[] { "import", glbPath, targetPath, outputPath, "1" }, output);

            Assert.True(code == 0, output.ToString());
            var originalPackage = GianPackage.TryParse(package)!;
            byte[] editedBytes = File.ReadAllBytes(outputPath);
            var editedPackage = GianPackage.TryParse(editedBytes)!;
            Assert.Equal(
                originalPackage.DecodeEntry(package, originalPackage.Entries[0]),
                editedPackage.DecodeEntry(editedBytes, editedPackage.Entries[0]));
            Assert.False(
                originalPackage.DecodeEntry(package, originalPackage.Entries[1])
                    .SequenceEqual(editedPackage.DecodeEntry(editedBytes, editedPackage.Entries[1])));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
