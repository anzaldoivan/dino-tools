using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class TextureTests
{
    private static DcmModel? Dc1(string file, Func<DcmModel, bool> pick)
        => DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File(file)))).FirstOrDefault(pick);

    // ---- ungated PSX color decode ----

    [Theory]
    [InlineData(0x0000, 0, 0, 0, 0)]     // raw 0 = transparent
    [InlineData(0x001F, 255, 0, 0, 255)] // red 5 bits
    [InlineData(0x03E0, 0, 255, 0, 255)] // green
    [InlineData(0x7C00, 0, 0, 255, 255)] // blue
    public void PsxColor_DecodesBgr555(ushort psx, int r, int g, int b, int a)
    {
        var (R, G, B, A) = PsxImage.FromPsv(psx);
        Assert.Equal((r, g, b, a), (R, G, B, A));
    }

    [Theory]
    [InlineData(GianEntryType.Lzss1)]
    [InlineData(GianEntryType.Lzss2)]
    public void Compressed_texture_decode_unswizzles_tiles_including_partial_final_band(
        GianEntryType entryType)
    {
        const int rowBytes = 128;
        const int visibleHeight = 33;
        const int storedHeight = 64;
        var linear = new byte[rowBytes * storedHeight];
        linear[0 * rowBytes + 0] = 1;
        linear[31 * rowBytes + 63] = 2;
        linear[32 * rowBytes + 0] = 3;
        linear[32 * rowBytes + 64] = 4;

        var swizzled = new byte[linear.Length];
        int cursor = 0;
        for (int tileY = 0; tileY < storedHeight; tileY += 32)
            for (int tileX = 0; tileX < rowBytes; tileX += 64)
                for (int row = 0; row < 32; row++)
                {
                    System.Buffer.BlockCopy(linear, (tileY + row) * rowBytes + tileX,
                        swizzled, cursor, 64);
                    cursor += 64;
                }

        var palette = new byte[512];
        void SetColor(int index, ushort color)
        {
            palette[index * 2] = (byte)color;
            palette[index * 2 + 1] = (byte)(color >> 8);
        }
        SetColor(1, 0x001F); // red
        SetColor(2, 0x03E0); // green
        SetColor(3, 0x7C00); // blue
        SetColor(4, 0x7FFF); // white

        var packageBytes = new GianPackageBuilder()
            .Add(entryType, Lzss.Compress(swizzled), 0, 0, rowBytes / 2, visibleHeight)
            .AddPalette(palette, 0, 0, 256, 1)
            .AddData(new byte[] { 1 })
            .Build();
        var package = GianPackage.TryParse(packageBytes)!;
        var model = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0,
            SyntheticBlob.Base, DcmVariant.Dc1);
        model.Triangles[0].TPage = 0x0080; // 8bpp page at VRAM (0,0)
        model.Triangles[0].Clut = 0;
        // This test covers the representative single-CLUT decoder.  The synthetic blob fills
        // untouched material words with sentinel bytes, so normalize every primitive explicitly;
        // a genuinely mixed-CLUT DC1 model must use TryDecodePageAware instead.
        foreach (var triangle in model.Triangles)
        {
            triangle.TPage = 0x0080;
            triangle.Clut = 0;
        }
        foreach (var quad in model.Quads)
        {
            quad.TPage = 0x0080;
            quad.Clut = 0;
        }

        var texture = DcmTextureDecoder.TryDecode(package, packageBytes, model)!;

        Assert.Equal((255, 0, 0, 255), Pixel(texture.Image, 0, 0));
        Assert.Equal((0, 255, 0, 255), Pixel(texture.Image, 63, 31));
        Assert.Equal((0, 0, 255, 255), Pixel(texture.Image, 0, 32));
        Assert.Equal((255, 255, 255, 255), Pixel(texture.Image, 64, 32));

        static (int R, int G, int B, int A) Pixel(PsxImage image, int x, int y)
        {
            int offset = (y * image.Width + x) * 4;
            return (image.Pixels[offset], image.Pixels[offset + 1],
                image.Pixels[offset + 2], image.Pixels[offset + 3]);
        }
    }

    [Fact]
    public void Tga_RoundTripsDimensionsAndIsReadable()
    {
        var img = new PsxImage(4, 3);
        img.Set(1, 1, 10, 20, 30, 255);
        string path = Path.Combine(Path.GetTempPath(), $"px_{Guid.NewGuid():N}.tga");
        try
        {
            img.WriteTga(path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(18 + 4 * 3 * 4, bytes.Length);  // header + 4x3 BGRA
            Assert.Equal(4, bytes[12] | (bytes[13] << 8)); // width
            Assert.Equal(3, bytes[14] | (bytes[15] << 8)); // height
            Assert.Equal(32, bytes[16]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- gated golden texture decode ----

    [Dc1Fact("core00.dat")]
    public void Regina_TextureDecodes_256x464_WithContent()
    {
        string path = TestData.Pristine(FixtureGate.Dc1File("core00.dat"));
        var model = Dc1("core00.dat", m => m.Header.PartCount == 15 && m.Header.TriangleCount == 314)!;
        byte[] bytes = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(bytes)!;
        var tex = DcmTextureDecoder.TryDecode(pkg, bytes, model);

        Assert.NotNull(tex);
        Assert.Equal(256, tex!.Image.Width);  // entry w=128 words * 2 (8bpp)
        Assert.Equal(464, tex.Image.Height);
        Assert.True(tex.Image.HasOpaquePixels());
        Assert.Equal("AC40DE97EED8CAD289D36AE47D6756B315FAF9D1951E2886B747B9531C6F9223",
            System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tex.Image.Pixels)));
    }

    [Dc1Fact("st106.dat")]
    public void Raptor_TextureDecodes_128x512_WithContent()
    {
        var model = Dc1("st106.dat", m => m.Header.PartCount == 15 && m.Header.TriangleCount == 282 && m.Header.QuadCount == 179)!;
        byte[] bytes = File.ReadAllBytes(FixtureGate.Dc1File("st106.dat"));
        var tex = DcmTextureDecoder.TryDecode(GianPackage.TryParse(bytes)!, bytes, model);

        Assert.NotNull(tex);
        Assert.Equal(128, tex!.Image.Width);
        Assert.Equal(512, tex.Image.Height);
        Assert.True(tex.Image.HasOpaquePixels());

        // every polygon's UVs map inside [0,1]
        foreach (var t in model.Triangles)
            foreach (var uvp in new[] { t.Uv0, t.Uv1, t.Uv2 })
            {
                var (s, tt) = tex.MapUv(uvp.U, uvp.V, t.TPage);
                Assert.InRange(s, 0.0, 1.0);
                Assert.InRange(tt, 0.0, 1.0);
            }
    }

    [Dc1Fact("st105.dat")]
    public void FourBpp_ModelTextureDecodes_WithSixteenColorClut()
    {
        byte[] bytes = File.ReadAllBytes(FixtureGate.Dc1File("st105.dat"));
        // the single-part 36-poly object whose polygons all sample a 4bpp page (depthMode 0).
        var model = DcmLocator.FromDc1Package(bytes)
            .FirstOrDefault(m => m.Parts.Count == 1 && m.Triangles.Count + m.Quads.Count == 36);
        Assert.NotNull(model);

        var tex = DcmTextureDecoder.TryDecode(GianPackage.TryParse(bytes)!, bytes, model!);
        Assert.NotNull(tex);                       // was null before 4bpp support
        Assert.Equal(4, tex!.TexelsPerWord);       // 4bpp: 4 texels per 16-bit VRAM word
        Assert.Equal(896, tex.Image.Width);        // GfxW words * 4
        Assert.Equal(512, tex.Image.Height);
        Assert.True(tex.Image.HasOpaquePixels());

        // a 4bpp page indexes a 16-colour CLUT → at most 16 distinct decoded colours
        int distinct = Enumerable.Range(0, tex.Image.Width * tex.Image.Height)
            .Select(i => System.BitConverter.ToInt32(tex.Image.Pixels, i * 4)).Distinct().Count();
        Assert.InRange(distinct, 2, 16);

        // every UV maps inside [0,1] via the texelsPerWord=4 path
        foreach (var t in model!.Triangles)
            foreach (var u in new[] { t.Uv0, t.Uv1, t.Uv2 })
            { var (s, tt) = tex.MapUv(u.U, u.V, t.TPage); Assert.InRange(s, 0.0, 1.0); Assert.InRange(tt, 0.0, 1.0); }
        foreach (var q in model.Quads)
            foreach (var u in new[] { q.Uv0, q.Uv1, q.Uv2, q.Uv3 })
            { var (s, tt) = tex.MapUv(u.U, u.V, q.TPage); Assert.InRange(s, 0.0, 1.0); Assert.InRange(tt, 0.0, 1.0); }
    }

    [Dc1Fact("core00.dat")]
    public void TexturedExport_WritesObjMtlTga()
    {
        var model = Dc1("core00.dat", m => m.Header.PartCount == 15 && m.Header.TriangleCount == 314)!;
        byte[] bytes = File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File("core00.dat")));
        string dir = Path.Combine(Path.GetTempPath(), $"dcmtex_{Guid.NewGuid():N}");
        try
        {
            bool textured = TexturedObjExporter.Export(bytes, model, dir, "regina");
            Assert.True(textured);
            Assert.True(File.Exists(Path.Combine(dir, "regina.obj")));
            Assert.True(File.Exists(Path.Combine(dir, "regina.mtl")));
            Assert.True(new FileInfo(Path.Combine(dir, "regina.tga")).Length > 1000);

            string obj = File.ReadAllText(Path.Combine(dir, "regina.obj"));
            Assert.Contains("mtllib regina.mtl", obj);
            Assert.Contains("usemtl dcm", obj);
            Assert.Contains("\nvt ", obj);
            Assert.Contains("map_Kd regina.tga", File.ReadAllText(Path.Combine(dir, "regina.mtl")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
