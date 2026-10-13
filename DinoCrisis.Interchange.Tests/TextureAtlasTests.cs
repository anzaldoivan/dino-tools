using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 5: the texel-fold repack + UV resolve. Pure, synthetic (no DcmTexture needed).</summary>
public class TextureAtlasTests
{
    private static PsxImage DeterministicImage(int w, int h)
    {
        var img = new PsxImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.Set(x, y, (byte)x, (byte)y, (byte)(x ^ y), 255);
        return img;
    }

    private static void AssertSamePixel(PsxImage a, int ax, int ay, PsxImage b, int bx, int by)
    {
        int oa = (ay * a.Width + ax) * 4, ob = (by * b.Width + bx) * 4;
        Assert.Equal(
            (a.Pixels[oa], a.Pixels[oa + 1], a.Pixels[oa + 2], a.Pixels[oa + 3]),
            (b.Pixels[ob], b.Pixels[ob + 1], b.Pixels[ob + 2], b.Pixels[ob + 3]));
    }

    [Theory]
    [InlineData(256, 256, 2, true)]   // DC2 enemy skin: 2 pages, fits
    [InlineData(128, 512, 2, true)]   // raptor: 1 col × 2 bands = 2 pages
    [InlineData(256, 464, 2, true)]   // Regina: 2 cols × 2 bands = 4 pages
    [InlineData(2048, 256, 2, true)]  // 16 pages, the max
    [InlineData(2048, 512, 2, false)] // 32 pages, too many
    [InlineData(256, 256, 4, false)]  // 4bpp unsupported
    [InlineData(200, 256, 2, false)]  // width not a 128 multiple
    public void CanFit_matches_re_addressing(int w, int h, int texelsPerWord, bool expected)
        => Assert.Equal(expected, TextureAtlas.CanFit(w, h, texelsPerWord));

    [Fact]
    public void Repack_folds_vertical_bands_horizontally()
    {
        var src = DeterministicImage(256, 512); // 2 bands
        var dst = TextureAtlas.Repack(src);

        Assert.Equal(512, dst.Width);   // srcWidth * 2 bands
        Assert.Equal(256, dst.Height);  // single page tall

        // Band 0 (rows 0..255) stays at x 0..255; band 1 (rows 256..511) moves to x 256..511, row-256.
        AssertSamePixel(dst, 100, 50, src, 100, 50);
        AssertSamePixel(dst, 256 + 100, 50, src, 100, 256 + 50);
        AssertSamePixel(dst, 255, 255, src, 255, 255);
        AssertSamePixel(dst, 256 + 0, 0, src, 0, 256);
    }

    [Fact]
    public void ResolveCorner_addresses_the_folded_atlas_consistently()
    {
        var src = DeterministicImage(256, 512);
        var dst = TextureAtlas.Repack(src);

        // tpage X=1 (col base 128), Y=0 (band 0); texel (10,20) ⇒ source (138,20).
        var (p0, u0, v0) = TextureAtlas.ResolveCorner(0, 0, 2, 256, tpage: 0x01, u: 10, v: 20);
        Assert.Equal(1, p0); Assert.Equal((byte)10, u0); Assert.Equal((byte)20, v0);
        AssertSamePixel(dst, p0 * 128 + u0, v0, src, 138, 20);

        // tpage X=0, Y=1 (row base 256); texel (5,30) ⇒ source (5,286) ⇒ band 1.
        var (p1, u1, v1) = TextureAtlas.ResolveCorner(0, 0, 2, 256, tpage: 0x10, u: 5, v: 30);
        Assert.Equal(2, p1); Assert.Equal((byte)5, u1); Assert.Equal((byte)30, v1);
        AssertSamePixel(dst, p1 * 128 + u1, v1, src, 5, 286);
    }
}
