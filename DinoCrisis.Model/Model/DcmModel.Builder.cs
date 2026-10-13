using System;
using System.Buffers.Binary;
using System.Linq;

namespace DinoCrisis.Model;

public sealed partial class DcmModel
{
    /// <summary>A mutable copy of this model for editing + byte-exact re-emit.</summary>
    public DcmModelBuilder ToBuilder() => new(this);
}

/// <summary>
/// Mutable, re-emittable form of a <see cref="DcmModel"/> (the reader/writer split mirrors
/// ref/biohazard-utils' <c>X.cs</c> + <c>X.Builder.cs</c> pattern). <see cref="Write"/> serializes
/// the (possibly edited) structure back over a copy of the source buffer at the original offsets:
/// because every DCM region is gap-free, an unedited build reproduces the source bytes exactly, and
/// an edit changes only the touched field. Re-layout (adding/removing parts or vertices, which would
/// move pointers) is out of scope for this tier.
/// </summary>
public sealed class DcmModelBuilder
{
    public byte[] Source { get; }
    public int ModelHead { get; }
    public uint LoadBase { get; }
    public DcmVariant Variant { get; }

    public DcmHeader Header { get; }
    public List<DcmPart> Parts { get; }
    public List<SVector> Vertices { get; }
    public List<SVector> Normals { get; }
    public List<DcmTriangle> Triangles { get; }
    public List<DcmQuad> Quads { get; }

    internal DcmModelBuilder(DcmModel m)
    {
        Source = (byte[])m.Raw.Clone();
        ModelHead = m.ModelHead;
        LoadBase = m.LoadBase;
        Variant = m.Variant;
        Header = CloneHeader(m.Header);
        Parts = m.Parts.Select(ClonePart).ToList();
        Vertices = m.Vertices.ToList();
        Normals = m.Normals.ToList();
        Triangles = m.Triangles.Select(CloneTriangle).ToList();
        Quads = m.Quads.Select(CloneQuad).ToList();
    }

    /// <summary>
    /// Create an <b>empty</b> builder for constructing a DCM model from scratch (e.g. a foreign model
    /// rebuilt onto this format). Populate it with <see cref="AddVertices"/> + <see cref="AddPart"/>, then
    /// <see cref="Serialize"/> to emit a fresh blob. <see cref="Write"/> is unavailable (there is no
    /// source buffer to re-emit over); use <see cref="Serialize"/>.
    /// </summary>
    public DcmModelBuilder(DcmVariant variant)
    {
        Source = Array.Empty<byte>();
        ModelHead = 0;
        LoadBase = 0;
        Variant = variant ?? throw new ArgumentNullException(nameof(variant));
        Header = new DcmHeader();
        Parts = new List<DcmPart>();
        Vertices = new List<SVector>();
        Normals = new List<SVector>();
        Triangles = new List<DcmTriangle>();
        Quads = new List<DcmQuad>();
    }

    private int Resolve(uint va) => (int)(va - LoadBase);

