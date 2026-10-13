using System;
using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Increment 3 of the DcmModelBuilder (docs/dc1/DCM-MODEL-BUILDER-PLAN.md): AddVertex / AddVertices append
/// new geometry to the shared pools — one normal per position, so the pools stay parallel (the Inc-7
/// invariant) — and return the base index a new part's polygons reference. Completes the static builder:
/// a part can now carry NEW geometry, not just reuse existing vertices.
/// </summary>
public sealed class BuilderGeometryTests
{
    private static DcmModelBuilder Synth(DcmGame game)
    {
        var v = DcmVariant.For(game);
        return DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!.ToBuilder();
    }

    private static DcmModel Reread(DcmModelBuilder b)
        => DcmModel.Read(b.Serialize(b.LoadBase), 0, b.LoadBase, b.Variant);

    private static SVector N => new(0, 0, 4096, 0); // a unit +Z normal (PSX ONE)

    [Fact]
    public void AddVertices_AppendsParallelPools_NewPartReferencesThem()
    {
        var b = Synth(DcmGame.Dc1);
        int v0 = b.Vertices.Count, p0 = b.Parts.Count;

        int baseIdx = b.AddVertices(
            new[] { new SVector(100, 0, 0, 0), new SVector(0, 100, 0, 0), new SVector(0, 0, 100, 0) },
            new[] { N, N, N });
        Assert.Equal(v0, baseIdx);

        var tri = new DcmTriangle
        {
            V0 = (ushort)baseIdx, V1 = (ushort)(baseIdx + 1), V2 = (ushort)(baseIdx + 2),
            Uv0 = new Uv(0, 0), Uv1 = new Uv(1, 1), Uv2 = new Uv(2, 2),
        };
        b.AddPart(new DcmPart { Parent = 0 }, new[] { tri }, Array.Empty<DcmQuad>());

        var rt = Reread(b);
        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(v0 + 3, rt.Vertices.Count);
        Assert.Equal(v0 + 3, rt.Normals.Count);   // pools stayed parallel
        Assert.Equal(p0 + 1, rt.Parts.Count);
        Assert.Equal((short)100, rt.Vertices[baseIdx].X);     // the new geometry survived
        Assert.Equal((short)4096, rt.Normals[baseIdx].Z);
    }

    [Fact]
    public void AddVertex_ReturnsConsecutiveIndices()
    {
        var b = Synth(DcmGame.Dc1);
        int i0 = b.AddVertex(new SVector(1, 2, 3, 0), N);
        int i1 = b.AddVertex(new SVector(4, 5, 6, 0), N);
        Assert.Equal(i0 + 1, i1);
        Assert.Equal(b.Vertices.Count, b.Normals.Count);
    }

    [Fact]
    public void AddVertices_MismatchedCounts_Throws()
    {
        var b = Synth(DcmGame.Dc1);
        Assert.Throws<ArgumentException>(() =>
            b.AddVertices(new[] { new SVector(1, 1, 1, 0), new SVector(2, 2, 2, 0) }, new[] { N }));
    }

    [Fact]
    public void Serialize_ThrowsWhenPoolsNotParallel()
    {
        var b = Synth(DcmGame.Dc1);
        b.Vertices.Add(new SVector(1, 2, 3, 0)); // a vertex with no paired normal
        Assert.Throws<InvalidOperationException>(() => b.Serialize(b.LoadBase));
    }

    // ---- gated golden: add real new geometry and prove it exports (normals stay parallel end-to-end) ----

    [Dc1Fact("st605.dat")]
    public void Golden_AddNewGeometryPart_ExportsWithNormals()
    {
        string path = FixtureGate.Dc1File("st605.dat");
        var model = DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(path)))
            .First(m => m.Header.PartCount == 22);

        var b = model.ToBuilder();
        int baseIdx = b.AddVertices(
            new[] { new SVector(50, 0, 0, 0), new SVector(0, 50, 0, 0), new SVector(0, 0, 50, 0) },
            new[] { N, N, N });
        var tri = new DcmTriangle
        {
            V0 = (ushort)baseIdx, V1 = (ushort)(baseIdx + 1), V2 = (ushort)(baseIdx + 2),
            Uv0 = new Uv(0, 0), Uv1 = new Uv(8, 8), Uv2 = new Uv(16, 16),
        };
        b.AddPart(new DcmPart { Parent = 0 }, new[] { tri }, Array.Empty<DcmQuad>());

        var rt = Reread(b);
        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(model.Vertices.Count + 3, rt.Vertices.Count);
        Assert.Equal(model.Normals.Count + 3, rt.Normals.Count);
        Assert.Equal(23, rt.Parts.Count);

        // end-to-end: the exporter indexes Normals[idx] per vertex, so a clean export with vn==v proves
        // the pools are still parallel after adding geometry.
        string obj = ObjExporter.Export(rt);
        int v = obj.Split('\n').Count(l => l.StartsWith("v "));
        int vn = obj.Split('\n').Count(l => l.StartsWith("vn "));
        Assert.Equal(v, vn);
    }
}
