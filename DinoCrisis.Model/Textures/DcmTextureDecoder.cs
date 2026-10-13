using System.Buffers.Binary;
using System.Linq;
using DinoCrisis.Model.Containers;

namespace DinoCrisis.Model.Textures;

/// <summary>
/// The decoded texture for a model plus the UV→image mapping. PSX stores textures as indexed or
/// direct-colour VRAM framebuffers; this resolves the model's <c>tpage</c> (page column + color depth)
/// and, for indexed textures, <c>clut</c> (palette location) against the package's gfx entries and
/// decodes them to RGBA.
/// </summary>
public sealed class DcmTexture
{
    public PsxImage Image { get; }

    /// <summary>VRAM x (16-bit units) of the decoded texture entry — the origin UV columns are measured from.</summary>
    public int EntryX16 { get; }

    /// <summary>VRAM y of the decoded texture entry — the origin UV rows are measured from.</summary>
    public int EntryY { get; }

    /// <summary>Texels per 16-bit VRAM word for this texture's depth (1 for 16bpp, 2 for 8bpp,
    /// 4 for 4bpp).</summary>
    public int TexelsPerWord { get; }

    internal DcmTexture(PsxImage image, int entryX16, int entryY, int texelsPerWord)
    {
        Image = image;
        EntryX16 = entryX16;
        EntryY = entryY;
        TexelsPerWord = texelsPerWord;
    }

    /// <summary>Map a polygon corner (texel <paramref name="u"/>/<paramref name="v"/> under page
    /// <paramref name="tpage"/>) to normalized OBJ texture coords (s, t) into <see cref="Image"/>.</summary>
    public (double S, double T) MapUv(byte u, byte v, ushort tpage)
    {
        int pageX16 = (tpage & 0xF) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        int col = (pageX16 - EntryX16) * TexelsPerWord + u;
        int row = (pageY - EntryY) + v;
        double s = Image.Width == 0 ? 0 : (double)col / Image.Width;
        double t = Image.Height == 0 ? 0 : (double)row / Image.Height;
        return (s, 1.0 - t); // OBJ V is bottom-up
    }
}

/// <summary>Decodes a model's texture from its sibling gfx entries in the same Gian package.</summary>
public static class DcmTextureDecoder
{
    private const int TexturePageWidth = 128;

