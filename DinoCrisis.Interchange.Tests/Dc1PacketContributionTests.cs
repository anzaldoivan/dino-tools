using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Dc1PacketContributionTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 40)]
    [InlineData(0, 1, 52)]
    [InlineData(2, 3, 236)]
    [InlineData(314, 141, 19_892)]
    [InlineData(3_269, 6, 131_072)]
    [InlineData(4_000, 0, 160_000)]
    public void Calculate_uses_native_gt3_and_gt4_packet_sizes(
        int triangles, int quads, int expectedBytes)
    {
        var contribution = Dc1PacketContribution.Calculate(triangles, quads);

        Assert.Equal(expectedBytes, contribution.Bytes);
        Assert.Equal(expectedBytes * 100d / 131_072, contribution.ArenaPercentage, 8);
    }

    [Fact]
    public void Format_labels_exact_arena_boundary_as_contribution_not_headroom()
    {
        string diagnostic = Dc1PacketContribution.Format(triangles: 3_269, quads: 6);

        Assert.Contains("131,072/131,072 B (100.0%)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("model contribution, not whole-frame headroom", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_reports_above_arena_arithmetic_without_claiming_runtime_overflow()
    {
        string diagnostic = Dc1PacketContribution.Format(triangles: 4_000, quads: 0);

        Assert.Contains("160,000/131,072 B (122.1%)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("model contribution, not whole-frame headroom", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Splice_report_includes_packet_contribution_and_maximum_raw_index_without_mutating_input()
    {
        byte[] package = new GianPackageBuilder()
            .AddData(SyntheticBlob.Build(DcmVariant.Dc1))
            .AddData(new byte[] { 1 })
            .Build();
        byte[] before = package.ToArray();
        var target = Assert.Single(DcmLocator.FromPackage(package));
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        int expectedMax = target.Triangles.SelectMany(t => new[] { t.V0, t.V1, t.V2 })
            .Concat(target.Quads.SelectMany(q => new[] { q.V0, q.V1, q.V2, q.V3 }))
            .Max(index => (int)index);

        var result = Dc1ModelSplicer.Splice(package, _ => true, mesh);

        Assert.Equal(before, package);
        Assert.Contains(Dc1PacketContribution.Format(result.Triangles, result.Quads), result.Report,
            StringComparison.Ordinal);
        Assert.Contains($"maximum referenced raw vertex index: {expectedMax}", result.Report,
            StringComparison.Ordinal);
    }
}
