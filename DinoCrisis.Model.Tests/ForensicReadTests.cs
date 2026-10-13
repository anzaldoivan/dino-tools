using System.Buffers.Binary;
using DinoCrisis.Model;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class ForensicReadTests
{
    [Fact]
    public void Explicit_parallel_pool_provenance_reads_a_known_discontinuous_layout()
    {
        var fixture = Discontinuous();

        var strictError = Assert.Throws<InvalidDataException>(() =>
            DcmModel.Read(fixture.Blob, 0, SyntheticBlob.Base, DcmVariant.Dc1));
        Assert.Contains("pool pointer alignment", strictError.Message);

        var model = DcmModel.ReadForensicDc1(
            fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance);

        Assert.Equal(4, model.Vertices.Count);
        Assert.Equal(4, model.Normals.Count);
        Assert.Equal(DcmPoolCountProvenance.ParallelPoolsWithKnownSections,
            model.PoolCountProvenance);
        Assert.Equal(2, model.InterveningSections.Count);
        Assert.True(DcmModel.Validate(model));
    }

    [Fact]
    public void Forensic_read_does_not_mutate_input()
    {
        var fixture = Discontinuous();
        byte[] before = (byte[])fixture.Blob.Clone();

        _ = DcmModel.ReadForensicDc1(
            fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance);

        Assert.Equal(before, fixture.Blob);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Forensic_read_rejects_misaligned_pool_pointers(int delta)
    {
        var fixture = Discontinuous();
        uint normal = U32(fixture.Blob, 4);
        W32(fixture.Blob, 4, normal + (uint)delta);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance));

        Assert.Contains("alignment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Forensic_read_rejects_gap_coverage_that_is_too_short_or_too_long(int delta)
    {
        var fixture = Discontinuous();
        var sections = new[]
        {
            DcmKnownSection.ZeroFill(fixture.GapStart, 4),
            DcmKnownSection.PreservedTemplate(
                fixture.GapStart + 4,
                fixture.TemplateBytes.Concat(new byte[Math.Max(delta, 0)]).ToArray()),
        };
        if (delta < 0)
            sections[1] = DcmKnownSection.PreservedTemplate(
                fixture.GapStart + 4, fixture.TemplateBytes[..^1]);
        var provenance = new DcmForensicLayout(4, sections);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, provenance));

        Assert.Contains("complete gap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_contradictory_parallel_normal_count()
    {
        var fixture = Discontinuous();
        var provenance = new DcmForensicLayout(3, fixture.Provenance.Sections);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, provenance));

        Assert.Contains("parallel", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_template_bytes_that_do_not_match()
    {
        var fixture = Discontinuous();
        byte[] wrong = (byte[])fixture.TemplateBytes.Clone();
        wrong[0] ^= 0x7f;
        var provenance = new DcmForensicLayout(4, new[]
        {
            DcmKnownSection.ZeroFill(fixture.GapStart, 4),
            DcmKnownSection.PreservedTemplate(fixture.GapStart + 4, wrong),
        });

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, provenance));

        Assert.Contains("template", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_nonzero_bytes_claimed_as_zero_fill()
    {
        var fixture = Discontinuous();
        fixture.Blob[fixture.GapStart] = 1;

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance));

        Assert.Contains("zero-fill", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_triangle_quad_overlap()
    {
        var fixture = Discontinuous();
        W32(fixture.Blob, 0x0c, U32(fixture.Blob, 0x0c) - 4);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance));

        Assert.Contains("triangle pool span", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_out_of_range_primitive_index()
    {
        var fixture = Discontinuous();
        int triangleOffset = checked((int)(U32(fixture.Blob, 8) - SyntheticBlob.Base));
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Blob.AsSpan(triangleOffset, 2), 4);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                fixture.Blob, 0, SyntheticBlob.Base, fixture.Provenance));

        Assert.Contains("vertex index", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_truncated_quad_tail()
    {
        var fixture = Discontinuous();
        byte[] truncated = fixture.Blob[..^1];

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(
                truncated, 0, SyntheticBlob.Base, fixture.Provenance));

        Assert.Contains("quad pool out of range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forensic_read_rejects_pointer_arithmetic_outside_int_range()
    {
        var fixture = Discontinuous();
        const uint highBase = 0x10;
        W32(fixture.Blob, 0, 0xffff_ff00);
        W32(fixture.Blob, 4, 0xffff_ff20);
        W32(fixture.Blob, 8, 0xffff_ff40);
        W32(fixture.Blob, 0x0c, 0xffff_ff60);

        var error = Assert.Throws<InvalidDataException>(() =>
            DcmModel.ReadForensicDc1(fixture.Blob, 0, highBase,
                new DcmForensicLayout(4, Array.Empty<DcmKnownSection>())));

        Assert.Contains("range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_contiguous_read_and_byte_exact_round_trip_are_unchanged()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);

        var model = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1);

        Assert.Equal(DcmPoolCountProvenance.ContiguousPointerSpans,
            model.PoolCountProvenance);
        Assert.Empty(model.InterveningSections);
        Assert.Equal(blob, model.ToBuilder().Write());
    }

    private static Fixture Discontinuous()
    {
        byte[] original = SyntheticBlob.Build(DcmVariant.Dc1);
        int oldTriangle = checked((int)(U32(original, 8) - SyntheticBlob.Base));
        const int gapLength = 12;
        var blob = new byte[original.Length + gapLength];
        Array.Copy(original, 0, blob, 0, oldTriangle);
        Array.Copy(original, oldTriangle, blob, oldTriangle + gapLength,
            original.Length - oldTriangle);

        byte[] template = Enumerable.Range(0, 8).Select(i => (byte)(0xa0 + i)).ToArray();
        template.CopyTo(blob, oldTriangle + 4);

        ShiftPointer(blob, 8, gapLength);
        ShiftPointer(blob, 0x0c, gapLength);
        for (int part = 0; part < 2; part++)
        {
            int descriptor = 0x18 + part * DcmVariant.PartStride;
            ShiftPointer(blob, descriptor + 8, gapLength);
            ShiftPointer(blob, descriptor + 0x0c, gapLength);
        }

        var provenance = new DcmForensicLayout(4, new[]
        {
            DcmKnownSection.ZeroFill(oldTriangle, 4),
            DcmKnownSection.PreservedTemplate(oldTriangle + 4, template),
        });
        return new Fixture(blob, oldTriangle, template, provenance);
    }

    private static void ShiftPointer(byte[] blob, int offset, int delta) =>
        W32(blob, offset, checked(U32(blob, offset) + (uint)delta));

    private static uint U32(byte[] blob, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset, 4));

    private static void W32(byte[] blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset, 4), value);

    private sealed record Fixture(
        byte[] Blob,
        int GapStart,
        byte[] TemplateBytes,
        DcmForensicLayout Provenance);
}