    /// <summary>
    /// Decode the texture a <paramref name="model"/> samples, from the <paramref name="pkg"/>'s
    /// Texture and, for indexed modes, Palette entries. Returns null when the model carries no texture
    /// codes or the package has no matching gfx entries (e.g. DC2, whose texture lives in a separate
    /// <c>.TEX</c>). Handles one texture column and, for 4/8bpp, one CLUT. An indexed DC1 model with
    /// mixed primitive CLUTs is refused; use <see cref="TryDecodePageAware"/> for that diagnostic path.
    /// </summary>
    public static DcmTexture? TryDecode(GianPackage pkg, byte[] fileBytes, DcmModel model)
    {
        // Representative tpage/clut: DC1 carries them per-polygon; DC2 in the header.
        ushort tpage, clut;
        if (model.Variant.Game == DcmGame.Dc1)
        {
            var poly = model.Triangles.Cast<object>().Concat(model.Quads).FirstOrDefault();
            if (poly is null) return null;
            (tpage, clut) = poly is DcmTriangle t ? (t.TPage, t.Clut) : (((DcmQuad)poly).TPage, ((DcmQuad)poly).Clut);
        }
        else { tpage = model.Header.TPage; clut = model.Header.Clut; }
        int depthMode = (tpage >> 7) & 3;      // 0=4bpp, 1=8bpp, 2=16bpp
        if (depthMode is not (0 or 1 or 2)) return null;
        if (model.Variant.Game == DcmGame.Dc1 && depthMode != 2)
        {
            var cluts = model.Triangles.Select(t => t.Clut)
                .Concat(model.Quads.Select(q => q.Clut))
                .Distinct()
                .Take(2)
                .Count();
            if (cluts > 1)
                return null;
        }
        int texelsPerWord = depthMode switch { 0 => 4, 1 => 2, _ => 1 };
        int paletteSize = depthMode == 0 ? 16 : 256;    // ignored for direct 16bpp
        int pageX16 = (tpage & 0xF) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        int clutX16 = (clut & 0x3F) << 4;
        int clutY = clut >> 6;

        // A texture framebuffer is a raw Texture entry or a compressed-texture Lzss1/Lzss2 entry
        // (GPC_LZSS1/2 = "compressed texture"); all carry the same gfx coords. Non-texture entries
        // have GfxW == 0, so InX excludes them.
        static bool IsTexture(GianEntryType t) => t is GianEntryType.Texture or GianEntryType.Lzss1 or GianEntryType.Lzss2;
        var texEntry = FindGfx(pkg, IsTexture, e => InX(e, pageX16) && InY(e, pageY));
        var palEntry = depthMode == 2
            ? null
            : FindGfx(pkg, t => t == GianEntryType.Palette, e => InX(e, clutX16) && InY(e, clutY));
        if (texEntry is null || (depthMode != 2 && palEntry is null)) return null;

        int width = texEntry.Value.GfxW * texelsPerWord;
        int height = texEntry.Value.GfxH;
        int rowBytes = texEntry.Value.GfxW * 2;         // a VRAM row is GfxW 16-bit words, regardless of depth
        byte[] texData = pkg.DecodeEntry(fileBytes, texEntry.Value);
        if (texEntry.Value.Type is GianEntryType.Lzss1 or GianEntryType.Lzss2)
        {
            texData = UnswizzleGfx(texData, rowBytes, height);
            if (texData.Length == 0) return null;
        }
        if (width <= 0 || height <= 0 || (long)rowBytes * height > texData.Length) return null;

        ushort[]? palette = null;
        if (depthMode != 2)
        {
            byte[] palData = pkg.DecodeEntry(fileBytes, palEntry!.Value);
            int palRowStrideBytes = palEntry.Value.GfxW * 2;
            int palBase = (clutY - palEntry.Value.GfxY) * palRowStrideBytes + (clutX16 - palEntry.Value.GfxX) * 2;
            if (palBase < 0 || palBase + paletteSize * 2 > palData.Length) return null;
            palette = new ushort[paletteSize];
            for (int i = 0; i < paletteSize; i++)
                palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(palData.AsSpan(palBase + i * 2, 2));
        }

        var img = new PsxImage(width, height);
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                ushort psv;
                if (depthMode == 2)
                {
                    psv = BinaryPrimitives.ReadUInt16LittleEndian(
                        texData.AsSpan(row * rowBytes + col * 2, 2));
                }
                else
                {
                    // 8bpp: one byte per texel. 4bpp: two texels per byte, low nibble = left.
                    int index = depthMode == 1
                        ? texData[row * rowBytes + col]
                        : (col & 1) == 0 ? texData[row * rowBytes + (col >> 1)] & 0x0F
                                         : texData[row * rowBytes + (col >> 1)] >> 4;
                    psv = palette![index];
                }
                var (r, g, b, a) = PsxImage.FromPsv(psv);
                img.Set(col, row, r, g, b, a);
            }