    /// <summary>Serialize back to bytes (a copy of the source with every modeled field rewritten).</summary>
    public byte[] Write()
    {
        var buf = (byte[])Source.Clone();
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), v);
        void WI16(int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o, 2), v);

        // header
        W32(ModelHead + 0, Header.VertexPtr);
        W32(ModelHead + 4, Header.NormalPtr);
        W32(ModelHead + 8, Header.TrianglePtr);
        W32(ModelHead + 0xc, Header.QuadPtr);
        W16(ModelHead + 0x10, (ushort)Header.TriangleCount);
        W16(ModelHead + 0x12, (ushort)Header.QuadCount);
        W32(ModelHead + 0x14, (uint)Header.PartCount);
        if (Variant.Game == DcmGame.Dc2)
        {
            W16(ModelHead + 0x18, Header.TPage);
            W16(ModelHead + 0x1a, Header.Clut);
        }

        // parts
        int mapOff = ModelHead + Variant.MapOffset;
        for (int i = 0; i < Parts.Count; i++)
        {
            int o = mapOff + i * DcmVariant.PartStride;
            var p = Parts[i];
            WI16(o + 0, p.X);
            WI16(o + 2, p.Y);
            WI16(o + 4, p.Z);
            buf[o + 6] = (byte)p.Parent;
            buf[o + 7] = p.Child;
            W32(o + 8, p.TrianglePtr);
            W32(o + 0xc, p.QuadPtr);
            W16(o + 0x10, (ushort)p.TriangleCount);
            W16(o + 0x12, (ushort)p.QuadCount);
        }

        // vertex + normal pools
        WriteSVectors(buf, Resolve(Header.VertexPtr), Vertices);
        WriteSVectors(buf, Resolve(Header.NormalPtr), Normals);

        // triangle + quad pools
        int triOff = Resolve(Header.TrianglePtr);
        for (int i = 0; i < Triangles.Count; i++)
            WriteTriangle(buf, triOff + i * Variant.TriangleStride, Triangles[i], Variant);
        int quadOff = Resolve(Header.QuadPtr);
        for (int i = 0; i < Quads.Count; i++)
            WriteQuad(buf, quadOff + i * Variant.QuadStride, Quads[i], Variant);

        return buf;
    }

    /// <summary>
    /// Re-layout serializer: build a <b>fresh, standalone</b> DCM blob (ModelHead = 0) from the current
    /// lists, recomputing every pointer, the per-part triangle/quad partition, the pool offsets, and the
    /// header totals from scratch — so it tolerates a changed part/vertex/poly count (unlike
    /// <see cref="Write"/>, which re-emits in place at the source offsets). Layout is the §7 canonical,
    /// gap-free order <c>[header][parts][vertices][normals][triangles][quads]</c> at <paramref name="loadBase"/>.
    /// Throws <see cref="InvalidOperationException"/> when the flat poly lists do not match the per-part
    /// counts (a broken partition), rather than emit a silently-corrupt model.
    /// </summary>
    public byte[] Serialize(uint loadBase)
    {
        int triSum = Parts.Sum(p => p.TriangleCount);
        int quadSum = Parts.Sum(p => p.QuadCount);
        if (triSum != Triangles.Count)
            throw new InvalidOperationException($"part triangle counts ({triSum}) != triangle list ({Triangles.Count})");
        if (quadSum != Quads.Count)
            throw new InvalidOperationException($"part quad counts ({quadSum}) != quad list ({Quads.Count})");
        if (Normals.Count != Vertices.Count)
            throw new InvalidOperationException($"normal pool ({Normals.Count}) must be parallel to the vertex pool ({Vertices.Count})");

        int partsOff = Variant.MapOffset;                       // == HeaderSize; parts follow the header
        int vertsOff = partsOff + Parts.Count * DcmVariant.PartStride;
        int normsOff = vertsOff + Vertices.Count * DcmVariant.VertexStride;
        int trisOff = normsOff + Normals.Count * DcmVariant.VertexStride;
        int quadsOff = trisOff + Triangles.Count * Variant.TriangleStride;
        int end = quadsOff + Quads.Count * Variant.QuadStride;
        var buf = new byte[end];

        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), v);
        void WI16(int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o, 2), v);

        uint vertexPtr = loadBase + (uint)vertsOff;
        uint normalPtr = loadBase + (uint)normsOff;
        uint trianglePtr = loadBase + (uint)trisOff;
        uint quadPtr = loadBase + (uint)quadsOff;

        // header (recomputed pointers + totals)
        W32(0, vertexPtr);
        W32(4, normalPtr);
        W32(8, trianglePtr);
        W32(0xc, quadPtr);
        W16(0x10, (ushort)Triangles.Count);
        W16(0x12, (ushort)Quads.Count);
        W32(0x14, (uint)Parts.Count);
        if (Variant.Game == DcmGame.Dc2)
        {
            W16(0x18, Header.TPage);
            W16(0x1a, Header.Clut);
        }

        // parts (per-part partition pointers recomputed from the counts, in part order)
        uint triCursor = trianglePtr, quadCursor = quadPtr;
        for (int i = 0; i < Parts.Count; i++)
        {
            int o = partsOff + i * DcmVariant.PartStride;
            var p = Parts[i];
            WI16(o + 0, p.X);
            WI16(o + 2, p.Y);
            WI16(o + 4, p.Z);
            buf[o + 6] = (byte)p.Parent;
            buf[o + 7] = p.Child;
            W32(o + 8, triCursor);
            W32(o + 0xc, quadCursor);
            W16(o + 0x10, (ushort)p.TriangleCount);
            W16(o + 0x12, (ushort)p.QuadCount);
            triCursor += (uint)(p.TriangleCount * Variant.TriangleStride);
            quadCursor += (uint)(p.QuadCount * Variant.QuadStride);
        }

        // pools
        WriteSVectors(buf, vertsOff, Vertices);
        WriteSVectors(buf, normsOff, Normals);
        for (int i = 0; i < Triangles.Count; i++)
            WriteTriangle(buf, trisOff + i * Variant.TriangleStride, Triangles[i], Variant);
        for (int i = 0; i < Quads.Count; i++)
            WriteQuad(buf, quadsOff + i * Variant.QuadStride, Quads[i], Variant);

        return buf;
    }

    /// <summary>
    /// Append new geometry to the shared pools: one normal per position, so the vertex and normal pools stay
    /// parallel (the Inc-7 invariant the OBJ exporter and <see cref="Serialize"/> rely on). Returns the index
    /// of the first appended vertex, so a new polygon / part (<see cref="AddPart"/>) can reference
    /// <c>baseIndex .. baseIndex + count - 1</c>.
    /// </summary>
    public int AddVertices(IReadOnlyList<SVector> positions, IReadOnlyList<SVector> normals)
    {
        if (positions is null) throw new ArgumentNullException(nameof(positions));
        if (normals is null) throw new ArgumentNullException(nameof(normals));
        if (positions.Count != normals.Count)
            throw new ArgumentException($"need one normal per position ({positions.Count} positions, {normals.Count} normals)", nameof(normals));

        int baseIndex = Vertices.Count;
        Vertices.AddRange(positions);
        Normals.AddRange(normals);
        return baseIndex;
    }

    /// <summary>Append a single vertex paired with its normal; returns the new pool index.</summary>
    public int AddVertex(SVector position, SVector normal)
    {
        int index = Vertices.Count;
        Vertices.Add(position);
        Normals.Add(normal);
        return index;
    }

    /// <summary>
    /// Append a new part (bone + mesh) at the end of the model, owning <paramref name="triangles"/> and
    /// <paramref name="quads"/> — which must reference <b>existing</b> pool vertices (adding new geometry is
    /// increment 3). The part's <see cref="DcmPart.Parent"/> must reference an earlier part (or be negative
    /// only when this is the first part). The part's counts are set from the supplied polygons, and the flat
    /// poly lists stay partition-consistent for <see cref="Serialize"/> (a new last part owns the tail run).
    /// </summary>
    public void AddPart(DcmPart part, IEnumerable<DcmTriangle> triangles, IEnumerable<DcmQuad> quads)
    {
        if (part is null) throw new ArgumentNullException(nameof(part));
        var tris = triangles?.ToList() ?? new List<DcmTriangle>();
        var qs = quads?.ToList() ?? new List<DcmQuad>();

        int newIndex = Parts.Count;
        if (newIndex == 0)
        {
            if (part.Parent >= 0) throw new ArgumentException("the first part must be the root (negative Parent)", nameof(part));
        }
        else if (part.Parent < 0 || part.Parent >= newIndex)
        {
            throw new ArgumentException($"new part Parent {part.Parent} must reference an existing part [0,{newIndex})", nameof(part));
        }

        foreach (var t in tris)
            if (t.V0 >= Vertices.Count || t.V1 >= Vertices.Count || t.V2 >= Vertices.Count)
                throw new ArgumentException("triangle references a vertex outside the pool (add geometry first)", nameof(triangles));
        foreach (var q in qs)
            if (q.V0 >= Vertices.Count || q.V1 >= Vertices.Count || q.V2 >= Vertices.Count || q.V3 >= Vertices.Count)
                throw new ArgumentException("quad references a vertex outside the pool (add geometry first)", nameof(quads));

        part.TriangleCount = tris.Count;
        part.QuadCount = qs.Count;
        Parts.Add(part);
        Triangles.AddRange(tris);   // the new part is last → its run is the tail of the flat poly list
        Quads.AddRange(qs);
    }

    /// <summary>
    /// Remove the part at <paramref name="index"/> and its triangle/quad run, keeping the flat poly
    /// partition and the parent tree consistent: a part whose parent referenced a <i>later</i> part shifts
    /// down by one, and a part whose parent <i>was</i> the removed one is grafted onto the removed part's
    /// parent. The root (index 0) cannot be removed. (The SDK <see cref="DcmPart.Child"/> byte is not
    /// remapped — it is not part of the FK parent tree this toolset assembles, nor checked by
    /// <see cref="DcmModel.Validate"/>.)
    /// </summary>
    public void RemovePart(int index)
    {
        if (index < 0 || index >= Parts.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0) throw new InvalidOperationException("cannot remove the root part (index 0)");

        var part = Parts[index];
        int triStart = 0, quadStart = 0;
        for (int j = 0; j < index; j++) { triStart += Parts[j].TriangleCount; quadStart += Parts[j].QuadCount; }
        Triangles.RemoveRange(triStart, part.TriangleCount);
        Quads.RemoveRange(quadStart, part.QuadCount);
        Parts.RemoveAt(index);

        foreach (var p in Parts)
        {
            if (p.Parent == index) p.Parent = part.Parent;        // graft orphans onto the removed part's parent
            else if (p.Parent > index) p.Parent = (sbyte)(p.Parent - 1);
        }
    }

    private static void WriteSVectors(byte[] buf, int off, List<SVector> verts)
    {
        for (int i = 0; i < verts.Count; i++)
        {
            int o = off + i * DcmVariant.VertexStride;
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o + 0, 2), verts[i].X);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o + 2, 2), verts[i].Y);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o + 4, 2), verts[i].Z);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o + 6, 2), verts[i].Pad);
        }
    }

    private static void WriteTriangle(byte[] b, int o, DcmTriangle t, DcmVariant v)
    {
        void W16(int x, ushort val) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(x, 2), val);
        W16(o + 0, t.V0); W16(o + 2, t.V1); W16(o + 4, t.V2);
        if (v.PerPolyTexture)
        {
            b[o + 6] = t.Uv0.U; b[o + 7] = t.Uv0.V;
            b[o + 8] = t.Uv1.U; b[o + 9] = t.Uv1.V;
            W16(o + 0x0a, t.Clut);
            b[o + 0x0c] = t.Uv2.U; b[o + 0x0d] = t.Uv2.V;
            W16(o + 0x0e, t.TPage);
        }
        else
        {
            b[o + 6] = t.Uv0.U; b[o + 7] = t.Uv0.V;
            b[o + 8] = t.Uv1.U; b[o + 9] = t.Uv1.V;
            b[o + 10] = t.Uv2.U; b[o + 11] = t.Uv2.V;
        }
    }

    private static void WriteQuad(byte[] b, int o, DcmQuad q, DcmVariant v)
    {
        void W16(int x, ushort val) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(x, 2), val);
        W16(o + 0, q.V0); W16(o + 2, q.V1); W16(o + 4, q.V2); W16(o + 6, q.V3);
        if (v.PerPolyTexture)
        {
            b[o + 8] = q.Uv0.U; b[o + 9] = q.Uv0.V;
            W16(o + 0x0a, q.Clut);
            b[o + 0x0c] = q.Uv1.U; b[o + 0x0d] = q.Uv1.V;
            W16(o + 0x0e, q.TPage);
            b[o + 0x10] = q.Uv2.U; b[o + 0x11] = q.Uv2.V;
            b[o + 0x12] = q.Uv3.U; b[o + 0x13] = q.Uv3.V;
        }
        else
        {
            b[o + 8] = q.Uv0.U; b[o + 9] = q.Uv0.V;
            b[o + 10] = q.Uv1.U; b[o + 11] = q.Uv1.V;
            b[o + 12] = q.Uv2.U; b[o + 13] = q.Uv2.V;
            b[o + 14] = q.Uv3.U; b[o + 15] = q.Uv3.V;
        }
    }

    private static DcmHeader CloneHeader(DcmHeader h) => new()
    {
        VertexPtr = h.VertexPtr, NormalPtr = h.NormalPtr, TrianglePtr = h.TrianglePtr, QuadPtr = h.QuadPtr,
        TriangleCount = h.TriangleCount, QuadCount = h.QuadCount, PartCount = h.PartCount,
        TPage = h.TPage, Clut = h.Clut,
    };

    private static DcmPart ClonePart(DcmPart p) => new()
    {
        X = p.X, Y = p.Y, Z = p.Z, Parent = p.Parent, Child = p.Child,
        TrianglePtr = p.TrianglePtr, QuadPtr = p.QuadPtr, TriangleCount = p.TriangleCount, QuadCount = p.QuadCount,
    };

    private static DcmTriangle CloneTriangle(DcmTriangle t) => new()
    {
        V0 = t.V0, V1 = t.V1, V2 = t.V2, Uv0 = t.Uv0, Uv1 = t.Uv1, Uv2 = t.Uv2, Clut = t.Clut, TPage = t.TPage,
    };

    private static DcmQuad CloneQuad(DcmQuad q) => new()
    {
        V0 = q.V0, V1 = q.V1, V2 = q.V2, V3 = q.V3,
        Uv0 = q.Uv0, Uv1 = q.Uv1, Uv2 = q.Uv2, Uv3 = q.Uv3, Clut = q.Clut, TPage = q.TPage,
    };
}
