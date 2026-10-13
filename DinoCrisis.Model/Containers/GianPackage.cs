using System.Buffers.Binary;

namespace DinoCrisis.Model.Containers;

/// <summary>PC ("Gian package") entry types (the DC1/DC2 PC build). Interoperability facts.</summary>
public enum GianEntryType : uint
{
    Data = 0, Texture = 1, Palette = 2, Sound = 3, Mp3 = 4,
    Lzss0 = 5, Lzss1 = 6, Unknown = 7, Lzss2 = 8,
}

/// <summary>One entry: type, payload span, and the first reserve word (the DC2 model load base).</summary>
public readonly struct GianEntry
{
    public GianEntry(GianEntryType type, uint declaredSize, int payloadOffset, int alignedSize,
                     uint reserve0, ushort gfxX, ushort gfxY, ushort gfxW, ushort gfxH)
    {
        Type = type;
        DeclaredSize = declaredSize;
        PayloadOffset = payloadOffset;
        AlignedSize = alignedSize;
        Reserve0 = reserve0;
        GfxX = gfxX;
        GfxY = gfxY;
        GfxW = gfxW;
        GfxH = gfxH;
    }

    public GianEntryType Type { get; }
    public uint DeclaredSize { get; }
    public int PayloadOffset { get; }
    public int AlignedSize { get; }

    /// <summary>First reserve u32 after {type,size}. For a DC2 model's LZSS0 entry this is the
    /// model's absolute load base; otherwise typically 0 / unused.</summary>
    public uint Reserve0 { get; }

    /// <summary>VRAM x of a <see cref="GianEntryType.Texture"/>/<see cref="GianEntryType.Palette"/>
    /// entry's framebuffer rect, in 16-bit units (the <c>DC2_ENTRY_GFX</c> coords; unused for other
    /// types — it overlaps <see cref="Reserve0"/>).</summary>
    public ushort GfxX { get; }

    /// <summary>VRAM y of a gfx entry's framebuffer rect.</summary>
    public ushort GfxY { get; }

    /// <summary>Width of a gfx entry's framebuffer rect, in 16-bit units.</summary>
    public ushort GfxW { get; }

    /// <summary>Height (rows) of a gfx entry's framebuffer rect.</summary>
    public ushort GfxH { get; }

    /// <summary>True when this entry stores its payload LZSS-compressed (types 5/6/7/8).</summary>
    public bool IsCompressed => Type is GianEntryType.Lzss0 or GianEntryType.Lzss1
                                     or GianEntryType.Lzss2 or GianEntryType.Unknown;

    public override string ToString() => $"{Type} @0x{PayloadOffset:X} ({DeclaredSize} B)";
}

/// <summary>
/// Parser for the Dino Crisis "Gian package" container (the <c>stNXX.dat</c>, <c>core*.dat</c>,
/// <c>E*.DAT</c> files). 2048-byte entry header, fixed-size entries (16 B DC1 / 32 B DC2, detected
/// by a zero-probe at offset 16), payloads from offset 2048 each advancing by
/// <c>align(size, 2048)</c>. Read-only; payload bytes preserved verbatim.
///
/// <para>Ported from DinoRand's own MIT parser (our code), extended to capture
/// <see cref="GianEntry.Reserve0"/>.</para>
/// </summary>
public sealed class GianPackage
{
    public const int HeaderSize = 2048;
    public const int SectorSize = 2048;
    public const int Dc1EntrySize = 16;
    public const int Dc2EntrySize = 32;

    private GianPackage(int entrySize, bool isDc2, IReadOnlyList<GianEntry> entries)
    {
        EntrySize = entrySize;
        IsDc2 = isDc2;
        Entries = entries;
    }

    public int EntrySize { get; }
    public bool IsDc2 { get; }
    public IReadOnlyList<GianEntry> Entries { get; }

    private static int Align(uint value, int sector) => (int)((value + (uint)(sector - 1)) & ~(uint)(sector - 1));

    /// <summary>Parse; null when the bytes are too short or unrecognized.</summary>
    public static GianPackage? TryParse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) return null;

        bool isDc2 = bytes.Length >= 32
                     && U32(bytes, 16) == 0 && U32(bytes, 20) == 0
                     && U32(bytes, 24) == 0 && U32(bytes, 28) == 0;
        int entrySize = isDc2 ? Dc2EntrySize : Dc1EntrySize;
        int slots = HeaderSize / entrySize;

        var entries = new List<GianEntry>();
        int pos = HeaderSize;
        for (int i = 0; i < slots; i++)
        {
            int entryOff = i * entrySize;
            uint type = U32(bytes, entryOff);
            uint size = U32(bytes, entryOff + 4);
            if (type > (uint)GianEntryType.Lzss2) break;
            if (size == 0) break;
            if (pos + (long)size > bytes.Length) break;

            uint reserve0 = U32(bytes, entryOff + 8); // first reserve word (DC2 load base)
            // For Texture/Palette entries the same 8 bytes are gfx coords {x,y,w,h} (DC2_ENTRY_GFX).
            ushort gfxX = U16(bytes, entryOff + 8), gfxY = U16(bytes, entryOff + 10);
            ushort gfxW = U16(bytes, entryOff + 12), gfxH = U16(bytes, entryOff + 14);
            int aligned = Align(size, SectorSize);
            entries.Add(new GianEntry((GianEntryType)type, size, pos, aligned, reserve0, gfxX, gfxY, gfxW, gfxH));
            pos += aligned;
            if (pos > bytes.Length) break;
        }

        return entries.Count > 0 ? new GianPackage(entrySize, isDc2, entries) : null;
    }

    /// <summary>This entry's payload, decompressed if its type is compressed.</summary>
    public byte[] DecodeEntry(ReadOnlySpan<byte> fileBytes, GianEntry entry)
    {
        var payload = fileBytes.Slice(entry.PayloadOffset, (int)entry.DeclaredSize);
        return entry.IsCompressed ? Lzss.Decompress(payload) : payload.ToArray();
    }

    private static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off, 4));
    private static ushort U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off, 2));
}
