using System.Buffers.Binary;

namespace DinoCrisis.Model.Containers;

/// <summary>
/// Writer for the DC1 "Gian package" container — the symmetric counterpart to
/// <see cref="GianPackage"/>. Emits the 2048-byte entry table (16-byte DC1 entries) followed by each
/// entry's payload on a 2048-byte sector boundary, so <see cref="GianPackage.TryParse"/> reads back the
/// same entries. Payloads are stored verbatim (uncompressed) — suitable for a <c>Data</c> model entry
/// plus <c>Texture</c>/<c>Palette</c> gfx entries.
///
/// <para>Only DC1 (16-byte entries) is written. Include at least two entries: a lone entry would leave
/// bytes 16..31 zero, which <see cref="GianPackage.TryParse"/> reads as the DC2 marker. The model +
/// texture + palette bundle (three entries) satisfies this.</para>
/// </summary>
public sealed class GianPackageBuilder
{
    private readonly struct Entry
    {
        public Entry(GianEntryType type, byte[] data, ushort x, ushort y, ushort w, ushort h)
        { Type = type; Data = data; X = x; Y = y; W = w; H = h; }
        public GianEntryType Type { get; }
        public byte[] Data { get; }
        public ushort X { get; }
        public ushort Y { get; }
        public ushort W { get; }
        public ushort H { get; }
    }

    private readonly List<Entry> _entries = new();

    /// <summary>Append an uncompressed <c>Data</c> entry (e.g. a DCM model blob). Its reserve/gfx words
    /// are left zero.</summary>
    public GianPackageBuilder AddData(byte[] data)
    {
        _entries.Add(new Entry(GianEntryType.Data, data ?? throw new System.ArgumentNullException(nameof(data)), 0, 0, 0, 0));
        return this;
    }

    /// <summary>Append a <c>Texture</c> framebuffer entry with its VRAM rect (x/w in 16-bit words).</summary>
    public GianPackageBuilder AddTexture(byte[] framebuffer, ushort x16, ushort y, ushort w16, ushort h)
    {
        _entries.Add(new Entry(GianEntryType.Texture, framebuffer ?? throw new System.ArgumentNullException(nameof(framebuffer)), x16, y, w16, h));
        return this;
    }

    /// <summary>Append a <c>Palette</c> (CLUT) entry with its VRAM rect.</summary>
    public GianPackageBuilder AddPalette(byte[] clut, ushort x16, ushort y, ushort w16, ushort h)
    {
        _entries.Add(new Entry(GianEntryType.Palette, clut ?? throw new System.ArgumentNullException(nameof(clut)), x16, y, w16, h));
        return this;
    }

    /// <summary>Append any entry with its raw offset-8..15 meta words (gfx <c>{x,y,w,h}</c> for
    /// Texture/Palette; reserve words for other types). Use when carrying stock DC2 entries verbatim.</summary>
    public GianPackageBuilder Add(GianEntryType type, byte[] data, ushort x, ushort y, ushort w, ushort h)
    {
        _entries.Add(new Entry(type, data ?? throw new System.ArgumentNullException(nameof(data)), x, y, w, h));
        return this;
    }

    /// <summary>Append a DC2 model as an uncompressed <c>Data</c> entry carrying its absolute load base in
    /// reserve0 (offset +8, split across the gfx x/y words); gfx w/h are zero (a model has no VRAM rect).</summary>
    public GianPackageBuilder AddDc2Model(byte[] data, uint loadBase)
        => Add(GianEntryType.Data, data, (ushort)(loadBase & 0xFFFF), (ushort)(loadBase >> 16), 0, 0);

    /// <summary>Serialize as a DC2 package (32-byte entries: type@0, size@4, meta@8..15, rest zero), the
    /// twin of <see cref="Build"/>. <see cref="GianPackage.TryParse"/> reads back the same entries.</summary>
    public byte[] BuildDc2()
    {
        if (_entries.Count == 0)
            throw new System.InvalidOperationException("package has no entries");
        if (_entries.Count > GianPackage.HeaderSize / GianPackage.Dc2EntrySize)
            throw new System.InvalidOperationException("too many entries for the 2048-byte DC2 table");

        var offsets = new int[_entries.Count];
        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < _entries.Count; i++)
        {
            offsets[i] = pos;
            pos += Align(_entries[i].Data.Length, GianPackage.SectorSize);
        }

        var buf = new byte[pos];
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int o = i * GianPackage.Dc2EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0, 4), (uint)e.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 4, 4), (uint)e.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 8, 2), e.X);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 10, 2), e.Y);
            // +12/+14 = gfx width/height of a Texture/Palette entry's VRAM rect. Omitting them uploads a
            // zero-size texture -> the model samples empty VRAM and renders WHITE. They must be written.
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 12, 2), e.W);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 14, 2), e.H);
            System.Array.Copy(e.Data, 0, buf, offsets[i], e.Data.Length);
        }
        return buf;
    }

    /// <summary>Serialize the package to bytes.</summary>
    public byte[] Build()
    {
        if (_entries.Count == 0)
            throw new System.InvalidOperationException("package has no entries");
        if (_entries.Count > GianPackage.HeaderSize / GianPackage.Dc1EntrySize)
            throw new System.InvalidOperationException("too many entries for the 2048-byte DC1 table");

        // Payload offsets: from 2048, each sector-aligned.
        var offsets = new int[_entries.Count];
        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < _entries.Count; i++)
        {
            offsets[i] = pos;
            pos += Align(_entries[i].Data.Length, GianPackage.SectorSize);
        }

        var buf = new byte[pos];
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int o = i * GianPackage.Dc1EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0, 4), (uint)e.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 4, 4), (uint)e.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 8, 2), e.X);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 10, 2), e.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 12, 2), e.W);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 14, 2), e.H);
            System.Array.Copy(e.Data, 0, buf, offsets[i], e.Data.Length);
        }
        return buf;
    }

    private static int Align(int value, int sector) => (value + (sector - 1)) & ~(sector - 1);
}
