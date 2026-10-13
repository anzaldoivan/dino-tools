using System.Buffers.Binary;
using IntelOrca.Biohazard;

namespace DinoCrisis.Interchange;

/// <summary>An indexed TIM page and the CLUT row that owns its indices.</summary>
public readonly record struct DcTexturePage(ushort TPage, int X, int Y, int PaletteRow);

/// <summary>
/// A Dino Crisis skin. Indexed skins retain the byte-per-texel index plane, every 256-entry PSX
/// BGR555 palette row, and the explicit page-to-row ownership. Direct 16bpp skins have no palette
/// rows and use <see cref="DirectPixels"/> instead.
/// </summary>
public sealed class DcSkin
{
    /// <summary>Compatibility constructor for a single-row indexed or direct 16bpp skin.</summary>
    public DcSkin(int width, int height, byte[] indices, ushort[] clut, ushort[]? directPixels = null)
        : this(width, height, indices,
            clut is { Length: > 0 } ? new[] { clut } : Array.Empty<ushort[]>(),
            Array.Empty<DcTexturePage>(), directPixels)
    {
    }

    /// <summary>Construct an indexed skin with explicit palette rows and page ownership.</summary>
    public DcSkin(int width, int height, byte[] indices,
                  IReadOnlyList<ushort[]> paletteRows,
                  IReadOnlyList<DcTexturePage> pages,
                  ushort[]? directPixels = null)
    {
        Width = width;
        Height = height;
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        PaletteRows = paletteRows ?? throw new ArgumentNullException(nameof(paletteRows));
        Pages = pages ?? throw new ArgumentNullException(nameof(pages));
        DirectPixels = directPixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Indices { get; }
    public IReadOnlyList<ushort[]> PaletteRows { get; }
    public IReadOnlyList<DcTexturePage> Pages { get; }
    public ushort[]? DirectPixels { get; }

    /// <summary>
    /// The only CLUT row when this skin is single-row. Accessing it for a mixed-CLUT skin is an
    /// explicit refusal; callers must use <see cref="PaletteRows"/> and page metadata.
    /// </summary>
    public ushort[] Clut
    {
        get
        {
            if (PaletteRows.Count > 1)
                throw new InvalidOperationException(
                    "indexed skin has multiple palette rows; use PaletteRows and page ownership");
            return PaletteRows.Count == 0 ? Array.Empty<ushort>() : PaletteRows[0];
        }
    }
}

/// <summary>
/// The <b>TIM → DC texture encoder</b> (the reverse of <see cref="TimInterchange"/>): turns an RE
/// <see cref="TimFile"/> into DC indexed or direct 16bpp texture data, and serializes it as a DC2
/// <c>.TEX</c> file. Both paths preserve the PSX pixel representation without re-quantisation.
///
/// <para><b>Scope:</b> writes a single-page DC2 <c>.TEX</c> (the format decoded in
/// <c>DCM-DC2-TEXTURE-PLAN.md</c>). DC1 textures live inside the model's Gian package, whose writer does
/// not exist yet, so producing an <i>in-game</i> textured DC1 <c>.dat</c> still needs a Gian-package
/// writer (a separate future step). This class delivers the texture-format bridge + a real DC2 <c>.TEX</c>.</para>
/// </summary>
public static class DcTextureEncoder
{
    /// <summary>
    /// Extract indexed 8bpp or direct 16bpp skin data from an RE TIM losslessly. An 8bpp TIM keeps
    /// every CLUT row; for page-aligned images the RE3 convention maps each 128-pixel page column
    /// to the same-numbered CLUT row (or row zero for a single-row TIM).
    /// </summary>
    public static DcSkin FromTim(TimFile tim)
    {
        if (tim is null) throw new System.ArgumentNullException(nameof(tim));
        int w = tim.Width, h = tim.Height;
        if (tim.PixelFormat == 2)
        {
            var direct = new ushort[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    direct[y * w + x] = tim.GetRawPixel(x, y);
            return new DcSkin(w, h, Array.Empty<byte>(), Array.Empty<ushort[]>(),
                Array.Empty<DcTexturePage>(), direct);
        }

        if (tim.PixelFormat != 9)
            throw new NotSupportedException(
                $"TIM pixel format {tim.PixelFormat} is not supported by the indexed 8bpp encoder");
        if (tim.PaletteCount <= 0)
            throw new InvalidDataException("8bpp TIM has no CLUT rows");

        var rows = Enumerable.Range(0, tim.PaletteCount)
            .Select(tim.GetPalette)
            .ToArray();
        if (rows.Any(row => row.Length != 256))
            throw new InvalidDataException("8bpp TIM CLUT rows must contain exactly 256 entries");

        var indices = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                indices[y * w + x] = (byte)tim.GetRawPixel(x, y);

        var pages = new List<DcTexturePage>();
        if (w % TextureAtlas.PageWidth == 0 && h >= TextureAtlas.PageHeight &&
            tim.ImageOriginX % (TextureAtlas.PageWidth / 2) == 0 &&
            tim.ImageOriginY % TextureAtlas.PageHeight == 0)
        {
            int pageCount = w / TextureAtlas.PageWidth;
            if (pageCount > 16)
                throw new InvalidDataException("8bpp TIM has more page columns than TPAGE can address");
            for (int page = 0; page < pageCount; page++)
            {
                int paletteRow = rows.Length == 1 ? 0 : page;
                if (paletteRow >= rows.Length)
                    throw new InvalidDataException(
                        $"8bpp TIM page column {page} has no matching CLUT row");
                int x = page * TextureAtlas.PageWidth;
                int y = 0;
                int pageX16 = tim.ImageOriginX + x / 2;
                int pageY = tim.ImageOriginY + y;
                if (pageX16 % 64 != 0 || pageY % 256 != 0 || pageX16 / 64 > 0xF || pageY / 256 > 1)
                    throw new InvalidDataException("8bpp TIM page origin cannot be represented by TPAGE");
                ushort tpage = (ushort)((pageX16 / 64) | ((pageY / 256) << 4) | (1 << 7));
                pages.Add(new DcTexturePage(tpage, x, y, paletteRow));
            }
        }

        return new DcSkin(w, h, indices, rows, pages);
    }

    /// <summary>Serialize a skin as a single-page DC2 <c>.TEX</c>: one TEXTURE plus a PALETTE for indexed
    /// data, or a texture-only direct 16bpp framebuffer. The layout uses 32-byte descriptors, an indexed
    /// palette at <c>0x800</c>, and framebuffer at <c>0x1000</c>. The VRAM coords default to the E00
    /// convention; a model that samples this skin sets its header tpage/clut to match.</summary>
    public static byte[] WriteDc2Tex(DcSkin skin, int texX16 = 896, int texY = 0, int clutX16 = 0, int clutY = 511)
    {
        RequirePositiveUShort(skin.Width, nameof(skin.Width));
        RequirePositiveUShort(skin.Height, nameof(skin.Height));
        RequireUShort(texX16, nameof(texX16));
        RequireUShort(texY, nameof(texY));
        RequireUShort(clutX16, nameof(clutX16));
        RequireUShort(clutY, nameof(clutY));
        int pixelCount = CheckedSize(() => checked(skin.Width * skin.Height));

        if (skin.DirectPixels is not null)
        {
            if (skin.DirectPixels.Length != pixelCount)
                throw new System.ArgumentException("direct pixel count must equal width * height", nameof(skin));

            const int directTexOff = 0x1000;
            int directFbSize = CheckedSize(() => checked(pixelCount * 2));
            var directBytes = new byte[CheckedSize(() => checked(directTexOff + directFbSize))];
            WriteDesc(directBytes, 0x00, tag: 1, size: directFbSize,
                x: texX16, y: texY, w: skin.Width, h: skin.Height);
            for (int i = 0; i < skin.DirectPixels.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    directBytes.AsSpan(directTexOff + i * 2, 2), skin.DirectPixels[i]);
            return directBytes;
        }

        if (skin.PaletteRows.Count != 1)
            throw new System.ArgumentException(
                "DC2 .TEX stores one palette row; mixed-CLUT skins require an explicit page-aware target", nameof(skin));
        if (skin.Indices is null) throw new System.ArgumentException("skin has no data", nameof(skin));
        var clut = skin.Clut;
        if (skin.Width % 2 != 0) throw new System.ArgumentException("8bpp .TEX width must be even (W is in 16-bit words)", nameof(skin));
        if (skin.Indices.Length != pixelCount)
            throw new System.ArgumentException("index count must equal width * height", nameof(skin));
        if (clut.Length != 256) throw new System.ArgumentException("CLUT must have exactly 256 entries", nameof(skin));

        const int palOff = 0x800, texOff = 0x1000;
        int fbSize = pixelCount;
        var b = new byte[CheckedSize(() => checked(texOff + fbSize))];

        // Descriptor 0: TEXTURE (w is in 16-bit words = width/2). Descriptor 1: PALETTE (256×u16).
        WriteDesc(b, 0x00, tag: 1, size: fbSize, x: texX16, y: texY, w: skin.Width / 2, h: skin.Height);
        WriteDesc(b, 0x20, tag: 2, size: 256 * 2, x: clutX16, y: clutY, w: 256, h: 1);
        // 0x40..0x800 stays zero → tag 0 terminates the descriptor table for the reader.

        for (int i = 0; i < 256; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(palOff + i * 2, 2), clut[i]);
        System.Array.Copy(skin.Indices, 0, b, texOff, fbSize);
        return b;
    }

    private static void WriteDesc(byte[] b, int o, uint tag, int size, int x, int y, int w, int h)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 0, 4), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4, 4), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 8, 2), (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 0xa, 2), (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 0xc, 2), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 0xe, 2), (ushort)h);
    }

    private static void RequirePositiveUShort(int value, string name)
    {
        if (value is <= 0 or > ushort.MaxValue)
            throw new System.ArgumentOutOfRangeException(name, value, "must be between 1 and 65535");
    }

    private static void RequireUShort(int value, string name)
    {
        if (value is < 0 or > ushort.MaxValue)
            throw new System.ArgumentOutOfRangeException(name, value, "must be between 0 and 65535");
    }

    private static int CheckedSize(System.Func<int> calculate)
    {
        try
        {
            return calculate();
        }
        catch (System.OverflowException)
        {
            throw new System.ArgumentOutOfRangeException(
                "skin", "texture dimensions exceed supported byte-array sizes");
        }
    }
}
