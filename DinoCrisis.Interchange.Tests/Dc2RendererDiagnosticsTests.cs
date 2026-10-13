using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Dc2RendererDiagnosticsTests
{
    [Fact]
    public void Transform_coverage_accepts_exact_declared_triplets()
    {
        var result = Dc2RendererDiagnostics.ValidateTransformCoverage(6, new byte[] { 1, 1 });

        Assert.Equal(2, result.TransformTriplets);
        Assert.Equal(6, result.CoveredVertices);
    }

    [Theory]
    [InlineData(9, new byte[] { 1, 1 }, "too few")]
    [InlineData(3, new byte[] { 1, 1 }, "too many")]
    public void Transform_coverage_rejects_declared_triplet_mismatch(
        int vertexCount, byte[] triplets, string expectedDirection)
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Dc2RendererDiagnostics.ValidateTransformCoverage(vertexCount, triplets));

        Assert.Contains(expectedDirection, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transform_coverage_rejects_non_multiple_of_three_vertex_count()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Dc2RendererDiagnostics.ValidateTransformCoverage(4, new byte[] { 1 }));

        Assert.Contains("multiple of three", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transform_coverage_handles_maximum_descriptor_byte_without_narrow_arithmetic()
    {
        var result = Dc2RendererDiagnostics.ValidateTransformCoverage(765, new byte[] { byte.MaxValue });

        Assert.Equal(255, result.TransformTriplets);
        Assert.Equal(765, result.CoveredVertices);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 64)]
    [InlineData(0, 1, 84)]
    [InlineData(2, 3, 380)]
    public void Packet_contribution_uses_native_gt3_and_gt4_sizes(
        int triangles, int quads, int expectedBytes)
    {
        Assert.Equal(expectedBytes,
            Dc2RendererDiagnostics.CalculatePacketContribution(triangles, quads));
    }

    [Fact]
    public void Packet_contribution_uses_checked_arithmetic()
    {
        Assert.Throws<OverflowException>(() =>
            Dc2RendererDiagnostics.CalculatePacketContribution(int.MaxValue, 0));
    }

    [Fact]
    public void Format_labels_packet_bytes_as_advisory_model_contribution()
    {
        string diagnostic = Dc2RendererDiagnostics.FormatPacketContribution(2, 3);

        Assert.Contains("380 B", diagnostic, StringComparison.Ordinal);
        Assert.Contains("advisory", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model contribution, not whole-frame headroom", diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Splice_reports_coverage_and_packets_without_mutating_input_or_enforcing_actor_ceiling()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc2);
        byte[] package = new GianPackageBuilder()
            .AddDc2Model(blob, SyntheticBlob.Base)
            .BuildDc2();
        byte[] before = package.ToArray();
        var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc2);
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);

        var result = Dc2ModelSplicer.Splice(package, SyntheticBlob.Base, mesh);

        Assert.Equal(before, package);
        Assert.Contains("transform triplets", result.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Dc2RendererDiagnostics.FormatPacketContribution(
                result.Triangles, result.Quads),
            result.Report, StringComparison.Ordinal);
        Assert.Contains("actor-path ceiling: advisory only", result.Report,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renderer ceiling", result.Report, StringComparison.OrdinalIgnoreCase);
    }
}
