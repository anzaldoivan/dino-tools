using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Increment 1 of the DC2 .TEX feature (docs/dc1/DCM-DC2-TEXTURE-PLAN.md): the Dc2TexFile reader resolves
/// each data block's offset/size from the 32-byte descriptor table + the packing alignments — never
/// hardcoded — and decodes the single 8bpp framebuffer through its CLUT. Synthetic fixture runs anywhere;
/// the golden test re-derives the §3 witnesses from real E*.TEX and is skipped when game data is absent.
/// </summary>
public sealed class Dc2TexFileTests
{
    /// <summary>Hand-build a minimal single-page .TEX: one TEXTURE (W16×2 by H, 8bpp indices) + one
    /// PALETTE (256 BGR555), laid out exactly as the reader expects (header→0x800, palette, texture@0x1000).
    /// CI-safe — no copyrighted bytes.</summary>
    private static byte[] BuildSyntheticTex(int w16, int h, ushort[] palette, byte[] indices)
    {
        const int recStride = 0x20, headerAlign = 0x800, texAlign = 0x1000;
        int texSize = w16 * 2 * h;
        int palOff = headerAlign;                 // 0x800
        int texOff = (palOff + 512 + (texAlign - 1)) & ~(texAlign - 1); // 0x1000
        int total = texOff + texSize;
        var b = new byte[total];

        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);

        // desc0: TEXTURE
        W32(0x00, 1); W32(0x04, (uint)texSize); W16(0x08, 10); W16(0x0a, 20); W16(0x0c, (ushort)w16); W16(0x0e, (ushort)h);
        // desc1: PALETTE
        W32(0x20, 2); W32(0x24, 512); W16(0x28, 0); W16(0x2a, 30); W16(0x2c, 256); W16(0x2e, 1);
        // desc terminator: "dummy header" filler (non-{1,2} tag) — like the real files
        for (int i = 0x40; i < headerAlign; i++) b[i] = (byte)'d';

