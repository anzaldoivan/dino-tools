using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>TIM → DC texture encoder: a full DC↔RE texture round-trip proves the pixel encoding is
/// preserved and the produced <c>.TEX</c> parses back through the reader.</summary>
public class DcTextureEncoderTests
{
    [Fact]
    public void Tim_to_dc_tex_round_trips_losslessly()
    {
        // 8 distinct BGR555 colours, built via FromPsv so re-encoding is exact.
        ushort[] cs = { 0x0000, 0x001F, 0x03E0, 0x7C00, 0x294A, 0x1234, 0x5678, 0x7FFF };
        var img = new PsxImage(8, 2);
        for (int x = 0; x < 8; x++)
        {
            var (r, g, b, a) = PsxImage.FromPsv(cs[x]);
            img.Set(x, 0, r, g, b, a);
            img.Set(x, 1, r, g, b, a);
        }

        // RE side: PsxImage → TIM. DC side: TIM → Dc skin → .TEX → read back → decode.
        var tim = TimInterchange.ToTim(img);
        var skin = DcTextureEncoder.FromTim(tim);
        Assert.Equal(8, skin.Width);
        Assert.Equal(2, skin.Height);

        var texBytes = DcTextureEncoder.WriteDc2Tex(skin);
        var read = Dc2TexFile.Read(texBytes);
        Assert.NotNull(read); // our writer produces a layout the reader accepts

        var decoded = read!.DecodeSinglePage();
        Assert.NotNull(decoded);
        Assert.Equal(8, decoded!.Image.Width);
        Assert.Equal(2, decoded.Image.Height);

        // Every pixel survives PsxImage → TIM → .TEX → decode unchanged.
        for (int i = 0; i < img.Pixels.Length; i++)
            Assert.Equal(img.Pixels[i], decoded.Image.Pixels[i]);
    }

    [Fact]
    public void WriteDc2Tex_rejects_odd_width()
    {
        var skin = new DcSkin(3, 1, new byte[3], new ushort[256]);
        Assert.Throws<System.ArgumentException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(65536, 1)]
    [InlineData(2, 65536)]
    public void WriteDc2Tex_rejects_nonpositive_or_non_ushort_dimensions(int width, int height)
    {
        var skin = new DcSkin(width, height, Array.Empty<byte>(), new ushort[256]);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(65536, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 65536, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 65536, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(0, 0, 0, 65536)]
    public void WriteDc2Tex_rejects_coordinates_outside_ushort_range(
        int texX16, int texY, int clutX16, int clutY)
    {
        var skin = new DcSkin(2, 1, new byte[2], new ushort[256]);

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => DcTextureEncoder.WriteDc2Tex(skin, texX16, texY, clutX16, clutY));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WriteDc2Tex_requires_exact_index_count(int count)
    {
        var skin = new DcSkin(2, 1, new byte[count], new ushort[256]);

        Assert.Throws<System.ArgumentException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(257)]
    public void WriteDc2Tex_requires_exact_clut_count(int count)
    {
        var skin = new DcSkin(2, 1, new byte[2], new ushort[count]);

        Assert.Throws<System.ArgumentException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WriteDc2Tex_requires_exact_direct_pixel_count(int count)
    {
        var skin = new DcSkin(2, 1, Array.Empty<byte>(), Array.Empty<ushort>(), new ushort[count]);

        Assert.Throws<System.ArgumentException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Fact]
    public void WriteDc2Tex_rejects_indexed_dimensions_whose_checked_pixel_count_exceeds_int32()
    {
        var skin = new DcSkin(65534, 65535, Array.Empty<byte>(), new ushort[256]);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }

    [Fact]
    public void WriteDc2Tex_rejects_direct_dimensions_whose_checked_byte_count_exceeds_int32()
    {
        var skin = new DcSkin(65535, 65535, Array.Empty<byte>(), Array.Empty<ushort>(), Array.Empty<ushort>());

        Assert.Throws<System.ArgumentOutOfRangeException>(() => DcTextureEncoder.WriteDc2Tex(skin));
    }
}
