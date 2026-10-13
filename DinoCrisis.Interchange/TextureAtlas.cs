using DinoCrisis.Model.Textures;

namespace DinoCrisis.Interchange;

/// <summary>
/// Repacks a Dino Crisis skin into RE's texel-addressing scheme and resolves model UVs to match — the
/// "G5-texture" half (Inc 5). RE addresses texels as page(0–15) × 128-wide columns, byte u/v within a
/// single 256-tall page; many DC skins are taller than 256 (raptor 512, Regina 464). A DC image is laid
/// out as a grid of 128×256 VRAM page cells, and every DC polygon's corners share one tpage → one cell,
/// so the fix is to <b>fold the vertical 256-tall bands into extra horizontal pages</b>: band <i>b</i>
/// (source rows <c>b*256 .. b*256+255</c>) moves to x-offset <c>b*srcWidth</c>, giving a ≤256-tall atlas
/// the byte v can address. UVs resolve to the same fold, so texture and UVs stay in agreement.
///
/// <para>8bpp only (a VRAM page is 128 texels wide at 8bpp), and the source width must be a whole number
/// of 128-px columns. Skins needing &gt;16 RE pages, 4bpp skins, and non-128-aligned widths are rejected
/// (<see cref="CanFit"/>) — the caller keeps the model untextured.</para>
/// </summary>
public static class TextureAtlas
{
    public const int PageWidth = 128;
    public const int PageHeight = 256;

    public static int NumBands(int height) => (height + PageHeight - 1) / PageHeight;
    public static int RepackedWidth(int srcWidth, int height) => srcWidth * NumBands(height);
    public static int NumPages(int srcWidth, int height) => RepackedWidth(srcWidth, height) / PageWidth;

    /// <summary>Whether a skin of these dimensions/depth folds into RE addressing: 8bpp, width a whole
    /// number of 128-px columns, and ≤16 RE pages after folding.</summary>
    public static bool CanFit(int srcWidth, int srcHeight, int texelsPerWord)
        => texelsPerWord == 2
        && srcWidth > 0 && srcHeight > 0
        && srcWidth % PageWidth == 0
        && NumPages(srcWidth, srcHeight) <= 16;

    /// <summary>Fold the source skin's 256-tall bands into a single-page-tall atlas laid out
    /// horizontally (band <i>b</i> at x = <c>b*srcWidth</c>). Height is always 256; width is
    /// <c>srcWidth * NumBands</c>.</summary>
    public static PsxImage Repack(PsxImage src)
    {
        int bands = NumBands(src.Height);
        var dst = new PsxImage(src.Width * bands, PageHeight);
        for (int row = 0; row < src.Height; row++)
        {
            int destXBase = (row / PageHeight) * src.Width;
            int destY = row % PageHeight;
            for (int col = 0; col < src.Width; col++)
            {
                int s = (row * src.Width + col) * 4;
                dst.Set(destXBase + col, destY, src.Pixels[s], src.Pixels[s + 1], src.Pixels[s + 2], src.Pixels[s + 3]);
            }
        }
        return dst;
    }

    /// <summary>Resolve a polygon corner (its <paramref name="tpage"/> + texel <paramref name="u"/>/
    /// <paramref name="v"/>) to the RE (page, u, v) that addresses the folded atlas from
    /// <see cref="Repack"/>. <paramref name="entryX16"/>/<paramref name="entryY"/>/
    /// <paramref name="texelsPerWord"/> are the decoded texture's VRAM origin (from <c>DcmTexture</c>).</summary>
    public static (int Page, byte U, byte V) ResolveCorner(
        int entryX16, int entryY, int texelsPerWord, int srcWidth, ushort tpage, byte u, byte v)
    {
        int pageX16 = (tpage & 0xF) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        int col = (pageX16 - entryX16) * texelsPerWord + u; // absolute texel column in the source skin
        int row = (pageY - entryY) + v;                     // absolute texel row in the source skin
        int destX = (row / PageHeight) * srcWidth + col;    // column in the folded atlas
        return (destX / PageWidth, (byte)(destX % PageWidth), (byte)(row % PageHeight));
    }
}
