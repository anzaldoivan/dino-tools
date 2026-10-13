using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace DinoCrisis.Model.Textures;

/// <summary>The kind of a <see cref="Dc2TexBlock"/>: an indexed/direct framebuffer or an indexed palette.
/// Values match the SDK <c>GPC_*</c> tag (cited as format documentation only).</summary>
public enum Dc2TexBlockType { Texture = 1, Palette = 2 }

/// <summary>One descriptor in a DC2 <c>E*.TEX</c> file: a VRAM rectangle plus the file offset/size of
/// its data block. <see cref="X16"/>/<see cref="W16"/> are in 16-bit VRAM words (the PSX VRAM unit); an
/// 8bpp framebuffer is <c>W16 * 2</c> texels wide; direct 16bpp is <c>W16</c> texels wide.</summary>
public readonly record struct Dc2TexBlock(
    Dc2TexBlockType Type, int X16, int Y, int W16, int H, int DataOffset, int DataSize);

/// <summary>
/// Reader for a DC2 <c>E*.TEX</c> texture file — the separate skin package a DC2 enemy/character model
/// (<c>E*.DAT</c>) samples (the model carries header-level <c>tpage</c>/<c>clut</c> codes; the framebuffer
/// + CLUT live here, not in the model package). Decoded offline from the original game data; format
/// validated by probe in <c>docs/dc1/DCM-DC2-TEXTURE-PLAN.md</c> §3.
///
/// <para><b>Layout (validated on single- and multi-page files).</b> A 32-byte descriptor table at the file
/// start — each record <c>{u32 tag(1=TEXTURE,2=PALETTE); u32 dataSize; u16 x,y,w,h; 16 reserved}</c> —
/// terminated by the first non-{1,2} tag (the trailing <c>"dummy header"</c> filler). Data blocks then
/// follow a fixed packing: the header is padded to a <c>0x800</c> boundary, the palette(s) sit there, and
/// the framebuffer(s) are <c>0x1000</c>-aligned (PSX DMA) — so a single texture ends exactly at file end.
/// The reader recomputes the offsets from the table + these alignments and <b>self-validates</b> that the
/// layout consumes the whole file, allowing the observed sub-alignment trailing pad on multi-page
/// skin variants such as <c>E00_02</c>.</para>
/// </summary>
public sealed class Dc2TexFile
{
    private const int RecordStride = 0x20;   // 32-byte descriptor records (the DC2 Gian-entry convention)
    private const int HeaderAlign = 0x800;   // descriptors + "dummy header" padding round up to here
    private const int TextureAlign = 0x1000; // framebuffers are 0x1000-aligned (PSX DMA upload)

    public byte[] Raw { get; }
    public IReadOnlyList<Dc2TexBlock> Blocks { get; }

    private Dc2TexFile(byte[] raw, List<Dc2TexBlock> blocks)
    {
        Raw = raw;
        Blocks = blocks;
    }

    /// <summary>Parse a <c>.TEX</c>. Returns null when the descriptor table is empty or the packing rule
    /// does not consume the file within the observed sub-alignment trailing-pad allowance.</summary>
    public static Dc2TexFile? Read(byte[] bytes)
    {
        if (bytes is null) return null;

        // 1. Descriptor table: 32-byte records while the tag is TEXTURE(1)/PALETTE(2).
        var descs = new List<(Dc2TexBlockType Type, int Size, int X, int Y, int W, int H)>();
        int o = 0;
        while (o + RecordStride <= bytes.Length)
        {
            uint tag = U32(bytes, o);
            if (tag is not (1 or 2)) break;
            uint rawSize = U32(bytes, o + 4);
            if (rawSize == 0 || rawSize > int.MaxValue || rawSize > bytes.Length) return null;
            descs.Add(((Dc2TexBlockType)tag, (int)rawSize,
                       U16(bytes, o + 8), U16(bytes, o + 0xa), U16(bytes, o + 0xc), U16(bytes, o + 0xe)));
            o += RecordStride;
        }
        if (descs.Count == 0) return null;
        int descEnd = descs.Count * RecordStride;

        // 2. Forward packing: palettes at the 0x800-padded header, then 0x1000-aligned framebuffers.
        var blocks = new List<Dc2TexBlock>(descs.Count);
        int cursor;
        try
        {
            cursor = Align(descEnd, HeaderAlign);
            foreach (var d in descs.Where(d => d.Type == Dc2TexBlockType.Palette))
            {
                blocks.Add(new Dc2TexBlock(d.Type, d.X, d.Y, d.W, d.H, cursor, d.Size));
                cursor = checked(cursor + d.Size);
            }
            cursor = Align(cursor, TextureAlign);
            foreach (var d in descs.Where(d => d.Type == Dc2TexBlockType.Texture))
            {
                blocks.Add(new Dc2TexBlock(d.Type, d.X, d.Y, d.W, d.H, cursor, d.Size));
                cursor = checked(cursor + d.Size);
            }
        }
        catch (OverflowException)
        {
            return null;
        }

        // 3. Self-validate: blocks stay in bounds and the layout closes — exactly for a single page, or
        // with < one framebuffer of trailing padding for a multi-page variant (E00_02: 0x800 of tail pad).
        if (cursor > bytes.Length || bytes.Length - cursor >= TextureAlign) return null;
        foreach (var b in blocks)
            if (b.DataOffset < descEnd || b.DataOffset + b.DataSize > bytes.Length) return null;

        return new Dc2TexFile(bytes, blocks);
    }

