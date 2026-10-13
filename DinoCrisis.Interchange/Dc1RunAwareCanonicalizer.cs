using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;

namespace DinoCrisis.Interchange;

public sealed record Dc1RunAwareCanonicalResult(
    byte[] DecodedBytes,
    DcmModel Model,
    IReadOnlyList<int> OldToNewIndices,
    IReadOnlyList<int> PaddingIndices);

/// <summary>Explicit offline DC1 cross-rig canonical repack; not used by the same-rig importer.</summary>
public static class Dc1RunAwareCanonicalizer
{
    public static Dc1RunAwareCanonicalResult Repack(
        DcmModel source, DcmModel target, IReadOnlyList<int> visibleRecordsPerPart,
        int maximumRecords, uint maximumDecodedEndAddress)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(visibleRecordsPerPart);
        Require(source.Variant.Game == DcmGame.Dc1 && target.Variant.Game == DcmGame.Dc1, "DC1 models are required");
        Require(source.Vertices.Count == source.Normals.Count, "source pools are not parallel");
        Require(source.Parts.Count == target.Parts.Count && visibleRecordsPerPart.Count == target.Parts.Count,
            "source, target, and ownership part counts must match");
        Require(target.Parts.Count is > 0 and <= Dc1TransformRunValidator.MaximumPartCount, "target part count is outside 1..33");
        Require(source.Parts.Sum(p => p.TriangleCount) == source.Triangles.Count &&
                source.Parts.Sum(p => p.QuadCount) == source.Quads.Count, "source primitive partitions are inconsistent");
        Require(visibleRecordsPerPart.All(n => n >= 0) && visibleRecordsPerPart.Sum() == source.Vertices.Count,
            "visible ownership must partition every source record exactly once");

        var map = new int[source.Vertices.Count];
        var padding = new List<int>();
        var positions = new List<SVector>();
        var normals = new List<SVector>();
        int oldCursor = 0;
        var triplets = new byte[target.Parts.Count];
        for (int part = 0; part < target.Parts.Count; part++)
        {
            int visible = visibleRecordsPerPart[part];
            int stored = checked((visible + 2) / 3 * 3);
            int partTriplets = stored / 3;
            Require(partTriplets <= byte.MaxValue, $"part {part} exceeds 255 RTPT triplets");
            triplets[part] = (byte)partTriplets;
            for (int i = 0; i < visible; i++, oldCursor++)
            {
                map[oldCursor] = positions.Count;
                positions.Add(source.Vertices[oldCursor]);
                normals.Add(source.Normals[oldCursor]);
            }
            for (int i = visible; i < stored; i++)
            {
                padding.Add(positions.Count);
                positions.Add(default);
                normals.Add(default);
            }
        }
        Require(positions.Count <= maximumRecords, $"canonical pool has {positions.Count} records above limit {maximumRecords}");
        Require(positions.Count <= ushort.MaxValue + 1, "canonical indices exceed ushort capacity");

        ushort M(ushort index)
        {
            Require(index < map.Length, $"source primitive index {index} is outside the visible pool");
            return checked((ushort)map[index]);
        }
        var triangles = source.Triangles.Select(t => new DcmTriangle
        {
            V0 = M(t.V0), V1 = M(t.V1), V2 = M(t.V2), Uv0 = t.Uv0, Uv1 = t.Uv1,
            Uv2 = t.Uv2, Clut = t.Clut, TPage = t.TPage,
        }).ToList();
        var quads = source.Quads.Select(q => new DcmQuad
        {
            V0 = M(q.V0), V1 = M(q.V1), V2 = M(q.V2), V3 = M(q.V3), Uv0 = q.Uv0,
            Uv1 = q.Uv1, Uv2 = q.Uv2, Uv3 = q.Uv3, Clut = q.Clut, TPage = q.TPage,
        }).ToList();

