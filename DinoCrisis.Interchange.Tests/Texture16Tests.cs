using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Texture16Tests
{
    private static readonly ushort[] Colours =
        { 0x0000, 0x001F, 0x03E0, 0x7C00, 0x1234, 0x294A, 0x5678, 0x7FFF };

    [Fact]
    public void Synthetic_tim16_encodes_to_native_tex_and_reimports_losslessly()
    {
        var tim = new TimFile(4, 2, 16);
        for (int i = 0; i < Colours.Length; i++)
            tim.SetRawPixel(i % 4, i / 4, Colours[i]);

        var skin = DcTextureEncoder.FromTim(tim);
        byte[] bytes = DcTextureEncoder.WriteDc2Tex(skin);
        var decoded = Dc2TexFile.Read(bytes)!.DecodeSinglePage();

        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.TexelsPerWord);
        Assert.Equal(4, decoded.Image.Width);
        Assert.Equal(2, decoded.Image.Height);
        AssertPixels(decoded.Image, Colours);
    }

    [Fact]
    public void Synthetic_dc1_package_decodes_direct_16bpp_framebuffer_without_a_palette()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        int triOff = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8, 4)) - SyntheticBlob.Base));
        int quadOff = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12, 4)) - SyntheticBlob.Base));
        const ushort direct16Tpage = 2 << 7;
        for (int i = 0; i < 2; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(
                blob.AsSpan(triOff + i * DcmVariant.Dc1.TriangleStride + 0x0e, 2), direct16Tpage);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(quadOff + 0x0e, 2), direct16Tpage);

        byte[] framebuffer = new byte[Colours.Length * 2];
        for (int i = 0; i < Colours.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(framebuffer.AsSpan(i * 2, 2), Colours[i]);
        byte[] package = new GianPackageBuilder()
            .AddData(blob)
            .AddTexture(framebuffer, x16: 0, y: 0, w16: 4, h: 2)
            .Build();
        var parsedPackage = GianPackage.TryParse(package)!;
        var model = Assert.Single(DcmLocator.FromPackage(package));

        var decoded = DcmTextureDecoder.TryDecode(parsedPackage, package, model);

        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.TexelsPerWord);
        AssertPixels(decoded.Image, Colours);
    }

    [Fact]
    public void Synthetic_dc1_package_accepts_valid_zero_tpage_and_clut_codes()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        int triOff = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8, 4)) - SyntheticBlob.Base));
        int quadOff = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12, 4)) - SyntheticBlob.Base));
        for (int i = 0; i < 2; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                blob.AsSpan(triOff + i * DcmVariant.Dc1.TriangleStride + 0x0a, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(
                blob.AsSpan(triOff + i * DcmVariant.Dc1.TriangleStride + 0x0e, 2), 0);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(quadOff + 0x0a, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(quadOff + 0x0e, 2), 0);
        byte[] framebuffer = { 0x10, 0x32 };
        var palette = new ushort[16];
        palette[0] = 0x001F;
        palette[1] = 0x03E0;
        palette[2] = 0x7C00;
        palette[3] = 0x7FFF;
        byte[] paletteBytes = new byte[palette.Length * 2];
        for (int i = 0; i < palette.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(paletteBytes.AsSpan(i * 2, 2), palette[i]);

        byte[] package = new GianPackageBuilder()
            .AddData(blob)
            .AddTexture(framebuffer, x16: 0, y: 0, w16: 1, h: 1)
            .AddPalette(paletteBytes, x16: 0, y: 0, w16: 16, h: 1)
            .Build();
        var parsedPackage = GianPackage.TryParse(package)!;
        var model = Assert.Single(DcmLocator.FromPackage(package));

        var decoded = DcmTextureDecoder.TryDecode(parsedPackage, package, model);

        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.TexelsPerWord);
        Assert.Equal(4, decoded.Image.Width);
        AssertPixels(decoded.Image, palette[..4]);
    }

    private static void AssertPixels(PsxImage image, ushort[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            var (r, g, b, a) = PsxImage.FromPsv(expected[i]);
            int offset = i * 4;
            Assert.Equal(r, image.Pixels[offset]);
            Assert.Equal(g, image.Pixels[offset + 1]);
            Assert.Equal(b, image.Pixels[offset + 2]);
            Assert.Equal(a, image.Pixels[offset + 3]);
        }
    }
}