    /// <summary>Decode the primary framebuffer into a <see cref="DcmTexture"/>. A palette block selects
    /// 8bpp indexed decoding; a texture-only file selects direct 16bpp. For a multi-page variant use
    /// <see cref="DecodeAt"/> with the model's known depth.</summary>
    public DcmTexture? DecodeSinglePage()
    {
        var t = FirstOfType(Dc2TexBlockType.Texture);
        var p = FirstOfType(Dc2TexBlockType.Palette);
        return t is null ? null : Decode(t.Value, p, p is null ? 1 : 2);
    }

    /// <summary>Decode the texture page + palette selected by VRAM coords — the page whose rect starts at
    /// (<paramref name="pageX16"/>, <paramref name="pageY"/>) and the palette at
    /// (<paramref name="clutX16"/>, <paramref name="clutY"/>), as a model's <c>tpage</c>/<c>clut</c> encode.
    /// Falls back only when there is exactly one texture or palette of that type (single-page tolerance).
    /// An unmatched selector is ambiguous in a multi-page file and returns null.</summary>
    public DcmTexture? DecodeAt(int pageX16, int pageY, int clutX16, int clutY, int texelsPerWord = 2)
    {
        var t = MatchOrSingle(Dc2TexBlockType.Texture, pageX16, pageY);
        var p = texelsPerWord == 2
            ? MatchOrSingle(Dc2TexBlockType.Palette, clutX16, clutY)
            : null;
        if (texelsPerWord == 2 && p is null) return null;
        return t is null ? null : Decode(t.Value, p, texelsPerWord);
    }

    private DcmTexture? Decode(Dc2TexBlock t, Dc2TexBlock? p, int texelsPerWord)
    {
        if (texelsPerWord is not (1 or 2)) return null;
        int width = t.W16 * texelsPerWord;
        int height = t.H;
        int bytesPerTexel = texelsPerWord == 1 ? 2 : 1;
        if (width <= 0 || height <= 0 || (long)width * height * bytesPerTexel > t.DataSize) return null;

        ushort[]? palette = null;
        if (texelsPerWord == 2)
        {
            if (p is null || p.Value.DataSize < 256 * 2) return null;
            palette = new ushort[256];
            for (int i = 0; i < 256; i++)
                palette[i] = U16(Raw, p.Value.DataOffset + i * 2);
        }

        var img = new PsxImage(width, height);
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                int pixel = row * width + col;
                ushort psv = texelsPerWord == 1
                    ? U16(Raw, t.DataOffset + pixel * 2)
                    : palette![Raw[t.DataOffset + pixel]];
                var (r, g, b, a) = PsxImage.FromPsv(psv);
                img.Set(col, row, r, g, b, a);
            }

        return new DcmTexture(img, t.X16, t.Y, texelsPerWord);
    }

    private Dc2TexBlock? FirstOfType(Dc2TexBlockType type)
    {
        foreach (var b in Blocks) if (b.Type == type) return b;
        return null;
    }

    private Dc2TexBlock? Match(Dc2TexBlockType type, int x16, int y)
    {
        foreach (var b in Blocks) if (b.Type == type && b.X16 == x16 && b.Y == y) return b;
        return null;
    }

    private Dc2TexBlock? MatchOrSingle(Dc2TexBlockType type, int x16, int y)
    {
        Dc2TexBlock? only = null;
        int count = 0;
        foreach (var b in Blocks)
        {
            if (b.Type != type) continue;
            if (b.X16 == x16 && b.Y == y) return b;
            only = b;
            count++;
        }
        return count == 1 ? only : null;
    }

    private static int Align(int v, int a) => checked((v + (a - 1)) & ~(a - 1));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
}
