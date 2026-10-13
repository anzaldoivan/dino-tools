using System.Buffers.Binary;

namespace DinoCrisis.Model;

/// <summary>
/// A parsed Dino Crisis DCM model: header, parts (bones), the vertex/normal SVECTOR pools, and the
/// triangle/quad polygons. Format byte-validated in docs/dc1/CHARACTER-IMPORT-AUTORIG-RESEARCH.md §7
/// across both games / three container types. The model keeps the decompressed buffer it lives in
/// (<see cref="Raw"/>) plus its <see cref="ModelHead"/> and <see cref="LoadBase"/> so a writer can
/// re-emit it byte-exactly (see <c>DcmModel.Builder</c>).
/// </summary>
public sealed partial class DcmModel
{
    /// <summary>The decompressed buffer this model lives in.</summary>
    public byte[] Raw { get; }

    /// <summary>Byte offset of the model header within <see cref="Raw"/>.</summary>
    public int ModelHead { get; }

    /// <summary>VA the model's internal pointers are relative to (<c>blobOffset = va - LoadBase</c>).</summary>
    public uint LoadBase { get; }

    /// <summary>Layout constants for this model's game.</summary>
    public DcmVariant Variant { get; }

    public DcmHeader Header { get; }
    public IReadOnlyList<DcmPart> Parts { get; }
    public IReadOnlyList<SVector> Vertices { get; }
    public IReadOnlyList<SVector> Normals { get; }
    public IReadOnlyList<DcmTriangle> Triangles { get; }
    public IReadOnlyList<DcmQuad> Quads { get; }

    /// <summary>Evidence used to establish the parallel pool counts.</summary>
    public DcmPoolCountProvenance PoolCountProvenance { get; }

    /// <summary>Validated owned sections between the normal and triangle pools. Empty for the
    /// ordinary contiguous layout.</summary>
    public IReadOnlyList<DcmKnownSection> InterveningSections { get; }

    /// <summary>Number of vertices in the shared pool (polygon indices must be below this).</summary>
    public int VertexCount => Vertices.Count;

    private DcmModel(byte[] raw, int modelHead, uint loadBase, DcmVariant variant, DcmHeader header,
                     List<DcmPart> parts, List<SVector> vertices, List<SVector> normals,
                     List<DcmTriangle> triangles, List<DcmQuad> quads,
                     DcmPoolCountProvenance poolCountProvenance,
                     IReadOnlyList<DcmKnownSection> interveningSections)
    {
        Raw = raw;
        ModelHead = modelHead;
        LoadBase = loadBase;
        Variant = variant;
        Header = header;
        Parts = parts;
        Vertices = vertices;
        Normals = normals;
        Triangles = triangles;
        Quads = quads;
        PoolCountProvenance = poolCountProvenance;
        InterveningSections = interveningSections;
    }

    /// <summary>Resolve a VA to an offset within <see cref="Raw"/>.</summary>
    public int Resolve(uint va) => (int)(va - LoadBase);

    // ---- reading ----

    /// <summary>
    /// Parse the model at <paramref name="modelHead"/> in <paramref name="blob"/>, with pointers
    /// relative to <paramref name="loadBase"/>. Throws <see cref="InvalidDataException"/> if the
    /// declared layout runs outside the buffer (use <see cref="TryRead"/> for a tolerant scan).
    /// </summary>
    public static DcmModel Read(byte[] blob, int modelHead, uint loadBase, DcmVariant variant)
        => ReadCore(blob, modelHead, loadBase, variant, forensicLayout: null);

    /// <summary>
    /// Parse a DC1 model whose normal pool is followed by caller-proven non-model sections before
    /// its triangle pool. This is an explicit forensic surface; ordinary <see cref="Read"/> remains
    /// gap-free and derives both pool counts from adjacent pointers.
    /// </summary>
    public static DcmModel ReadForensicDc1(
        byte[] blob, int modelHead, uint loadBase, DcmForensicLayout forensicLayout)
    {
        ArgumentNullException.ThrowIfNull(forensicLayout);
        return ReadCore(blob, modelHead, loadBase, DcmVariant.Dc1, forensicLayout);
    }

