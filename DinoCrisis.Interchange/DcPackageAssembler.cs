using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;

namespace DinoCrisis.Interchange;

/// <summary>
/// Assembles a <b>game-ready DC1 <c>.dat</c></b> that bundles a reverse-converted model with its
/// texture in the Gian-package form DC1 expects: the DCM model as an uncompressed <c>Data</c> entry
/// (found by <c>DcmLocator</c>'s brute-scan), plus a <c>Texture</c> framebuffer entry and a
/// <c>Palette</c> entry whose VRAM coords the model's per-polygon tpage/clut reference. The result
/// round-trips through this toolset's own readers (model + textured decode).
///
/// <para>The skin is placed at VRAM x=0 as an 8bpp framebuffer <c>Width</c> texels wide; each polygon's
/// RE page index (0..) becomes the DC tpage page column (x = page*128), so a multi-page RE atlas maps
/// straight across. The single CLUT sits at a fixed VRAM row. (Single 256-tall band; a skin taller than
/// 256 would need the vertical fold — out of scope here, same as Inc 5.)</para>
/// </summary>
public static class DcPackageAssembler
{
    private const int ClutVramY = 480; // a CLUT row clear of the framebuffer

    /// <summary>Build the DC1 <c>.dat</c> bytes from an interchange mesh (RE page/u/v UVs) + a DC 8bpp
    /// skin. <paramref name="scale"/> is applied to the geometry (RE→DC units).</summary>
    public static byte[] BuildDc1(InterchangeMesh mesh, DcSkin skin, uint loadBase = 0x80100000, double scale = 1.0)
    {
        if (mesh is null) throw new System.ArgumentNullException(nameof(mesh));
        if (skin.PaletteRows.Count != 1)
            throw new System.ArgumentException(
                "DC1 package assembly stores one palette row; mixed-CLUT skins require page-aware atlas conversion",
                nameof(skin));
        if (skin.Indices is null || skin.Clut is null) throw new System.ArgumentException("skin has no data", nameof(skin));
        if (skin.Width % 2 != 0) throw new System.ArgumentException("8bpp texture width must be even (VRAM words)", nameof(skin));
        if (skin.Width > 16 * 128) throw new System.ArgumentException("skin wider than 16 pages cannot be addressed by tpage", nameof(skin));
        if (skin.Height > 256) throw new System.ArgumentException("skin taller than 256 needs the vertical fold (out of scope)", nameof(skin));

        // CLUT code: clutX16 = (clut & 0x3F) << 4 ; clutY = clut >> 6. Texture at VRAM (0,0), CLUT at (0,ClutVramY).
        ushort clutCode = (ushort)((ClutVramY << 6) | 0);
        const ushort tpage8bpp = 0x80; // depth bits (tpage>>7)&3 == 1, page column from the poly's page nibble

        var modelBlob = ReverseInterchange.ToDcm(mesh, DcmVariant.Dc1, loadBase, scale, tpage8bpp, clutCode);

        var clutBytes = new byte[skin.Clut.Length * 2];
        for (int i = 0; i < skin.Clut.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(clutBytes.AsSpan(i * 2, 2), skin.Clut[i]);

        return new GianPackageBuilder()
            .AddData(modelBlob)
            .AddTexture(skin.Indices, x16: 0, y: 0, w16: (ushort)(skin.Width / 2), h: (ushort)skin.Height)
            .AddPalette(clutBytes, x16: 0, y: ClutVramY, w16: (ushort)skin.Clut.Length, h: 1)
            .Build();
    }
}
