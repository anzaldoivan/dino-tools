using System.Buffers.Binary;
using DinoCrisis.Model;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class CoreSafetyModelTests
{
    [Fact]
    public void Read_rejects_pool_pointers_out_of_order_before_deriving_counts()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        uint vertexPtr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), vertexPtr - DcmVariant.VertexStride);

        var error = Assert.Throws<InvalidDataException>(
            () => DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1));

        Assert.Contains("pool pointer order", error.Message);
    }

    [Fact]
    public void Read_rejects_pool_spans_that_are_not_exact_svector_multiples()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        uint normalPtr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), normalPtr + 1);

        var error = Assert.Throws<InvalidDataException>(
            () => DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1));

        Assert.Contains("pool pointer alignment", error.Message);
    }

    [Fact]
    public void Read_rejects_gap_between_triangle_and_quad_pools()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        Array.Resize(ref blob, blob.Length + DcmVariant.VertexStride);
        uint quadPtr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x0c, 4));
        uint shifted = quadPtr + DcmVariant.VertexStride;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x0c, 4), shifted);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x18 + 0x0c, 4), shifted);
        BinaryPrimitives.WriteUInt32LittleEndian(
            blob.AsSpan(0x18 + DcmVariant.PartStride + 0x0c, 4),
            shifted + (uint)DcmVariant.Dc1.QuadStride);

        var error = Assert.Throws<InvalidDataException>(
            () => DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1));

        Assert.Contains("triangle pool span", error.Message);
    }

    [Fact]
    public void Validate_rejects_non_parallel_vertex_and_normal_pools()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        uint normalPtr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), normalPtr + DcmVariant.VertexStride);

        var model = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1);

        Assert.NotEqual(model.Vertices.Count, model.Normals.Count);
        Assert.False(DcmModel.Validate(model));
    }
}
