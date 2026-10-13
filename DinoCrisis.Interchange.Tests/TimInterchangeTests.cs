using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 4 (G4): DC skin → RE TIM. Synthetic pixels are built via PsxImage.FromPsv so they
/// look like real decoder output (already 5-bit-expanded), which lets the re-encode round-trip exactly.</summary>
public class TimInterchangeTests
{
    [Fact]
    public void ToTim_builds_lossless_8bpp_clut_with_transparent_at_index0()
    {
        // 5 colours via FromPsv + one opaque black (only producible via the STP bit 0x8000).
        ushort[] cs = { 0x0000, 0x001F, 0x03E0, 0x7C00, 0x294A };
        var img = new PsxImage(6, 1);
        for (int i = 0; i < cs.Length; i++)
        {
            var (r, g, b, a) = PsxImage.FromPsv(cs[i]);
            img.Set(i, 0, r, g, b, a);
        }
        var (kr, kg, kb, ka) = PsxImage.FromPsv(0x8000); // opaque black (0,0,0,255)
        img.Set(5, 0, kr, kg, kb, ka);

        var tim = TimInterchange.ToTim(img);
        Assert.Equal(6, tim.Width);
        Assert.Equal(1, tim.Height);

        var pal = tim.GetPalette(0);
        Assert.Equal(0, (int)tim.GetRawPixel(0, 0));                 // transparent → reserved index 0
        Assert.Equal((ushort)0x0000, pal[tim.GetRawPixel(0, 0)]);
        Assert.Equal((ushort)0x001F, pal[tim.GetRawPixel(1, 0)]);
        Assert.Equal((ushort)0x03E0, pal[tim.GetRawPixel(2, 0)]);
        Assert.Equal((ushort)0x7C00, pal[tim.GetRawPixel(3, 0)]);
        Assert.Equal((ushort)0x294A, pal[tim.GetRawPixel(4, 0)]);
        Assert.Equal((ushort)0x8000, pal[tim.GetRawPixel(5, 0)]);   // opaque black kept distinct via STP
    }

    [Fact]
    public void ToTim_dedups_repeated_colours()
    {
        var (r, g, b, a) = PsxImage.FromPsv(0x1234);
        var img = new PsxImage(4, 1);
        for (int x = 0; x < 4; x++) img.Set(x, 0, r, g, b, a);

        var tim = TimInterchange.ToTim(img);
        // All four pixels share one palette index.
        var idx = tim.GetRawPixel(0, 0);
        Assert.Equal(idx, tim.GetRawPixel(1, 0));
        Assert.Equal(idx, tim.GetRawPixel(2, 0));
        Assert.Equal(idx, tim.GetRawPixel(3, 0));
    }

    [Fact]
    public void ToTim_throws_above_256_colours()
    {
        var img = new PsxImage(512, 1);
        for (int x = 0; x < 512; x++)
        {
            var (r, g, b, a) = PsxImage.FromPsv((ushort)(x + 1)); // 512 distinct non-zero colours
            img.Set(x, 0, r, g, b, a);
        }
        Assert.Throws<System.InvalidOperationException>(() => TimInterchange.ToTim(img));
    }

    [Theory]
    [InlineData(256, 256, true)]   // DC2 enemy skin fits RE addressing
    [InlineData(2048, 256, true)]  // 16 pages wide, one page tall
    [InlineData(128, 512, false)]  // DC1 raptor — too tall for byte v / a 256 page
    [InlineData(256, 464, false)]  // DC1 Regina — too tall
    public void FitsReAtlas_matches_re_page_bounds(int w, int h, bool expected)
        => Assert.Equal(expected, TimInterchange.FitsReAtlas(w, h));

    // ---- gated golden: a real DC1 skin → TIM, losslessly ----

    [Dc1Fact("core00.dat")]
    public void Real_dc1_skin_converts_to_a_lossless_readable_tim()
    {
        var bytes = File.ReadAllBytes(FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat")));
        var model = DcmLocator.FromDc1Package(bytes).FirstOrDefault(m => m.Header.PartCount == 15);
        Assert.NotNull(model);
        var tex = DcmTextureDecoder.TryDecode(GianPackage.TryParse(bytes)!, bytes, model!);
        Assert.NotNull(tex);

        var tim = TimInterchange.ToTim(tex!.Image);
        Assert.Equal(tex.Image.Width, tim.Width);
        Assert.Equal(tex.Image.Height, tim.Height);

        // Every pixel re-decodes (through the new CLUT) to the exact source RGBA — lossless.
        var pal = tim.GetPalette(0);
        for (int y = 0; y < tex.Image.Height; y++)
            for (int x = 0; x < tex.Image.Width; x++)
            {
                int o = (y * tex.Image.Width + x) * 4;
                var (r, g, b, a) = PsxImage.FromPsv(pal[tim.GetRawPixel(x, y)]);
                Assert.Equal(
                    (tex.Image.Pixels[o], tex.Image.Pixels[o + 1], tex.Image.Pixels[o + 2], tex.Image.Pixels[o + 3]),
                    ((byte)r, (byte)g, (byte)b, (byte)a));
            }

        // The serialized TIM re-reads with the same dimensions (valid PSX TIM).
        var reread = new TimFile(tim.GetBytes());
        Assert.Equal(tim.Width, reread.Width);
        Assert.Equal(tim.Height, reread.Height);
    }
}