    private static DcmModel ReadCore(
        byte[] blob, int modelHead, uint loadBase, DcmVariant variant,
        DcmForensicLayout? forensicLayout)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(variant);
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o, 4));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(o, 2));
        short I16(int o) => BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(o, 2));

        Require(HasRange(blob.Length, modelHead, variant.HeaderSize + 4), "header out of range");

        var header = new DcmHeader
        {
            VertexPtr = U32(modelHead + 0),
            NormalPtr = U32(modelHead + 4),
            TrianglePtr = U32(modelHead + 8),
            QuadPtr = U32(modelHead + 0xc),
            TriangleCount = U16(modelHead + 0x10),
            QuadCount = U16(modelHead + 0x12),
            PartCount = (int)U32(modelHead + 0x14),
        };
        if (variant.Game == DcmGame.Dc2)
        {
            header.TPage = U16(modelHead + 0x18);
            header.Clut = U16(modelHead + 0x1a);
        }

        Require(header.PartCount is > 0 and <= 64, $"implausible part count {header.PartCount}");
        int mapOff = checked(modelHead + variant.MapOffset);
        int partBytes = checked(header.PartCount * DcmVariant.PartStride);
        Require(HasRange(blob.Length, mapOff, partBytes), "part array out of range");

        var parts = new List<DcmPart>(header.PartCount);
        for (int i = 0; i < header.PartCount; i++)
        {
            int o = mapOff + i * DcmVariant.PartStride;
            parts.Add(new DcmPart
            {
                X = I16(o + 0),
                Y = I16(o + 2),
                Z = I16(o + 4),
                Parent = (sbyte)blob[o + 6],
                Child = blob[o + 7],
                TrianglePtr = U32(o + 8),
                QuadPtr = U32(o + 0xc),
                TriangleCount = U16(o + 0x10),
                QuadCount = U16(o + 0x12),
            });
        }

        Require(header.VertexPtr <= header.NormalPtr && header.NormalPtr <= header.TrianglePtr &&
                header.TrianglePtr <= header.QuadPtr,
            "pool pointer order must be vertex <= normal <= triangle <= quad");

        int vtxOff = ResolveOffset(header.VertexPtr, loadBase, "vertex pointer");
        int nrmOff = ResolveOffset(header.NormalPtr, loadBase, "normal pointer");
        int triOff = ResolveOffset(header.TrianglePtr, loadBase, "triangle pointer");
        int quadOff = ResolveOffset(header.QuadPtr, loadBase, "quad pointer");

        uint vertexBytes = header.NormalPtr - header.VertexPtr;
        Require(vertexBytes % DcmVariant.VertexStride == 0,
            $"pool pointer alignment must be an exact {DcmVariant.VertexStride}-byte SVECTOR multiple");
        int vertexCount = checked((int)(vertexBytes / DcmVariant.VertexStride));

        int normalCount;
        IReadOnlyList<DcmKnownSection> interveningSections;
        DcmPoolCountProvenance poolCountProvenance;
        if (forensicLayout is null)
        {
            uint normalBytes = header.TrianglePtr - header.NormalPtr;
            Require(normalBytes % DcmVariant.VertexStride == 0,
                $"pool pointer alignment must be an exact {DcmVariant.VertexStride}-byte SVECTOR multiple");
            normalCount = checked((int)(normalBytes / DcmVariant.VertexStride));
            interveningSections = Array.Empty<DcmKnownSection>();
            poolCountProvenance = DcmPoolCountProvenance.ContiguousPointerSpans;
        }
        else
        {
            normalCount = forensicLayout.NormalCount;
            Require(normalCount >= 0, "forensic normal count must not be negative");
            Require(normalCount == vertexCount,
                $"forensic parallel pool contract requires {vertexCount} normals, declared {normalCount}");
            int normalBytes = checked(normalCount * DcmVariant.VertexStride);
            int normalEnd = checked(nrmOff + normalBytes);
            Require(normalEnd <= triOff, "forensic normal pool overlaps the triangle pool");
            ValidateKnownSections(blob, normalEnd, triOff, forensicLayout.Sections);
            interveningSections = forensicLayout.Sections.ToArray();
            poolCountProvenance = DcmPoolCountProvenance.ParallelPoolsWithKnownSections;
        }

        uint triangleBytes = header.QuadPtr - header.TrianglePtr;
        int declaredTriangleBytes = checked(header.TriangleCount * variant.TriangleStride);
        Require(triangleBytes == (uint)declaredTriangleBytes,
            "triangle pool span must exactly match triangle count and stride");
        Require(HasRange(blob.Length, vtxOff, checked(vertexCount * DcmVariant.VertexStride)), "vertex pool out of range");
        Require(HasRange(blob.Length, nrmOff, checked(normalCount * DcmVariant.VertexStride)), "normal pool out of range");
        Require(HasRange(blob.Length, triOff, declaredTriangleBytes), "triangle pool out of range");
        Require(HasRange(blob.Length, quadOff, checked(header.QuadCount * variant.QuadStride)), "quad pool out of range");

        var vertices = ReadSVectors(blob, vtxOff, vertexCount);
        var normals = ReadSVectors(blob, nrmOff, normalCount);

        var triangles = new List<DcmTriangle>(header.TriangleCount);
        for (int i = 0; i < header.TriangleCount; i++)
            triangles.Add(ReadTriangle(blob, triOff + i * variant.TriangleStride, variant));

        var quads = new List<DcmQuad>(header.QuadCount);
        for (int i = 0; i < header.QuadCount; i++)
            quads.Add(ReadQuad(blob, quadOff + i * variant.QuadStride, variant));

        var model = new DcmModel(
            blob, modelHead, loadBase, variant, header, parts, vertices, normals, triangles, quads,
            poolCountProvenance, interveningSections);
        if (forensicLayout is not null)
            RequireForensicStructure(model);
        return model;
    }

    private static List<SVector> ReadSVectors(byte[] blob, int off, int count)
    {
        var list = new List<SVector>(count);
        for (int i = 0; i < count; i++)
        {
            int o = off + i * DcmVariant.VertexStride;
            list.Add(new SVector(
                BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(o + 0, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(o + 2, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(o + 4, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(o + 6, 2))));
        }
        return list;
    }

    private static DcmTriangle ReadTriangle(byte[] b, int o, DcmVariant v)
    {
        ushort U16(int x) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(x, 2));
        var t = new DcmTriangle { V0 = U16(o + 0), V1 = U16(o + 2), V2 = U16(o + 4) };
        if (v.PerPolyTexture)
        {
            // DC1 DCM1_TRIMAP (16B): uv0@6, uv1@8, clut@0xa, uv2@0xc, tpage@0xe
            t.Uv0 = new Uv(b[o + 6], b[o + 7]);
            t.Uv1 = new Uv(b[o + 8], b[o + 9]);
            t.Clut = U16(o + 0x0a);
            t.Uv2 = new Uv(b[o + 0x0c], b[o + 0x0d]);
            t.TPage = U16(o + 0x0e);
        }
        else
        {
            // DC2 DCM_TRIMAP (12B): uv0@6, uv1@8, uv2@10
            t.Uv0 = new Uv(b[o + 6], b[o + 7]);
            t.Uv1 = new Uv(b[o + 8], b[o + 9]);
            t.Uv2 = new Uv(b[o + 10], b[o + 11]);
        }
        return t;
    }

    private static DcmQuad ReadQuad(byte[] b, int o, DcmVariant v)
    {
        ushort U16(int x) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(x, 2));
        var q = new DcmQuad { V0 = U16(o + 0), V1 = U16(o + 2), V2 = U16(o + 4), V3 = U16(o + 6) };
        if (v.PerPolyTexture)
        {
            // DC1 DCM1_QUADMAP (20B): uv0@8, clut@0xa, uv1@0xc, tpage@0xe, uv2@0x10, uv3@0x12
            q.Uv0 = new Uv(b[o + 8], b[o + 9]);
            q.Clut = U16(o + 0x0a);
            q.Uv1 = new Uv(b[o + 0x0c], b[o + 0x0d]);
            q.TPage = U16(o + 0x0e);
            q.Uv2 = new Uv(b[o + 0x10], b[o + 0x11]);
            q.Uv3 = new Uv(b[o + 0x12], b[o + 0x13]);
        }
        else
        {
            // DC2 DCM_QUADMAP (16B): uv0@8, uv1@10, uv2@12, uv3@14
            q.Uv0 = new Uv(b[o + 8], b[o + 9]);
            q.Uv1 = new Uv(b[o + 10], b[o + 11]);
            q.Uv2 = new Uv(b[o + 12], b[o + 13]);
            q.Uv3 = new Uv(b[o + 14], b[o + 15]);
        }
        return q;
    }

    // ---- tolerant scan-read ----

    /// <summary>
    /// Attempt to read a model at <paramref name="modelHead"/>, deriving the load base from the
    /// validated invariant that the vertex pool sits immediately after the part array
    /// (<c>vertex_ptr = loadBase + modelHead + headerSize + partCount*0x14</c>). Returns null unless
    /// every §7 structural invariant holds — so a brute-force scan can call this at every offset.
    /// </summary>
    public static DcmModel? TryRead(byte[] blob, int modelHead, DcmVariant variant)
    {
        try
        {
            if (modelHead < 0 || modelHead + variant.HeaderSize + 4 > blob.Length) return null;
            int partCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(modelHead + 0x14, 4));
            if (partCount is < 1 or > 64) return null;

            int afterParts = modelHead + variant.MapOffset + partCount * DcmVariant.PartStride;
            if (afterParts > blob.Length) return null;

            uint vptr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(modelHead + 0, 4));
            // loadBase such that resolve(vptr) == afterParts (vertex pool right after the part array)
            uint loadBase = vptr - (uint)afterParts;

            var model = Read(blob, modelHead, loadBase, variant);
            return Validate(model) ? model : null;
        }
        catch (InvalidDataException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Read at an explicit <paramref name="loadBase"/> (DC2: from the package entry's
    /// reserve word) and validate; null if any invariant fails.</summary>
    public static DcmModel? TryReadAt(byte[] blob, int modelHead, uint loadBase, DcmVariant variant)
    {
        try
        {
            var model = Read(blob, modelHead, loadBase, variant);
            return Validate(model) ? model : null;
        }
        catch (InvalidDataException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The §7 structural invariants that make a candidate a real DCM model.</summary>
    public static bool Validate(DcmModel m)
    {
        if (m.Vertices.Count != m.Normals.Count) return false;

        // vertex pool sits right after the part array
        if (m.Resolve(m.Header.VertexPtr) != m.ModelHead + m.Variant.MapOffset + m.Parts.Count * DcmVariant.PartStride)
            return false;

        // header tri/quad pointers == part 0's pools
        if (m.Parts.Count == 0) return false;
        if (m.Header.TrianglePtr != m.Parts[0].TrianglePtr || m.Header.QuadPtr != m.Parts[0].QuadPtr) return false;

        // parent tree: root negative, others reference an earlier part; rest-pose coords sane
        if (m.Parts[0].Parent >= 0) return false;
        for (int i = 0; i < m.Parts.Count; i++)
        {
            var p = m.Parts[i];
            if (i > 0 && (p.Parent < 0 || p.Parent >= i)) return false;
            if (Math.Abs(p.X) > 4096 || Math.Abs(p.Y) > 4096 || Math.Abs(p.Z) > 4096) return false;
        }

        // per-part counts sum to the header totals
        int triSum = 0, quadSum = 0;
        foreach (var p in m.Parts) { triSum += p.TriangleCount; quadSum += p.QuadCount; }
        if (triSum != m.Header.TriangleCount || quadSum != m.Header.QuadCount) return false;

        // pointer partition: each part's pool follows the previous by count*stride
        for (int i = 1; i < m.Parts.Count; i++)
        {
            if (m.Parts[i].TrianglePtr != m.Parts[i - 1].TrianglePtr + (uint)(m.Parts[i - 1].TriangleCount * m.Variant.TriangleStride)) return false;
            if (m.Parts[i].QuadPtr != m.Parts[i - 1].QuadPtr + (uint)(m.Parts[i - 1].QuadCount * m.Variant.QuadStride)) return false;
        }

        // every polygon vertex index is in range
        foreach (var t in m.Triangles)
            if (t.V0 >= m.VertexCount || t.V1 >= m.VertexCount || t.V2 >= m.VertexCount) return false;
        foreach (var q in m.Quads)
            if (q.V0 >= m.VertexCount || q.V1 >= m.VertexCount || q.V2 >= m.VertexCount || q.V3 >= m.VertexCount) return false;

        return true;
    }

    private static int ResolveOffset(uint va, uint loadBase, string field)
    {
        Require(va >= loadBase, $"{field} is below the load base range");
        uint offset = va - loadBase;
        Require(offset <= int.MaxValue, $"{field} resolves outside the supported integer range");
        return (int)offset;
    }

    private static void ValidateKnownSections(
        byte[] blob, int gapStart, int gapEnd, IReadOnlyList<DcmKnownSection> sections)
    {
        int cursor = gapStart;
        foreach (var section in sections)
        {
            Require(section.Offset == cursor,
                "forensic known sections must cover the complete gap in order");
            Require(section.Length >= 0, "forensic known section length must not be negative");
            Require(HasRange(blob.Length, section.Offset, section.Length),
                "forensic known section out of range");
            long end = (long)section.Offset + section.Length;
            Require(end <= gapEnd,
                "forensic known sections must cover the complete gap without surplus bytes");

            ReadOnlySpan<byte> actual = blob.AsSpan(section.Offset, section.Length);
            switch (section.Kind)
            {
                case DcmKnownSectionKind.ZeroFill:
                    Require(actual.IndexOfAnyExcept((byte)0) < 0,
                        "forensic zero-fill section contains nonzero bytes");
                    break;
                case DcmKnownSectionKind.PreservedTemplateBytes:
                    Require(actual.SequenceEqual(section.ExpectedTemplateBytes),
                        "forensic preserved-template section does not match its template bytes");
                    break;
                default:
                    throw new InvalidDataException("DCM: unsupported forensic known-section kind");
            }
            cursor = (int)end;
        }

        Require(cursor == gapEnd,
            "forensic known sections must cover the complete gap without missing or surplus bytes");
    }

    private static void RequireForensicStructure(DcmModel m)
    {
        Require(m.Vertices.Count == m.Normals.Count,
            "forensic position and normal pools must be parallel");
        Require(m.Resolve(m.Header.VertexPtr) ==
                m.ModelHead + m.Variant.MapOffset + m.Parts.Count * DcmVariant.PartStride,
            "forensic vertex pool must sit immediately after the part array");
        Require(m.Parts.Count > 0, "forensic model has no parts");
        Require(m.Header.TrianglePtr == m.Parts[0].TrianglePtr &&
                m.Header.QuadPtr == m.Parts[0].QuadPtr,
            "forensic header polygon pointers must match part zero");

        Require(m.Parts[0].Parent < 0, "forensic root part must have a negative parent");
        long triSum = 0, quadSum = 0;
        for (int i = 0; i < m.Parts.Count; i++)
        {
            var part = m.Parts[i];
            if (i > 0)
                Require(part.Parent >= 0 && part.Parent < i,
                    $"forensic part {i} has an invalid parent");
            Require(Math.Abs((int)part.X) <= 4096 && Math.Abs((int)part.Y) <= 4096 &&
                    Math.Abs((int)part.Z) <= 4096,
                $"forensic part {i} has implausible rest coordinates");
            triSum += part.TriangleCount;
            quadSum += part.QuadCount;
        }
        Require(triSum == m.Header.TriangleCount,
            "forensic part triangle counts do not match the header total");
        Require(quadSum == m.Header.QuadCount,
            "forensic part quad counts do not match the header total");

        for (int i = 1; i < m.Parts.Count; i++)
        {
            var previous = m.Parts[i - 1];
            ulong expectedTriangle = (ulong)previous.TrianglePtr +
                (ulong)previous.TriangleCount * (uint)m.Variant.TriangleStride;
            ulong expectedQuad = (ulong)previous.QuadPtr +
                (ulong)previous.QuadCount * (uint)m.Variant.QuadStride;
            Require(expectedTriangle <= uint.MaxValue &&
                    m.Parts[i].TrianglePtr == (uint)expectedTriangle,
                "forensic part triangle partitions overlap or contain a gap");
            Require(expectedQuad <= uint.MaxValue &&
                    m.Parts[i].QuadPtr == (uint)expectedQuad,
                "forensic part quad partitions overlap or contain a gap");
        }

        foreach (var triangle in m.Triangles)
            Require(triangle.V0 < m.VertexCount && triangle.V1 < m.VertexCount &&
                    triangle.V2 < m.VertexCount,
                "forensic triangle vertex index is out of range");
        foreach (var quad in m.Quads)
            Require(quad.V0 < m.VertexCount && quad.V1 < m.VertexCount &&
                    quad.V2 < m.VertexCount && quad.V3 < m.VertexCount,
                "forensic quad vertex index is out of range");
    }

    private static bool HasRange(int bufferLength, int offset, int count) =>
        offset >= 0 && count >= 0 && (long)offset + count <= bufferLength;

    private static void Require(bool cond, string message)
    {
        if (!cond) throw new InvalidDataException("DCM: " + message);
    }
}