        int vertexOff = target.Raw.Length;
        int normalOff = checked(vertexOff + positions.Count * 8);
        int triangleOff = checked(normalOff + normals.Count * 8);
        int quadOff = checked(triangleOff + triangles.Count * DcmVariant.Dc1.TriangleStride);
        int end = checked(quadOff + quads.Count * DcmVariant.Dc1.QuadStride);
        Require((ulong)target.LoadBase + (uint)end <= maximumDecodedEndAddress, "canonical decoded end exceeds target ceiling");
        var bytes = new byte[end];
        target.Raw.CopyTo(bytes, 0);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o, 2), v);
        void WI16(int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(o, 2), v);
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o, 4), v);
        uint A(int o) => checked(target.LoadBase + (uint)o);
        W32(target.ModelHead, A(vertexOff)); W32(target.ModelHead + 4, A(normalOff));
        W32(target.ModelHead + 8, A(triangleOff)); W32(target.ModelHead + 12, A(quadOff));
        W16(target.ModelHead + 0x10, checked((ushort)triangles.Count));
        W16(target.ModelHead + 0x12, checked((ushort)quads.Count));

        uint triCursor = A(triangleOff), quadCursor = A(quadOff);
        for (int i = 0; i < target.Parts.Count; i++)
        {
            int o = target.ModelHead + target.Variant.MapOffset + i * DcmVariant.PartStride;
            bytes[o + 7] = triplets[i];
            W32(o + 8, triCursor); W32(o + 12, quadCursor);
            W16(o + 0x10, checked((ushort)source.Parts[i].TriangleCount));
            W16(o + 0x12, checked((ushort)source.Parts[i].QuadCount));
            triCursor += checked((uint)(source.Parts[i].TriangleCount * 16));
            quadCursor += checked((uint)(source.Parts[i].QuadCount * 20));
        }
        WriteVectors(positions, vertexOff); WriteVectors(normals, normalOff);
        for (int i = 0; i < triangles.Count; i++) WriteTriangle(triangles[i], triangleOff + i * 16);
        for (int i = 0; i < quads.Count; i++) WriteQuad(quads[i], quadOff + i * 20);

        var model = DcmModel.Read(bytes, target.ModelHead, target.LoadBase, DcmVariant.Dc1);
        var legality = Dc1TransformRunValidator.Validate(model, padding);
        Require(legality.IsLegal, legality.Error ?? "canonical run validation failed");
        return new(bytes, model, Array.AsReadOnly(map), padding.AsReadOnly());

        void WriteVectors(List<SVector> records, int offset)
        {
            for (int i = 0; i < records.Count; i++)
            { int o = offset + i * 8; WI16(o, records[i].X); WI16(o + 2, records[i].Y); WI16(o + 4, records[i].Z); WI16(o + 6, records[i].Pad); }
        }
        void WriteTriangle(DcmTriangle t, int o)
        {
            W16(o, t.V0); W16(o + 2, t.V1); W16(o + 4, t.V2); bytes[o + 6] = t.Uv0.U; bytes[o + 7] = t.Uv0.V;
            bytes[o + 8] = t.Uv1.U; bytes[o + 9] = t.Uv1.V; W16(o + 10, t.Clut); bytes[o + 12] = t.Uv2.U; bytes[o + 13] = t.Uv2.V; W16(o + 14, t.TPage);
        }
        void WriteQuad(DcmQuad q, int o)
        {
            W16(o, q.V0); W16(o + 2, q.V1); W16(o + 4, q.V2); W16(o + 6, q.V3); bytes[o + 8] = q.Uv0.U; bytes[o + 9] = q.Uv0.V;
            W16(o + 10, q.Clut); bytes[o + 12] = q.Uv1.U; bytes[o + 13] = q.Uv1.V; W16(o + 14, q.TPage);
            bytes[o + 16] = q.Uv2.U; bytes[o + 17] = q.Uv2.V; bytes[o + 18] = q.Uv3.U; bytes[o + 19] = q.Uv3.V;
        }
    }

    public static byte[] RebuildPackage(byte[] packageBytes, int modelEntryIndex, byte[] canonicalDecodedBytes)
    {
        var package = GianPackage.TryParse(packageBytes) ?? throw new InvalidDataException("DC1 GIAN package is invalid");
        Require(!package.IsDc2 && modelEntryIndex >= 0 && modelEntryIndex < package.Entries.Count, "DC1 model entry index is invalid");
        var builder = new GianPackageBuilder();
        for (int i = 0; i < package.Entries.Count; i++)
        {
            var e = package.Entries[i];
            byte[] payload = i == modelEntryIndex
                ? (e.IsCompressed ? Lzss.Compress(canonicalDecodedBytes) : (byte[])canonicalDecodedBytes.Clone())
                : packageBytes.AsSpan(e.PayloadOffset, checked((int)e.DeclaredSize)).ToArray();
            builder.Add(e.Type, payload, e.GfxX, e.GfxY, e.GfxW, e.GfxH);
        }
        return builder.Build();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("DC1 canonical repack: " + message); }
}
