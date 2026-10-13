using System.Globalization;
using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class ObjExportTests
{
    private static (int v, int vt, int vn, string[] faces) Parse(string obj)
    {
        var lines = obj.Split('\n');
        int v = lines.Count(l => l.StartsWith("v "));
        int vt = lines.Count(l => l.StartsWith("vt "));
        int vn = lines.Count(l => l.StartsWith("vn "));
        var faces = lines.Where(l => l.StartsWith("f ")).ToArray();
        return (v, vt, vn, faces);
    }

    private static (double X, double Y, double Z)[] ParseNormals(string obj) =>
        obj.Split('\n').Where(l => l.StartsWith("vn ")).Select(l =>
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (double.Parse(p[1], CultureInfo.InvariantCulture),
                    double.Parse(p[2], CultureInfo.InvariantCulture),
                    double.Parse(p[3], CultureInfo.InvariantCulture));
        }).ToArray();

    [Theory]
    [InlineData(DcmGame.Dc1)]
    [InlineData(DcmGame.Dc2)]
    public void Synthetic_ExportsWellFormedObj(DcmGame game)
    {
        var v = DcmVariant.For(game);
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;

        var (vCount, vtCount, vnCount, faces) = Parse(ObjExporter.Export(model));

        // one face per polygon; one UV per polygon corner
        Assert.Equal(model.Triangles.Count + model.Quads.Count, faces.Length);
        Assert.Equal(model.Triangles.Count * 3 + model.Quads.Count * 4, vtCount);
        Assert.Equal(7, vCount); // part0 uses {0,1,2,3}=4, part1 uses {1,2,3}=3
        // normals are per-vertex (parallel pool) → one vn per emitted vertex
        Assert.Equal(vCount, vnCount);

        // every face reference is in range and well-formed "v/vt/vn" with vn == v
        foreach (var f in faces)
        {
            var corners = f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.InRange(corners.Length, 3, 4);
            foreach (var c in corners)
            {
                var p = c.Split('/');
                Assert.Equal(3, p.Length);
                int vi = int.Parse(p[0]), vti = int.Parse(p[1]), vni = int.Parse(p[2]);
                Assert.InRange(vi, 1, vCount);
                Assert.InRange(vti, 1, vtCount);
                Assert.Equal(vi, vni); // vn emitted in lockstep with v → same index
            }
        }
    }

    [Fact]
    public void Synthetic_EmitsPerVertexUnitNormals()
    {
        var v = DcmVariant.Dc1;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        // synthetic pool normals: n[i] = (-i, 2i, 3i). n0 is degenerate (0,0,0); n1..n3 unit-normalize.
        var normals = ParseNormals(ObjExporter.Export(model, flipY: false));

        Assert.Equal(7, normals.Length);
        foreach (var n in normals)
        {
            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            Assert.True(len < 1.0001, $"normal {n} should be unit or zero, len={len}");
            Assert.True(len > 0.999 || len < 1e-9, $"normal {n} should be unit or zero, len={len}");
        }
        // a unit normal in the expected direction of vertex 1 = (-1,2,3)/sqrt(14)
        double s = Math.Sqrt(14.0);
        Assert.Contains(normals, n =>
            Math.Abs(n.X - -1 / s) < 1e-4 && Math.Abs(n.Y - 2 / s) < 1e-4 && Math.Abs(n.Z - 3 / s) < 1e-4);
        // the degenerate vertex-0 normal emits as zero (viewer falls back to the face normal)
        Assert.Contains(normals, n => n.X == 0 && n.Y == 0 && n.Z == 0);
    }

    [Fact]
    public void FlipY_NegatesNormalY()
    {
        var v = DcmVariant.Dc1;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        double s = Math.Sqrt(14.0);
        var flipped = ParseNormals(ObjExporter.Export(model, flipY: true));
        // vertex 1 = (-1,2,3): with the Y flip its normal Y is negated → (-1,-2,3)/sqrt(14)
        Assert.Contains(flipped, n =>
            Math.Abs(n.X - -1 / s) < 1e-4 && Math.Abs(n.Y - -2 / s) < 1e-4 && Math.Abs(n.Z - 3 / s) < 1e-4);
    }

    [Fact]
    public void Dc1VertexPoolBindingWorldTranslation_IsAppliedToVertices()
    {
        var v = DcmVariant.Dc1;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;

        // part0 root xyz=(0,-100,0); part1 parent0 xyz=(10,20,30) → world1=(10,-80,30)
        var world = ObjExporter.ComputeWorldPositions(model);
        Assert.Equal((0, -100, 0), world[0]);
        Assert.Equal((10, -80, 30), world[1]);

        // Part 1 owns the second triangle (verts 1,2,3), but shared pool vertex 1 is bound to
        // DC1 transform run 0. Without the Y flip it must stay at (7,11-100,13) = (7,-89,13)
        // in both per-part OBJ seam copies instead of being translated by polygon owner 1.
        var obj = ObjExporter.Export(model, flipY: false);
        Assert.Equal(2, obj.Split('\n').Count(line => line == "v 7 -89 13"));
        Assert.DoesNotContain("v 17 -69 43\n", obj);
    }

    [Dc1Fact("st106.dat")]
    public void Golden_RaptorExports_FaceCountMatchesPolygons()
    {
        var model = DcmLocator.FromDc1Package(File.ReadAllBytes(FixtureGate.Dc1File("st106.dat")))
            .First(m => m.Header.PartCount == 15 && m.Header.TriangleCount == 282 && m.Header.QuadCount == 179);

        var obj = ObjExporter.Export(model);
        var (vCount, vtCount, vnCount, faces) = Parse(obj);
        Assert.Equal(282 + 179, faces.Length);
        Assert.Equal(282 * 3 + 179 * 4, vtCount);
        Assert.True(vCount > 0);
        // every exported vertex carries a normal; real DCM normals unit-normalize cleanly
        Assert.Equal(vCount, vnCount);
        Assert.All(ParseNormals(obj), n =>
        {
            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            Assert.True(len < 1.0001 && (len > 0.999 || len < 1e-9), $"len={len}");
        });
    }
}