        for (int i = 0; i < 256; i++) W16(palOff + i * 2, palette[i]);
        Array.Copy(indices, 0, b, texOff, indices.Length);
        return b;
    }

    [Fact]
    public void Synthetic_ResolvesBlockOffsetsFromTable()
    {
        var pal = new ushort[256];
        for (int i = 0; i < 256; i++) pal[i] = (ushort)(0x0421 * (i + 1)); // distinct, non-zero
        var idx = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };  // 4×2
        var tex = Dc2TexFile.Read(BuildSyntheticTex(w16: 2, h: 2, pal, idx));

        Assert.NotNull(tex);
        var t = tex!.Blocks.Single(x => x.Type == Dc2TexBlockType.Texture);
        var p = tex.Blocks.Single(x => x.Type == Dc2TexBlockType.Palette);
        Assert.Equal((0x1000, 8), (t.DataOffset, t.DataSize)); // computed, not hardcoded
        Assert.Equal((2, 2), (t.W16, t.H));
        Assert.Equal((0x800, 512), (p.DataOffset, p.DataSize));
    }

    [Fact]
    public void Synthetic_DecodesIndicesThroughClut()
    {
        var pal = new ushort[256];
        for (int i = 0; i < 256; i++) pal[i] = (ushort)(0x0421 * (i + 1));
        var idx = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var tex = Dc2TexFile.Read(BuildSyntheticTex(w16: 2, h: 2, pal, idx))!;

        var dt = tex.DecodeSinglePage();
        Assert.NotNull(dt);
        Assert.Equal(4, dt!.Image.Width);
        Assert.Equal(2, dt.Image.Height);

        // pixel (col,row) must equal the CLUT lookup of its index
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 4; col++)
            {
                byte index = idx[row * 4 + col];
                var (r, g, b, a) = PsxImage.FromPsv(pal[index]);
                int o = (row * 4 + col) * 4;
                Assert.Equal(new[] { r, g, b, a }, dt.Image.Pixels.Skip(o).Take(4).ToArray());
            }
    }

    [Fact]
    public void Synthetic_RejectsTruncatedLayout()
    {
        var pal = new ushort[256];
        var blob = BuildSyntheticTex(2, 2, pal, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Null(Dc2TexFile.Read(blob[..^1]));  // file shorter than the packed layout → doesn't close
        Assert.Null(Dc2TexFile.Read(Array.Empty<byte>()));
    }

    [Fact]
    public void Synthetic_Rejects_descriptor_size_that_overflows_signed_offsets()
    {
        var blob = new byte[0x1000];
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x00, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x04, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0c, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0e, 2), 1);

        Assert.Null(Dc2TexFile.Read(blob));
    }

    /// <summary>Hand-build a two-page .TEX: tex0 @VRAM x896 (all index 10) + pal0 @y511, tex1 @x768
    /// (all index 20) + pal1 @y510, with a sub-block trailing pad — like the real E00_02 variant skins.</summary>
    private static byte[] BuildMultiTex()
    {
        const int headerAlign = 0x800, texAlign = 0x1000;
        int w16 = 2, h = 2, texSize = w16 * 2 * h;
        int pal0 = headerAlign, pal1 = pal0 + 512;
        int tex0 = (pal1 + 512 + (texAlign - 1)) & ~(texAlign - 1); // 0x1000
        int tex1 = tex0 + texSize;
        var b = new byte[tex1 + texSize + 0x40]; // + trailing pad (< one framebuffer)
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
        W32(0x00, 1); W32(0x04, (uint)texSize); W16(0x08, 896); W16(0x0a, 256); W16(0x0c, (ushort)w16); W16(0x0e, (ushort)h);
        W32(0x20, 2); W32(0x24, 512); W16(0x28, 0); W16(0x2a, 511); W16(0x2c, 256); W16(0x2e, 1);
        W32(0x40, 1); W32(0x44, (uint)texSize); W16(0x48, 768); W16(0x4a, 256); W16(0x4c, (ushort)w16); W16(0x4e, (ushort)h);
        W32(0x60, 2); W32(0x64, 512); W16(0x68, 0); W16(0x6a, 510); W16(0x6c, 256); W16(0x6e, 1);
        for (int i = 0x80; i < headerAlign; i++) b[i] = (byte)'d';
        W16(pal0 + 10 * 2, 0x001F); // pal0[10] = red
        W16(pal0 + 20 * 2, 0x7C00); // pal0[20] = blue
        W16(pal1 + 10 * 2, 0x03E0); // pal1[10] = green
        for (int i = 0; i < texSize; i++) { b[tex0 + i] = 10; b[tex1 + i] = 20; }
        return b;
    }

    [Fact]
    public void Synthetic_MultiPage_DecodeAtSelectsPageAndPalette()
    {
        var tex = Dc2TexFile.Read(BuildMultiTex());
        Assert.NotNull(tex);
        Assert.Equal(2, tex!.Blocks.Count(b => b.Type == Dc2TexBlockType.Texture));

        // page 896 + palette y511 → index 10 → red
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, tex.DecodeAt(896, 256, 0, 511)!.Image.Pixels[..4]);
        // page 768 + palette y511 → index 20 → blue (page selected by x)
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, tex.DecodeAt(768, 256, 0, 511)!.Image.Pixels[..4]);
        // page 896 + palette y510 → index 10 → green (palette selected independently)
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, tex.DecodeAt(896, 256, 0, 510)!.Image.Pixels[..4]);
    }

    [Fact]
    public void Synthetic_MultiPage_DecodeAtRejectsUnmatchedTextureCoordinates()
    {
        var tex = Dc2TexFile.Read(BuildMultiTex())!;

        Assert.Null(tex.DecodeAt(640, 256, 0, 511));
    }

    [Fact]
    public void Synthetic_MultiPage_DecodeAtRejectsUnmatchedPaletteCoordinates()
    {
        var tex = Dc2TexFile.Read(BuildMultiTex())!;

        Assert.Null(tex.DecodeAt(896, 256, 0, 509));
    }

    [Fact]
    public void Synthetic_SinglePage_DecodeAtToleratesUnmatchedCoordinates()
    {
        var pal = new ushort[256];
        pal[1] = 0x001F;
        var tex = Dc2TexFile.Read(BuildSyntheticTex(2, 2, pal, new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 }))!;

        var decoded = tex.DecodeAt(999, 999, 999, 999);

        Assert.NotNull(decoded);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, decoded!.Image.Pixels[..4]);
    }

    [Fact]
    public void Synthetic_RejectsTrailingPadOfAFullFramebuffer()
    {
        var blob = BuildMultiTex();
        Array.Resize(ref blob, blob.Length + 0x1000); // a full framebuffer of slack → not a closed layout
        Assert.Null(Dc2TexFile.Read(blob));
    }

    [Dc2Theory("E00.TEX", "E90.TEX")]
    [InlineData("E00.TEX")]  // Velociraptor skin
    [InlineData("E90.TEX")]  // Oviraptor skin
    public void Golden_DecodesRealTex(string file)
    {
        string path = FixtureGate.Dc2File(file);

        var tex = Dc2TexFile.Read(File.ReadAllBytes(path));
        Assert.NotNull(tex);
        // §3: one 256×256 8bpp framebuffer (rect w16=128) + one 256-entry CLUT, packed to 0x800/0x1000.
        var t = tex!.Blocks.Single(b => b.Type == Dc2TexBlockType.Texture);
        var p = tex.Blocks.Single(b => b.Type == Dc2TexBlockType.Palette);
        Assert.Equal((128, 256), (t.W16, t.H));
        Assert.Equal((0x1000, 0x10000), (t.DataOffset, t.DataSize));
        Assert.Equal((0x800, 0x200), (p.DataOffset, p.DataSize));

        var dt = tex.DecodeSinglePage();
        Assert.NotNull(dt);
        Assert.Equal(256, dt!.Image.Width);
        Assert.Equal(256, dt.Image.Height);
        Assert.True(dt.Image.HasOpaquePixels(), "a real skin must decode to opaque pixels");
        // a real palette yields many distinct colors (not the near-uniform of a wrong CLUT offset)
        int distinct = Enumerable.Range(0, 256 * 256)
            .Select(i => BitConverter.ToInt32(dt.Image.Pixels, i * 4)).Distinct().Count();
        Assert.True(distinct >= 32, $"expected a rich skin palette, got {distinct} colors");
    }
}
