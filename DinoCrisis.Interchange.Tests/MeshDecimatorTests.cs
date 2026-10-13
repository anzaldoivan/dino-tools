using System;
using System.Collections.Generic;
using System.Linq;
using DinoCrisis.Interchange;
using DinoCrisis.Model;
using Xunit;
using static DinoCrisis.Interchange.MeshDecimator;

namespace DinoCrisis.Interchange.Tests;

public sealed class MeshDecimatorTests
{
    private static readonly Uv U = new(0, 0);

    // n×n grid of unit quads on z=0, all bone 0.
    private static (List<DecVertex> V, List<DecFace> F) Grid(int n, short bone = 0)
    {
        var V = new List<DecVertex>();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                V.Add(new DecVertex { X = x, Y = y, Z = 0, Bone = bone, NX = 0, NY = 0, NZ = 1 });
        int Idx(int x, int y) => y * (n + 1) + x;
        var F = new List<DecFace>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                F.Add(new DecFace { V = new[] { Idx(x, y), Idx(x + 1, y), Idx(x + 1, y + 1), Idx(x, y + 1) }, Uv = new[] { U, U, U, U }, Bone = bone });
        return (V, F);
    }

    [Fact]
    public void Decimate_reduces_to_byte_budget()
    {
        var (V, F) = Grid(6);
        int before = V.Count * 16 + F.Count * 20;
        int budget = before / 2;
        var r = Decimate(V, F, budget, DecByteModel.Dc1);
        Assert.True(r.Bytes <= budget, $"bytes {r.Bytes} > budget {budget}");
        Assert.True(r.Collapses > 0);
    }

    [Fact]
    public void Under_budget_is_a_noop()
    {
        var (V, F) = Grid(2);
        int budget = V.Count * 16 + F.Count * 20 + 1000;
        var r = Decimate(V, F, budget, DecByteModel.Dc1);
        Assert.Equal(0, r.Collapses);
        Assert.Equal(V.Count, r.Vertices.Count);
        Assert.Equal(F.Count, r.Quads);
    }

    [Fact]
    public void Never_collapses_across_bones()
    {
        var (V0, F0) = Grid(4, bone: 0);
        var (V1, F1) = Grid(4, bone: 1);
        foreach (var v in V1) v.X += 1000; // bone-1 block far away
        int off = V0.Count;
        foreach (var f in F1) for (int k = 0; k < f.V.Length; k++) f.V[k] += off;
        var V = V0.Concat(V1).ToList();
        var F = F0.Concat(F1).ToList();

        var r = Decimate(V, F, (V.Count * 16 + F.Count * 20) / 3, DecByteModel.Dc1);

        // every face's corners all share the face's bone; both bones survive
        foreach (var f in r.Faces)
            foreach (int vi in f.V)
                Assert.Equal(f.Bone, r.Vertices[vi].Bone);
        Assert.Contains(r.Faces, f => f.Bone == 0);
        Assert.Contains(r.Faces, f => f.Bone == 1);
        // no bone-0 vertex drifted into the bone-1 block and vice versa
        Assert.All(r.Vertices, v => Assert.True(v.Bone == 0 ? v.X < 500 : v.X > 500));
    }

    [Fact]
    public void Quad_losing_one_edge_becomes_a_triangle_not_deleted()
    {
        // one quad; its shortest edge (A-B) is tiny, so the first collapse turns it into a triangle
        var V = new List<DecVertex>
        {
            new() { X = 0, Y = 0, Z = 0, NZ = 1 },       // A
            new() { X = 0.02, Y = 0, Z = 0, NZ = 1 },    // B (≈A)
            new() { X = 1, Y = 1, Z = 0, NZ = 1 },       // C
            new() { X = -1, Y = 1, Z = 0, NZ = 1 },      // D
        };
        var F = new List<DecFace> { new() { V = new[] { 0, 1, 2, 3 }, Uv = new[] { U, U, U, U }, Bone = 0, Page = 1 } };
        int budget = 3 * 16 + 16 + 5; // room for exactly 3 verts + 1 tri, not the 4-vert quad
        var r = Decimate(V, F, budget, DecByteModel.Dc1);

        Assert.True(r.Collapses >= 1);
        Assert.Equal(1, r.Triangles);   // the quad SURVIVED as a triangle
        Assert.Equal(0, r.Quads);       // …not deleted
        Assert.Equal(3, r.Vertices.Count);
        Assert.Equal((ushort)1, Assert.Single(r.Faces).Page);
    }

    [Fact]
    public void Material_page_survives_a_noop_decimation_in_face_order()
    {
        var (V, F) = Grid(2);
        ushort[] pages = [0, 1, 1, 0];
        for (int i = 0; i < F.Count; i++) F[i].Page = pages[i];

        var r = Decimate(V, F, int.MaxValue, DecByteModel.Dc1);

        Assert.Equal(pages, r.Faces.Select(face => face.Page));
    }

    [Fact]
    public void Planar_mesh_stays_in_plane()
    {
        var (V, F) = Grid(6);
        var r = Decimate(V, F, (V.Count * 16 + F.Count * 20) / 2, DecByteModel.Dc1);
        Assert.All(r.Vertices, v => Assert.True(Math.Abs(v.Z) < 1e-6, $"z drifted to {v.Z}"));
    }

    [Fact]
    public void Boundary_corners_are_preserved()
    {
        int n = 5;
        var (V, F) = Grid(n);
        var r = Decimate(V, F, (V.Count * 16 + F.Count * 20) / 3, DecByteModel.Dc1);
        (double, double)[] corners = { (0, 0), (n, 0), (0, n), (n, n) };
        foreach (var (cx, cy) in corners)
            Assert.Contains(r.Vertices, v => Math.Abs(v.X - cx) < 1e-6 && Math.Abs(v.Y - cy) < 1e-6);
    }

    [Fact]
    public void Input_lists_are_not_mutated()
    {
        var (V, F) = Grid(4);
        var vx = V.Select(v => v.X).ToArray();
        var f0 = (int[])F[0].V.Clone();
        Decimate(V, F, 100, DecByteModel.Dc1);
        Assert.Equal(vx, V.Select(v => v.X).ToArray());
        Assert.Equal(f0, F[0].V);
    }

    [Fact]
    public void Dc2_byte_model_uses_smaller_strides()
    {
        var (V, F) = Grid(5);
        var r = Decimate(V, F, (V.Count * 16 + F.Count * 16) / 2, DecByteModel.Dc2);
        Assert.Equal(r.Vertices.Count * 16 + r.Triangles * 12 + r.Quads * 16, r.Bytes);
        Assert.True(r.Bytes <= (V.Count * 16 + F.Count * 16) / 2);
    }
}
