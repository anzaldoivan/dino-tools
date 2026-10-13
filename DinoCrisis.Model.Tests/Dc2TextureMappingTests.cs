using System;
using System.Buffers.Binary;
using System.IO;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Increment 2 of the DC2 .TEX feature (docs/dc1/DCM-DC2-TEXTURE-PLAN.md): a DC2 model's header
/// tpage/clut + per-corner UVs map into the decoded .TEX texture via the existing DcmTexture.MapUv.
/// Acceptance gate (plan D4): every DC2 model UV lands in [0,1]. Synthetic runs anywhere; the golden
/// re-derives it from real E*.DAT + E*.TEX and is skipped when game data is absent.
/// </summary>
public sealed class Dc2TextureMappingTests
{
    /// <summary>Minimal single-page .TEX with a configurable texture rect (so it can be made to match a
    /// model's tpage) and one full 256-entry palette. CI-safe.</summary>
    private static byte[] BuildTex(int x16, int y, int w16, int h)
    {
        const int headerAlign = 0x800, texAlign = 0x1000;
        int texSize = w16 * 2 * h;
        int texOff = (headerAlign + 512 + (texAlign - 1)) & ~(texAlign - 1); // 0x1000
        var b = new byte[texOff + texSize];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
        W32(0x00, 1); W32(0x04, (uint)texSize); W16(0x08, (ushort)x16); W16(0x0a, (ushort)y); W16(0x0c, (ushort)w16); W16(0x0e, (ushort)h);
        W32(0x20, 2); W32(0x24, 512); W16(0x28, 0); W16(0x2a, 511); W16(0x2c, 256); W16(0x2e, 1);
        for (int i = 0x40; i < headerAlign; i++) b[i] = (byte)'d';
        for (int i = 0; i < 256; i++) W16(headerAlign + i * 2, (ushort)(0x0421 * (i + 1)));
        for (int i = 0; i < texSize; i++) b[texOff + i] = 1; // index 1 everywhere
        return b;
    }

    private static void AssertEveryUvInRange(DcmModel model, DcmTexture tex)
    {
        ushort tpage = model.Header.TPage;
        void Check(Uv uv)
        {
            var (s, t) = tex.MapUv(uv.U, uv.V, tpage);
            Assert.InRange(s, 0.0, 1.0);
            Assert.InRange(t, 0.0, 1.0);
        }
        foreach (var tr in model.Triangles) { Check(tr.Uv0); Check(tr.Uv1); Check(tr.Uv2); }
        foreach (var q in model.Quads) { Check(q.Uv0); Check(q.Uv1); Check(q.Uv2); Check(q.Uv3); }
    }

    [Fact]
    public void Synthetic_Dc2ModelUvsMapIntoTexture()
    {
        var v = DcmVariant.Dc2;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        // SyntheticBlob's DC2 header tpage=158 (0x9E) encodes VRAM page (896,256); match the .TEX rect.
        var tex = DcmTextureDecoder.TryDecodeDc2(BuildTex(896, 256, 128, 256), model);
        Assert.NotNull(tex);
        Assert.Equal(896, tex!.EntryX16);
        Assert.Equal(256, tex.EntryY);
        AssertEveryUvInRange(model, tex);
    }

    [Fact]
    public void TryDecodeDc2_RejectsDc1ModelAndBadDepth()
    {
        var dc1 = DcmModel.TryReadAt(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1)!;
        Assert.Null(DcmTextureDecoder.TryDecodeDc2(BuildTex(896, 256, 128, 256), dc1)); // DC1 model → null
        Assert.Null(DcmTextureDecoder.TryDecodeDc2(Array.Empty<byte>(),
            DcmModel.TryReadAt(SyntheticBlob.Build(DcmVariant.Dc2), 0, SyntheticBlob.Base, DcmVariant.Dc2)!)); // unparseable .TEX → null
    }

    [Dc2Theory("E00.DAT", "E00.TEX", "E90.DAT", "E90.TEX")]
    [InlineData("E00.DAT", "E00.TEX")]  // Velociraptor
    [InlineData("E90.DAT", "E90.TEX")]  // Oviraptor
    public void Golden_EveryRealModelUvMapsIntoSkin(string dat, string tex)
    {
        string dp = FixtureGate.Dc2File(dat);
        string tp = FixtureGate.Dc2File(tex);

        var model = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(TestData.Pristine(dp)));
        var texture = DcmTextureDecoder.TryDecodeDc2(File.ReadAllBytes(tp), model);
        Assert.NotNull(texture);
        Assert.Equal(256, texture!.Image.Width);
        Assert.Equal(256, texture.Image.Height);
        AssertEveryUvInRange(model, texture);   // the D4 acceptance gate
    }

    [Dc2Fact("E00.DAT", "E00_02.TEX")]
    public void Golden_MultiPageVariant_DecodesTheModelsPage()
    {
        string dp = FixtureGate.Dc2File("E00.DAT");
        string tp = FixtureGate.Dc2File("E00_02.TEX"); // 2-page variant skin

        var model = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(TestData.Pristine(dp)));
        var texture = DcmTextureDecoder.TryDecodeDc2(File.ReadAllBytes(tp), model);
        Assert.NotNull(texture);                       // multi-page .TEX now parses (was rejected)
        Assert.Equal(896, texture!.EntryX16);          // selected the page the model's tpage points at
        Assert.Equal(256, texture.Image.Width);
        Assert.Equal(256, texture.Image.Height);
        Assert.True(texture.Image.HasOpaquePixels());
        AssertEveryUvInRange(model, texture);
    }

    // ---- increment 3: textured exporter wiring ----

    [Fact]
    public void TexturedExport_Dc2_WithTexBytes_WritesTexturedObj()
    {
        var v = DcmVariant.Dc2;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        string dir = Path.Combine(Path.GetTempPath(), $"dc2tex_{Guid.NewGuid():N}");
        try
        {
            bool textured = TexturedObjExporter.Export(
                fileBytes: SyntheticBlob.Build(v), model, dir, "raptor", flipY: true,
                texFileBytes: BuildTex(896, 256, 128, 256));
            Assert.True(textured);
            Assert.True(File.Exists(Path.Combine(dir, "raptor.mtl")));
            Assert.True(new FileInfo(Path.Combine(dir, "raptor.tga")).Length > 1000);
            string obj = File.ReadAllText(Path.Combine(dir, "raptor.obj"));
            Assert.Contains("usemtl dcm", obj);
            Assert.Contains("\nvt ", obj);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TexturedExport_Dc2_WithoutTexBytes_FallsBackToUntextured()
    {
        var v = DcmVariant.Dc2;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        string dir = Path.Combine(Path.GetTempPath(), $"dc2notex_{Guid.NewGuid():N}");
        try
        {
            bool textured = TexturedObjExporter.Export(SyntheticBlob.Build(v), model, dir, "raptor"); // no .TEX
            Assert.False(textured);
            Assert.True(File.Exists(Path.Combine(dir, "raptor.obj")));
            Assert.False(File.Exists(Path.Combine(dir, "raptor.mtl")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
