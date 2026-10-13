using DinoCrisis.Model;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 1: the DCM → interchange → MD1 geometry bridge, on the clean-room synthetic
/// DCM blob (no copyrighted data). The synthetic model has: part0 owns tri(0,1,2)+quad(0,1,2,3);
/// part1 owns tri(1,2,3). So part0 uses 4 verts, part1 uses 3.</summary>
public class DcmToMd1Tests
{
    private static DcmModel SyntheticDc1() =>
        DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);

    [Fact]
    public void ToInterchange_preserves_parts_rig_and_localizes_vertices()
    {
        var mesh = DcmInterchange.ToInterchange(SyntheticDc1());

        Assert.Equal(2, mesh.Parts.Count);

        // Part 0: 4 local verts (tri uses 0,1,2; quad uses 0,1,2,3), 1 tri, 1 quad, root parent.
        var p0 = mesh.Parts[0];
        Assert.Equal(4, p0.Positions.Count);
        Assert.Equal(p0.Positions.Count, p0.Normals.Count); // pools stay parallel
        Assert.Single(p0.Triangles);
        Assert.Single(p0.Quads);
        Assert.True(p0.Parent < 0);

        // Part 1: uses verts {1,2,3} → 3 local verts, 1 tri, 0 quads, parented to root.
        var p1 = mesh.Parts[1];
        Assert.Equal(3, p1.Positions.Count);
        Assert.Empty(p1.Quads);
        Assert.Single(p1.Triangles);
        Assert.Equal(0, p1.Parent);
        Assert.Equal(10, p1.RestX); // synthetic part1 rest pose
        Assert.Equal(20, p1.RestY);
        Assert.Equal(30, p1.RestZ);

        // Vertices are bone-LOCAL (carried verbatim from the DCM pool, not FK-baked). The synthetic
        // pool is vertex i = (i*7, i*11, i*13); part1's local index 0 is global vertex 1.
        Assert.Equal((short)7, p1.Positions[0].X);
        Assert.Equal((short)11, p1.Positions[0].Y);
    }

    [Fact]
    public void ToMd1_produces_valid_md1_with_matching_part_and_poly_counts()
    {
        var mesh = DcmInterchange.ToInterchange(SyntheticDc1());
        var md1 = Md1Interchange.ToMd1(mesh);

        // 2 parts → 4 MD1 objects (tri object + quad object per part).
        Assert.Equal(2, md1.NumParts);
        Assert.Equal(4, md1.NumObjects);

        // Part 0: tri object (index 0) has 1 triangle, quad object (index 1) has 1 quad, 4 verts.
        var p0Tri = md1.Objects[0];
        var p0Quad = md1.Objects[1];
        Assert.Equal(1, p0Tri.pri_count);
        Assert.Equal(1, p0Quad.pri_count);
        Assert.Equal(4, p0Tri.vtx_count);
        Assert.Equal(4, p0Tri.nor_count); // n := v ⇒ equal pools

        // Part 1: 1 triangle, 0 quads, 3 verts.
        Assert.Equal(1, md1.Objects[2].pri_count);
        Assert.Equal(0, md1.Objects[3].pri_count);
        Assert.Equal(3, md1.Objects[2].vtx_count);
    }

    [Fact]
    public void ToMd1_normal_index_equals_vertex_index()
    {
        var mesh = DcmInterchange.ToInterchange(SyntheticDc1());
        var md1 = Md1Interchange.ToMd1(mesh);

        // DCM normals are per-vertex, so every MD1 triangle corner must satisfy n == v.
        var tri = md1.GetTriangles(md1.Objects[0])[0];
        Assert.Equal(tri.v0, tri.n0);
        Assert.Equal(tri.v1, tri.n1);
        Assert.Equal(tri.v2, tri.n2);

        var quad = md1.GetQuads(md1.Objects[1])[0];
        Assert.Equal(quad.v0, quad.n0);
        Assert.Equal(quad.v3, quad.n3);
    }

    [Fact]
    public void ToMd1_carries_vertex_positions_verbatim()
    {
        var mesh = DcmInterchange.ToInterchange(SyntheticDc1());
        var md1 = Md1Interchange.ToMd1(mesh);

        // Part 0 local vertex 1 = synthetic global vertex 1 = (7, 11, 13).
        var pos = md1.GetPositionData(md1.Objects[0])[1];
        Assert.Equal((short)7, pos.x);
        Assert.Equal((short)11, pos.y);
        Assert.Equal((short)13, pos.z);
    }
}
