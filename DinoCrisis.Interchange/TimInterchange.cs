using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;

namespace DinoCrisis.Interchange;

/// <summary>
/// Converts a decoded Dino Crisis skin (an RGBA <see cref="PsxImage"/> from
/// <c>DcmTextureDecoder</c>/<c>Dc2TexFile</c>) into an RE <see cref="TimFile"/> — gap "G4". DC skins
/// are already paletted (8bpp/4bpp through a CLUT), so the decoded image has ≤256 distinct colours;
/// this re-collects those colours into a PSX 8bpp TIM <b>losslessly</b> (no quantisation): the
/// BGR555→RGBA8 expansion the decoder used (<c>(v5&lt;&lt;3)|(v5&gt;&gt;2)</c>) is invertible
/// (<c>r8&gt;&gt;3 == r5</c>), so each RGBA pixel re-encodes to its exact original 15-bit colour.
///
/// <para>Texture chunk vs sibling file: RE1 EMDs carry a Texture chunk (embed the TIM there); RE2/RE3
/// EMDs do not — the skin is a sibling <c>EM##.TIM</c> the caller writes next to the EMD. This class
/// produces the TIM bytes; placement is the caller's choice.</para>
///
/// <para><b>UV/atlas coherence is NOT done here</b> (see the plan): RE addresses texels as
/// (page 0–15 × 128 wide, byte u/v within a 256-tall page). Many DC1 skins exceed that (raptor
/// 128×512, Regina 256×464), so a coherent DC→RE textured model needs an atlas repack + UV rewrite
/// that is the next increment. <see cref="FitsReAtlas"/> reports whether a skin fits RE addressing as
/// is (true for the DC2 256×256 enemy case).</para>
/// </summary>
public static class TimInterchange
{
    /// <summary>Build an 8bpp PSX TIM from a decoded DC skin. Throws if the image has &gt;256 distinct
    /// colours (it should not, coming from an 8bpp/4bpp source).</summary>
    public static TimFile ToTim(PsxImage image)
    {
        if (image is null) throw new System.ArgumentNullException(nameof(image));

        var tim = new TimFile(image.Width, image.Height, 8);

        // Reserve index 0 for transparent (PSX/RE convention: alpha<128 ⇒ index 0).
        var palette = new System.Collections.Generic.List<ushort> { 0 };
        var lookup = new System.Collections.Generic.Dictionary<ushort, int> { [0] = 0 };

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int o = (y * image.Width + x) * 4;
                ushort c = Encode(image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]);
                if (!lookup.TryGetValue(c, out int idx))
                {
                    if (palette.Count >= 256)
                        throw new System.InvalidOperationException(
                            "Skin has more than 256 distinct colours — cannot fit an 8bpp TIM CLUT.");
                    idx = palette.Count;
                    palette.Add(c);
                    lookup[c] = idx;
                }
                tim.SetRawPixel(x, y, (byte)idx);
            }
        }

        while (palette.Count < 256) palette.Add(0);
        tim.SetPalette(0, palette.ToArray());
        return tim;
    }

    /// <summary>Convenience: build the TIM from a decoded <see cref="DcmTexture"/> (uses its image).</summary>
    public static TimFile ToTim(DcmTexture texture) => ToTim((texture ?? throw new System.ArgumentNullException(nameof(texture))).Image);

    /// <summary>The serialized TIM file bytes — write these as the sibling <c>EM##.TIM</c> (RE2/RE3) or
    /// embed in the RE1 Texture chunk.</summary>
    public static byte[] ToTimBytes(PsxImage image) => ToTim(image).GetBytes();

    /// <summary>Whether a skin of these dimensions fits RE's texel addressing as-is: up to 16 pages of
    /// 128 px wide (≤2048) and a single 256-tall page (byte v). Skins beyond this need the atlas
    /// repack + UV rewrite that is the next increment.</summary>
    public static bool FitsReAtlas(int width, int height) =>
        width > 0 && width <= 16 * 128 && height > 0 && height <= 256;

    /// <summary>Re-encode an RGBA8 pixel to PSX BGR555. Transparent (a==0) ⇒ 0 (the CLUT's transparent
    /// slot). Opaque pure black would also be 0, which PSX reads as transparent, so it gets the STP bit
    /// (0x8000 = opaque black) to stay distinct.</summary>
    private static ushort Encode(byte r, byte g, byte b, byte a)
    {
        if (a == 0) return 0;
        int v = (r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10);
        return (ushort)(v == 0 ? 0x8000 : v);
    }
}
