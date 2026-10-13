using System.Buffers.Binary;
using DinoCrisis.Model;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class Dc1TransformRunValidatorTests
{
    [Fact]
    public void ExactMultiPartCoverage_ProducesConsecutiveRuns()
    {
        var model = BuildModel([1, 2, 1], positionCount: 12, normalCount: 12,
            triangle: [0, 5, 11]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.True(result.IsLegal, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(12, result.PoolRecordCount);
        Assert.Equal(12, result.CoveredRecordCount);
        Assert.Collection(result.Runs,
            run => AssertRun(run, partIndex: 0, start: 0, count: 3),
            run => AssertRun(run, partIndex: 1, start: 3, count: 6),
            run => AssertRun(run, partIndex: 2, start: 9, count: 3));
    }

    [Fact]
    public void UnderCoverage_IsRejectedEvenBelowLiftedTableAndWithPacketHeadroom()
    {
        // Nine records are far below the shipped 1,024-entry table, and one triangle consumes
        // negligible packet space. Neither diagnostic can legalize only six transformed records.
        var model = BuildModel([1, 1], positionCount: 9, normalCount: 9,
            triangle: [0, 1, 8]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: under-coverage: part runs cover 6 records but parallel pools contain 9",
            result.Error);
    }

    [Fact]
    public void OverCoverage_IsRejected()
    {
        var model = BuildModel([2, 2], positionCount: 9, normalCount: 9,
            triangle: [0, 1, 8]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: over-coverage: part runs cover 12 records but parallel pools contain 9",
            result.Error);
    }

    [Fact]
    public void NonparallelPositionAndNormalPools_AreRejected()
    {
        var model = BuildModel([2], positionCount: 6, normalCount: 3,
            triangle: [0, 1, 2]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: position pool has 6 records but normal pool has 3",
            result.Error);
    }

    [Fact]
    public void ReferencedDeclaredPadding_IsRejected()
    {
        var model = BuildModel([2], positionCount: 6, normalCount: 6,
            triangle: [0, 4, 5]);

        var result = Dc1TransformRunValidator.Validate(model, declaredPaddingIndices: [4]);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: declared padding record 4 is referenced by triangle 0",
            result.Error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void OutOfRangeDeclaredPadding_IsRejected(int paddingIndex)
    {
        var model = BuildModel([2], positionCount: 6, normalCount: 6,
            triangle: [0, 1, 5]);

        var result = Dc1TransformRunValidator.Validate(model, declaredPaddingIndices: [paddingIndex]);

        Assert.False(result.IsLegal);
        Assert.Equal(
            $"DC1 transform runs: declared padding record {paddingIndex} is outside the pool range [0,6)",
            result.Error);
    }

    [Fact]
    public void MaximumSupportedPartCount_IsAccepted()
    {
        var triplets = Enumerable.Repeat((byte)1, Dc1TransformRunValidator.MaximumPartCount).ToArray();
        int records = triplets.Length * 3;
        var model = BuildModel(triplets, records, records,
            triangle: [0, (ushort)(records - 2), (ushort)(records - 1)]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.True(result.IsLegal, result.Error);
        Assert.Equal(Dc1TransformRunValidator.MaximumPartCount, result.Runs.Count);
    }

    [Fact]
    public void MoreThanSupportedPartCount_IsRejected()
    {
        var triplets = Enumerable.Repeat((byte)1, 34).ToArray();
        var model = BuildModel(triplets, positionCount: 102, normalCount: 102,
            triangle: [0, 100, 101]);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal("DC1 transform runs: part count 34 is outside the supported range 1..33", result.Error);
    }

    [Fact]
    public void HeaderAndParsedPartCountsMustAgree()
    {
        var model = BuildModel([1], positionCount: 3, normalCount: 3,
            triangle: [0, 1, 2]);
        model.Header.PartCount = 2;

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: header declares 2 parts but the parsed model contains 1",
            result.Error);
    }

    [Fact]
    public void LastCoveredIndex_IsAccepted_AndFirstIndexBeyondCoverageIsRejected()
    {
        var model = BuildModel([2], positionCount: 6, normalCount: 6,
            triangle: [0, 1, 5]);
        Assert.True(Dc1TransformRunValidator.Validate(model).IsLegal);

        model.Triangles[0].V2 = 6;
        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal(
            "DC1 transform runs: triangle 0 references position/normal index 6 outside defined runs [0,6)",
            result.Error);
    }

    [Fact]
    public void QuadReferencesAreValidated_AndDeclaredPaddingMayRemainUnreferenced()
    {
        var model = BuildModel([1, 1], positionCount: 6, normalCount: 6,
            triangle: [0, 1, 2], quad: [0, 1, 2, 4]);

        var legal = Dc1TransformRunValidator.Validate(model, declaredPaddingIndices: [3, 5]);
        Assert.True(legal.IsLegal, legal.Error);

        model.Quads[0].V3 = 5;
        var referencedPadding = Dc1TransformRunValidator.Validate(model, declaredPaddingIndices: [3, 5]);
        Assert.False(referencedPadding.IsLegal);
        Assert.Equal(
            "DC1 transform runs: declared padding record 5 is referenced by quad 0",
            referencedPadding.Error);
    }

    [Fact]
    public void Dc2Model_IsRejectedWithoutInterpretingChildAsTripletCount()
    {
        var model = BuildModel([1, 1], positionCount: 6, normalCount: 6,
            triangle: [0, 1, 5], variant: DcmVariant.Dc2);

        var result = Dc1TransformRunValidator.Validate(model);

        Assert.False(result.IsLegal);
        Assert.Equal("DC1 transform runs: validator requires a DC1 model", result.Error);
    }

    private static void AssertRun(Dc1TransformRun run, int partIndex, int start, int count)
    {
        Assert.Equal(partIndex, run.PartIndex);
        Assert.Equal(start, run.StartRecord);
        Assert.Equal(count, run.RecordCount);
        Assert.Equal(start + count, run.EndRecordExclusive);
        Assert.Equal(count / 3, run.TripletCount);
    }

    private static DcmModel BuildModel(
        IReadOnlyList<byte> tripletCounts,
        int positionCount,
        int normalCount,
        ushort[] triangle,
        ushort[]? quad = null,
        DcmVariant? variant = null)
    {
        variant ??= DcmVariant.Dc1;
        int partCount = tripletCounts.Count;
        int triangleCount = 1;
        int quadCount = quad is null ? 0 : 1;
        int partsOffset = variant.HeaderSize;
        int positionsOffset = partsOffset + partCount * DcmVariant.PartStride;
        int normalsOffset = positionsOffset + positionCount * DcmVariant.VertexStride;
        int trianglesOffset = normalsOffset + normalCount * DcmVariant.VertexStride;
        int quadsOffset = trianglesOffset + triangleCount * variant.TriangleStride;
        int end = quadsOffset + quadCount * variant.QuadStride;
        var blob = new byte[end];

        void WriteU16(int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(offset, 2), value);
        void WriteI16(int offset, short value) =>
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(offset, 2), value);
        void WriteU32(int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset, 4), value);
        uint Address(int offset) => SyntheticBlob.Base + (uint)offset;

        WriteU32(0, Address(positionsOffset));
        WriteU32(4, Address(normalsOffset));
        WriteU32(8, Address(trianglesOffset));
        WriteU32(0x0c, Address(quadsOffset));
        WriteU16(0x10, (ushort)triangleCount);
        WriteU16(0x12, (ushort)quadCount);
        WriteU32(0x14, (uint)partCount);

        for (int i = 0; i < partCount; i++)
        {
            int partOffset = partsOffset + i * DcmVariant.PartStride;
            blob[partOffset + 6] = i == 0 ? unchecked((byte)-1) : (byte)0;
            blob[partOffset + 7] = tripletCounts[i];
            WriteU32(partOffset + 8, Address(trianglesOffset + (i == 0 ? 0 : variant.TriangleStride)));
            WriteU32(partOffset + 0x0c, Address(quadsOffset + (i == 0 ? 0 : quadCount * variant.QuadStride)));
            WriteU16(partOffset + 0x10, (ushort)(i == 0 ? 1 : 0));
            WriteU16(partOffset + 0x12, (ushort)(i == 0 ? quadCount : 0));
        }

        for (int i = 0; i < positionCount; i++)
        {
            int offset = positionsOffset + i * DcmVariant.VertexStride;
            WriteI16(offset, (short)i);
            WriteI16(offset + 2, (short)(i + 1));
            WriteI16(offset + 4, (short)(i + 2));
        }
        for (int i = 0; i < normalCount; i++)
        {
            int offset = normalsOffset + i * DcmVariant.VertexStride;
            WriteI16(offset + 2, 4096);
        }

        WriteU16(trianglesOffset, triangle[0]);
        WriteU16(trianglesOffset + 2, triangle[1]);
        WriteU16(trianglesOffset + 4, triangle[2]);
        if (quad is not null)
        {
            WriteU16(quadsOffset, quad[0]);
            WriteU16(quadsOffset + 2, quad[1]);
            WriteU16(quadsOffset + 4, quad[2]);
            WriteU16(quadsOffset + 6, quad[3]);
        }

        return DcmModel.Read(blob, 0, SyntheticBlob.Base, variant);
    }
}
