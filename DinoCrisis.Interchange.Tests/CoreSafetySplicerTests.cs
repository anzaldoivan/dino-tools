using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class CoreSafetySplicerTests
{
    [Theory]
    [InlineData("dc1")]
    [InlineData("dc2")]
    public void Identity_with_zero_stock_normal_is_accepted_when_y_flip_is_disabled(string game)
    {
        var variant = game == "dc1" ? DcmVariant.Dc1 : DcmVariant.Dc2;
        byte[] blob = SyntheticBlob.Build(variant);
        var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, variant);
        Assert.Equal((short)0, target.Normals[0].X);
        Assert.Equal((short)0, target.Normals[0].Y);
        Assert.Equal((short)0, target.Normals[0].Z);
        var mesh = GltfImporter.ReadSameRig(
            GltfInterchange.FromDcm(target, flipY: false), target, null, flipY: false);

        if (game == "dc1")
        {
            byte[] package = new GianPackageBuilder().AddData(blob).AddData(new byte[] { 1 }).Build();
            Dc1ModelSplicer.Splice(package, _ => true, mesh);
        }
        else
        {
            byte[] package = new GianPackageBuilder().AddDc2Model(blob, SyntheticBlob.Base).BuildDc2();
            Dc2ModelSplicer.Splice(package, SyntheticBlob.Base, mesh);
        }
    }

    [Fact]
    public void Dc1_refuses_ambiguous_coincident_stock_vertices_after_normal_unitization()
    {
        var builder = DcmModel.Read(
            SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1).ToBuilder();
        builder.Vertices[1] = builder.Vertices[0] with { Pad = builder.Vertices[1].Pad };
        builder.Normals[0] = new SVector(-2, 4, 6, 0);
        builder.Normals[1] = new SVector(-1, 2, 3, 0);
        builder.Triangles[0].V0 = 0;
        builder.Triangles[0].V1 = 2;
        builder.Triangles[0].V2 = 3;
        builder.Triangles[1].V0 = 1;
        builder.Triangles[1].V1 = 2;
        builder.Triangles[1].V2 = 3;
        builder.Parts[0].TriangleCount = 2;
        builder.Parts[1].TriangleCount = 0;
        builder.Parts[1].TrianglePtr = builder.Header.QuadPtr;
        builder.Header.QuadCount = 0;
        builder.Parts[0].QuadCount = 0;
        builder.Parts[1].QuadPtr = builder.Header.QuadPtr;
        byte[] blob = builder.Write();
        byte[] package = new GianPackageBuilder().AddData(blob).AddData(new byte[] { 1 }).Build();
        var target = Assert.Single(DcmLocator.FromPackage(package));
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        foreach (var vertex in mesh.Vertices) vertex.StockIndex = -1; // exercise legacy metadata-free fallback

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc1ModelSplicer.Splice(package, _ => true, mesh));

        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dc1_refuses_a_moved_or_new_vertex()
    {
        byte[] package = new GianPackageBuilder()
            .AddData(SyntheticBlob.Build(DcmVariant.Dc1))
            .AddData(new byte[] { 1 })
            .Build();
        var target = Assert.Single(DcmLocator.FromPackage(package));
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        mesh.Vertices[mesh.Triangles[0].V0].X++;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc1ModelSplicer.Splice(package, m => m.ModelHead == target.ModelHead, mesh));

        Assert.Contains("moved/new", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dc2_refuses_moved_vertices_by_default_but_unsafe_opt_in_warns()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc2);
        byte[] package = new GianPackageBuilder()
            .AddDc2Model(blob, SyntheticBlob.Base)
            .BuildDc2();
        var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc2);
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        mesh.Vertices[mesh.Triangles[0].V0].X++;
        mesh.Quads.Clear(); // free one native quad record so the unsafe witness still fits in place

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc2ModelSplicer.Splice(package, SyntheticBlob.Base, mesh));
        Assert.Contains("moved/new", ex.Message, StringComparison.OrdinalIgnoreCase);

        var result = Dc2ModelSplicer.Splice(
            package, SyntheticBlob.Base, mesh, allowUnsafeMovedVertices: true);
        Assert.Contains("WARNING", result.Report, StringComparison.Ordinal);
        Assert.Contains("moved/new", result.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside proven transform-run coverage", result.Report,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("descriptor +7 remains unchanged", result.Report,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dc2_refuses_polygon_texture_page_not_representable_by_its_header()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc2);
        byte[] package = new GianPackageBuilder()
            .AddDc2Model(blob, SyntheticBlob.Base)
            .BuildDc2();
        var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc2);
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        mesh.Triangles[0].TPage = (ushort)(target.Header.TPage ^ 1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc2ModelSplicer.Splice(package, SyntheticBlob.Base, mesh));

        Assert.Contains("texture page", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dc2_does_not_treat_zero_polygon_texture_page_as_a_wildcard()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc2);
        byte[] package = new GianPackageBuilder()
            .AddDc2Model(blob, SyntheticBlob.Base)
            .BuildDc2();
        var target = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc2);
        Assert.NotEqual((ushort)0, target.Header.TPage);
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);
        mesh.Triangles[0].TPage = 0;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc2ModelSplicer.Splice(package, SyntheticBlob.Base, mesh));

        Assert.Contains("texture page", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dc1_identity_preserves_coincident_stock_vertices_with_distinct_normals()
    {
        var builder = DcmModel.Read(
            SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1).ToBuilder();
        builder.Vertices[1] = builder.Vertices[0] with { Pad = builder.Vertices[1].Pad };
        // Keep both coincident records referenced without making either triangle degenerate.
        builder.Triangles[0].V0 = 0;
        builder.Triangles[0].V1 = 2;
        builder.Triangles[0].V2 = 3;
        builder.Triangles[1].V0 = 1;
        builder.Triangles[1].V1 = 2;
        builder.Triangles[1].V2 = 3;
        builder.Header.QuadCount = 0;
        builder.Parts[0].QuadCount = 0;
        builder.Parts[1].QuadPtr = builder.Header.QuadPtr;
        byte[] blob = builder.Write();
        byte[] package = new GianPackageBuilder()
            .AddData(blob)
            .AddData(new byte[] { 1 })
            .Build();
        var target = Assert.Single(DcmLocator.FromPackage(package));
        Assert.NotEqual(target.Normals[0], target.Normals[1]);
        var mesh = GltfImporter.ReadSameRig(GltfInterchange.FromDcm(target), target, null);

        var result = Dc1ModelSplicer.Splice(package, _ => true, mesh);
        var reread = Assert.Single(DcmLocator.FromPackage(result.Package));

        var expectedTriangles = target.Triangles.Select(t => (t.V0, t.V1, t.V2)).ToArray();
        var actualTriangles = reread.Triangles.Select(t => (t.V0, t.V1, t.V2)).ToArray();
        Assert.True(expectedTriangles.SequenceEqual(actualTriangles),
            $"mesh vertices: {string.Join("; ", mesh.Vertices.Select((v, i) => $"{i}=({v.X},{v.Y},{v.Z}) n=({v.NX},{v.NY},{v.NZ}) j={v.Joint}"))}; " +
            $"expected: {string.Join(", ", expectedTriangles)}; actual: {string.Join(", ", actualTriangles)}");
        Assert.Equal(
            target.Quads.Select(q => (q.V0, q.V1, q.V2, q.V3)),
            reread.Quads.Select(q => (q.V0, q.V1, q.V2, q.V3)));
    }
}