        return new DcmTexture(img, texEntry.Value.GfxX, texEntry.Value.GfxY, texelsPerWord);
    }

    /// <summary>
    /// Decode an 8bpp DC1 atlas whose primitive CLUTs can differ. The dictionary maps each local
    /// 128-pixel page column in the selected texture entry to the CLUT code that owns that column.
    /// Every page column must be mapped; requiring this metadata prevents a diagnostic from silently
    /// using row zero for an unmapped page. The returned image is RGBA8 and is safe for PNG output.
    /// </summary>
    public static DcmTexture? TryDecodePageAware(
        GianPackage pkg,
        byte[] fileBytes,
        DcmModel model,
        IReadOnlyDictionary<int, ushort> paletteByColumn)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        ArgumentNullException.ThrowIfNull(fileBytes);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(paletteByColumn);
        if (model.Variant.Game != DcmGame.Dc1) return null;

        var polygons = model.Triangles.Select(t =>
                (TPage: t.TPage, Clut: t.Clut, Uvs: new[] { t.Uv0, t.Uv1, t.Uv2 }))
            .Concat(model.Quads.Select(q =>
                (TPage: q.TPage, Clut: q.Clut, Uvs: new[] { q.Uv0, q.Uv1, q.Uv2, q.Uv3 })))
            .ToArray();
        if (polygons.Length == 0) return null;

        var tpages = polygons.Select(p => p.TPage).Distinct().ToArray();
        var firstTpage = tpages[0];
        int depthMode = (firstTpage >> 7) & 3;
        if (depthMode != 1 || tpages.Any(tp => ((tp >> 7) & 3) != depthMode)) return null;

        static bool IsTexture(GianEntryType t) =>
            t is GianEntryType.Texture or GianEntryType.Lzss1 or GianEntryType.Lzss2;
        var textureMatches = pkg.Entries
            .Where(e => IsTexture(e.Type))
            .Where(e => tpages.All(tp =>
            {
                int x = (tp & 0xF) * 64;
                int y = ((tp >> 4) & 1) * 256;
                return InX(e, x) && InY(e, y);
            }))
            .ToArray();
        if (textureMatches.Length != 1) return null;
        var texture = textureMatches[0];

        int width = texture.GfxW * 2;
        int height = texture.GfxH;
        int rowBytes = texture.GfxW * 2;
        if (width <= 0 || height <= 0 || width % TexturePageWidth != 0) return null;
        if (paletteByColumn.Keys.Any(key => key < 0 || key % TexturePageWidth != 0 || key >= width))
            return null;
        for (int column = 0; column < width; column += TexturePageWidth)
            if (!paletteByColumn.ContainsKey(column)) return null;
        foreach (var polygon in polygons)
        {
            int polygonPageX16 = (polygon.TPage & 0xF) * 64;
            int polygonPageY = ((polygon.TPage >> 4) & 1) * 256;
            foreach (var uv in polygon.Uvs)
            {
                int localX = (polygonPageX16 - texture.GfxX) * 2 + uv.U;
                int localY = polygonPageY - texture.GfxY + uv.V;
                if (localX < 0 || localX >= width || localY < 0 || localY >= height)
                    return null;
                int column = localX / TexturePageWidth * TexturePageWidth;
                if (!paletteByColumn.TryGetValue(column, out ushort mappedClut) ||
                    mappedClut != polygon.Clut)
                    return null;
            }
        }

        byte[] texData = pkg.DecodeEntry(fileBytes, texture);
        if (texture.Type is GianEntryType.Lzss1 or GianEntryType.Lzss2)
        {
            texData = UnswizzleGfx(texData, rowBytes, height);
            if (texData.Length == 0) return null;
        }
        int storedHeight = texture.Type is GianEntryType.Lzss1 or GianEntryType.Lzss2
            ? (height + 31) / 32 * 32
            : height;
        if ((long)rowBytes * storedHeight > texData.Length) return null;

        var palettes = new Dictionary<ushort, ushort[]>();
        foreach (ushort clut in paletteByColumn.Values.Distinct())
        {
            int clutX16 = (clut & 0x3F) << 4;
            int clutY = clut >> 6;
            var matches = pkg.Entries
                .Where(e => e.Type == GianEntryType.Palette)
                .Where(e => e.GfxX <= clutX16 && clutX16 + 256 <= e.GfxX + e.GfxW &&
                            e.GfxY <= clutY && clutY < e.GfxY + e.GfxH)
                .ToArray();
            if (matches.Length != 1) return null;
            var entry = matches[0];
            byte[] data = pkg.DecodeEntry(fileBytes, entry);
            int stride = entry.GfxW * 2;
            int baseOffset = (clutY - entry.GfxY) * stride + (clutX16 - entry.GfxX) * 2;
            if (baseOffset < 0 || baseOffset + 512 > data.Length) return null;
            var row = new ushort[256];
            for (int i = 0; i < row.Length; i++)
                row[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + i * 2, 2));
            palettes.Add(clut, row);
        }

        var image = new PsxImage(width, height);
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                ushort clut = paletteByColumn[col / TexturePageWidth * TexturePageWidth];
                ushort psv = palettes[clut][texData[row * rowBytes + col]];
                var (r, g, b, a) = PsxImage.FromPsv(psv);
                image.Set(col, row, r, g, b, a);
            }

        return new DcmTexture(image, texture.GfxX, texture.GfxY, texelsPerWord: 2);
    }

    /// <summary>
    /// Expand the 64-byte-wide by 32-row tile order used by compressed package graphics into
    /// ordinary VRAM scanlines. The stored height is rounded up to a complete tile band (Regina's
    /// 464 visible rows occupy 480 stored rows), so the final partial band must still be consumed.
    /// </summary>
    public static byte[] UnswizzleGfx(ReadOnlySpan<byte> swizzled, int rowBytes, int visibleHeight)
    {
        const int TileWidthBytes = 64;
        const int TileHeight = 32;
        if (rowBytes <= 0 || rowBytes % TileWidthBytes != 0 || visibleHeight <= 0)
            return Array.Empty<byte>();

        int storedHeight = (visibleHeight + TileHeight - 1) / TileHeight * TileHeight;
        long required = (long)rowBytes * storedHeight;
        if (required > int.MaxValue || swizzled.Length < required)
            return Array.Empty<byte>();

        var linear = new byte[(int)required];
        int source = 0;
        for (int tileY = 0; tileY < storedHeight; tileY += TileHeight)
            for (int tileX = 0; tileX < rowBytes; tileX += TileWidthBytes)
                for (int row = 0; row < TileHeight; row++)
                {
                    swizzled.Slice(source, TileWidthBytes).CopyTo(
                        linear.AsSpan((tileY + row) * rowBytes + tileX, TileWidthBytes));
                    source += TileWidthBytes;
                }
        return linear;
    }

    /// <summary>
    /// Pack ordinary VRAM scanlines into the exact 64-byte-wide by 32-row tile order consumed by
    /// compressed DC graphics. The input includes the rounded storage band after the visible height;
    /// those rows are preserved and emitted too. This is the byte-for-byte inverse of
    /// <see cref="UnswizzleGfx"/>.
    /// </summary>
    public static byte[] SwizzleGfx(ReadOnlySpan<byte> linear, int rowBytes, int visibleHeight)
    {
        const int TileWidthBytes = 64;
        const int TileHeight = 32;
        if (rowBytes <= 0 || rowBytes % TileWidthBytes != 0 || visibleHeight <= 0)
            return Array.Empty<byte>();

        int storedHeight = checked((visibleHeight + TileHeight - 1) / TileHeight * TileHeight);
        long required = (long)rowBytes * storedHeight;
        if (required > int.MaxValue || linear.Length < required)
            return Array.Empty<byte>();

        var swizzled = new byte[(int)required];
        int destination = 0;
        for (int tileY = 0; tileY < storedHeight; tileY += TileHeight)
            for (int tileX = 0; tileX < rowBytes; tileX += TileWidthBytes)
                for (int row = 0; row < TileHeight; row++)
                {
                    linear.Slice((tileY + row) * rowBytes + tileX, TileWidthBytes)
                        .CopyTo(swizzled.AsSpan(destination, TileWidthBytes));
                    destination += TileWidthBytes;
                }
        return swizzled;
    }

    /// <summary>
    /// Decode a DC2 model's skin from its separate <c>E*.TEX</c> file — the DC2 analog of
    /// <see cref="TryDecode"/>, whose in-package gfx-entry path finds nothing for DC2 (the framebuffer +
    /// CLUT live in the <c>.TEX</c>, not the model package). The model's header <c>tpage</c> then drives
    /// <see cref="DcmTexture.MapUv"/> exactly as DC1's per-poly tpage does. Zero-valued tpage/clut
    /// words remain valid selectors. Returns null for a non-DC2 model, an unsupported depth, or a
    /// <c>.TEX</c> whose layout does not parse or cannot unambiguously satisfy those selectors
    /// (<see cref="Dc2TexFile"/>).
    /// </summary>
    public static DcmTexture? TryDecodeDc2(byte[] texBytes, DcmModel model)
    {
        if (model.Variant.Game != DcmGame.Dc2) return null;
        ushort tpage = model.Header.TPage, clut = model.Header.Clut;
        int depthMode = (tpage >> 7) & 3;
        if (depthMode is not (1 or 2)) return null;

        // The model's tpage/clut select the VRAM page + palette; for a multi-page variant .TEX
        // (E00_01/02/03) this picks the one page the model samples.
        int pageX16 = (tpage & 0xF) * 64, pageY = ((tpage >> 4) & 1) * 256;
        int clutX16 = (clut & 0x3F) << 4, clutY = clut >> 6;
        return Dc2TexFile.Read(texBytes)?.DecodeAt(
            pageX16, pageY, clutX16, clutY, texelsPerWord: depthMode == 1 ? 2 : 1);
    }

    private static GianEntry? FindGfx(GianPackage pkg, Func<GianEntryType, bool> typeOk, Func<GianEntry, bool> match)
    {
        foreach (var e in pkg.Entries)
            if (typeOk(e.Type) && match(e)) return e;
        return null;
    }

    private static bool InX(GianEntry e, int x16) => e.GfxX <= x16 && x16 < e.GfxX + e.GfxW;
    private static bool InY(GianEntry e, int y) => e.GfxY <= y && y < e.GfxY + e.GfxH;
}
