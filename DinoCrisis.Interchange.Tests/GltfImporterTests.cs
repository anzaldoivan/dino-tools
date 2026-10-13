using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using DinoCrisis.Model.Textures;
using SharpGLTF.Schema2;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>
/// P2 same-rig import (DC-MODEL-TOOLKIT-PLAN D4): the glTF→DCM return path. Synthetic tests pin
/// the inverse transforms and the contract refusals; the golden test runs the FULL CIRCLE on real
/// Regina — export core00 to GLB, read it back, splice it into the package, reload — and asserts
/// the strongest available oracle: the multiset of (world position, joint, texel) corner keys is
/// IDENTICAL before and after, the skeleton FK is untouched, and the animation tail survives
/// byte-exact (the splicer self-verifies that; Validate double-checks here).
/// </summary>
public class GltfImporterTests
{
    private static DcmModel SyntheticDc1() =>
        DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);

    private static ModelRoot Reload(ModelRoot root)
    {
        using var ms = new MemoryStream();
        root.WriteGLB(ms);
        ms.Position = 0;
        return ModelRoot.ReadGLB(ms);
    }

    [Fact]
    public void Synthetic_round_trip_recovers_world_positions_joints_and_triangulation()
    {
        var model = SyntheticDc1();
        var gltf = Reload(GltfInterchange.FromDcm(model));
        var mesh = GltfImporter.ReadSameRig(gltf, model, tex: null);

        // Triangulated corner budget is invariant (QuadMerge may reconstitute quads, which is
        // render-exact — a merged quad splits back to exactly its two source triangles).
        Assert.Equal(model.Triangles.Count + 2 * model.Quads.Count,
                     mesh.Triangles.Count + 2 * mesh.Quads.Count);

        // Every imported vertex's (world position, joint) must exist in the source model
        // assembled the same way the exporter assembles (DC1 vertex transform-run FK), and vice versa.
        var world = ObjExporter.ComputeWorldPositions(model);
        var expected = new HashSet<(int, int, int, int)>();
        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            void Add(int vi)
            {
                var v = model.Vertices[vi];
                int vertexPart = ObjExporter.ResolveVertexTransformPart(model, vi, pi);
                expected.Add((v.X + world[vertexPart].X, v.Y + world[vertexPart].Y,
                              v.Z + world[vertexPart].Z, vertexPart));
            }
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tr = model.Triangles[triCursor + t];
                Add(tr.V0); Add(tr.V1); Add(tr.V2);
            }
            triCursor += part.TriangleCount;
            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                Add(qd.V0); Add(qd.V1); Add(qd.V2); Add(qd.V3);
            }
            quadCursor += part.QuadCount;
        }
        var actual = mesh.Vertices.Select(v => (v.X, v.Y, v.Z, (int)v.Joint)).ToHashSet();
        Assert.Equal(expected, actual);

        // Normals inverted back to PSX fixed-point unit length (≈4096).
        Assert.All(mesh.Vertices, v =>
        {
            double len = System.Math.Sqrt((double)v.NX * v.NX + (double)v.NY * v.NY + (double)v.NZ * v.NZ);
            Assert.InRange(len, 4090, 4102);
        });
    }

    [Fact]
    public void Synthetic_round_trip_recovers_explicit_polygon_part_ownership()
    {
        var model = SyntheticDc1();
        var gltf = Reload(GltfInterchange.FromDcm(model));
        var mesh = GltfImporter.ReadSameRig(gltf, model, tex: null);

        var ownerCounts = mesh.Triangles.Select(t => t.Part)
            .Concat(mesh.Quads.Select(q => q.Part))
            .GroupBy(part => part)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(2, ownerCounts[0]); // source part 0: one triangle + one quad
        Assert.Equal(1, ownerCounts[1]); // source part 1: one triangle
        Assert.DoesNotContain(-1, ownerCounts.Keys);
    }

    [Fact]
    public void Wrong_joint_count_is_refused_with_diagnostic()
    {
        var model = SyntheticDc1();
        var gltf = Reload(GltfInterchange.FromDcm(model));

        // A target with one more part than the GLB's skin: grow via the builder.
        var b = model.ToBuilder();
        var reusedTri = b.Triangles[0];
        b.AddPart(new DcmPart { X = 1, Y = 2, Z = 3, Parent = 0, Child = 0 },
                  new[] { new DcmTriangle { V0 = reusedTri.V0, V1 = reusedTri.V1, V2 = reusedTri.V2 } },
                  System.Array.Empty<DcmQuad>());
        var bigger = DcmModel.Read(b.Serialize(SyntheticBlob.Base), 0, SyntheticBlob.Base, DcmVariant.Dc1);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, bigger, tex: null));
        Assert.Contains("joint_02", ex.Message); // names the missing bone
    }

    [Fact]
    public void Edited_hierarchy_is_refused()
    {
        var model = SyntheticDc1();

        // Build a 3-part model, export it, then import against a target whose part 2 has a
        // DIFFERENT parent — the same-rig contract must refuse the re-parented rig.
        static DcmModel ThreeParts(DcmModel src, sbyte parentOfPart2)
        {
            var b = src.ToBuilder();
            var t = b.Triangles[0];
            b.AddPart(new DcmPart { X = 5, Y = 5, Z = 5, Parent = parentOfPart2, Child = 0 },
                      new[] { new DcmTriangle { V0 = t.V0, V1 = t.V1, V2 = t.V2 } },
                      System.Array.Empty<DcmQuad>());
            return DcmModel.Read(b.Serialize(SyntheticBlob.Base), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        }

        var exported = ThreeParts(model, parentOfPart2: 0);
        var reParented = ThreeParts(model, parentOfPart2: 1);
        var gltf = Reload(GltfInterchange.FromDcm(exported));

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, reParented, tex: null));
        Assert.Contains("hierarchy", ex.Message);
    }

    [Fact]
    public void Unskinned_gltf_is_refused()
    {
        // A GLB with a mesh but no skin at all (plain scene).
        var scene = new SharpGLTF.Scenes.SceneBuilder();
        var mesh = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPosition>("m");
        var prim = mesh.UsePrimitive(new SharpGLTF.Materials.MaterialBuilder("mat"));
        prim.AddTriangle(new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 0),
                         new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0),
                         new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 1, 0));
        scene.AddRigidMesh(mesh, System.Numerics.Matrix4x4.Identity);

        var ex = Assert.Throws<InvalidDataException>(
            () => GltfImporter.ReadSameRig(scene.ToGltf2(), SyntheticDc1(), tex: null));
        Assert.Contains("skin", ex.Message);
    }

    // ---- gated golden: the full round-trip circle on real Regina ----

    [Dc1Fact("core00.dat")]
    public void Golden_regina_full_circle_export_import_splice_reload()
    {
        string core = FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat"));
        byte[] file = File.ReadAllBytes(core);

        var target = DcmLocator.FromPackage(file).First(m =>
            m.Header.PartCount == 15 && m.Header.TriangleCount == 314 && m.Header.QuadCount == 141);
        var pkg = GianPackage.TryParse(file)!;
        var tex = DcmTextureDecoder.TryDecode(pkg, file, target)!;
        Assert.NotNull(tex);

        // 1) export → 2) re-read (simulating a no-op Blender edit) → 3) splice → 4) reload.
        var gltf = Reload(GltfInterchange.FromDcm(target, tex));
        var mesh = GltfImporter.ReadSameRig(gltf, target, tex);
        Assert.Empty(mesh.Warnings); // native UV islands never straddle a page column

        var result = Dc1ModelSplicer.Splice(
            file, m => m.Header.PartCount == 15 && m.Header.TriangleCount == 314 && m.Header.QuadCount == 141, mesh);

        var injected = DcmLocator.FromPackage(result.Package).First(m => m.Header.PartCount == 15);
        Assert.True(DcmModel.Validate(injected));
        // QuadMerge restores strip quads: the triangulated budget is invariant and most of the
        // original 141 quads come back (any pairing is render-exact, so ≥ is the right assert).
        Assert.Equal(target.Header.TriangleCount + 2 * target.Header.QuadCount,
                     injected.Header.TriangleCount + 2 * injected.Header.QuadCount);
        Assert.True(injected.Header.QuadCount >= target.Header.QuadCount,
            $"quad merge recovered only {injected.Header.QuadCount}/{target.Header.QuadCount} quads");

        // Skeleton verbatim: rest offsets, parents, FK.
        Assert.Equal(target.Parts.Select(p => (p.X, p.Y, p.Z, p.Parent)),
                     injected.Parts.Select(p => (p.X, p.Y, p.Z, p.Parent)));
        Assert.Equal(ObjExporter.ComputeWorldPositions(target), ObjExporter.ComputeWorldPositions(injected));

        // THE ORACLE: the multiset of polygon-corner keys (world position, texel col/row) is
        // identical before and after the circle — geometry and texturing both survived exactly.
        var before = CornerKeys(target, tex);
        var after = CornerKeys(injected, tex);
        Assert.Equal(before.Count, after.Count);
        foreach (var kv in before)
            Assert.True(after.TryGetValue(kv.Key, out int n) && n == kv.Value,
                $"corner {kv.Key} count {kv.Value} -> {(after.TryGetValue(kv.Key, out int m2) ? m2 : 0)}");

        // Engine-facing fidelity, strongest form: an identity round-trip reproduces the STOCK
        // vertex/normal pool EXACTLY — count, order, duplicate records, out-of-range pads, raw
        // normals, byte for byte (the engine's pool-binding semantics are unproven, so only a
        // byte-identical pool is guaranteed correct) — and only stock tpage/clut values appear.
        Assert.Equal(target.Vertices.Count, injected.Vertices.Count);
        for (int i = 0; i < target.Vertices.Count; i++)
        {
            Assert.Equal(target.Vertices[i], injected.Vertices[i]);
            Assert.Equal(target.Normals[i], injected.Normals[i]);
        }

        var stockTpages = target.Triangles.Select(t => t.TPage).Concat(target.Quads.Select(q => q.TPage)).ToHashSet();
        var stockCluts = target.Triangles.Select(t => t.Clut).Concat(target.Quads.Select(q => q.Clut)).ToHashSet();
        Assert.All(injected.Triangles, t => { Assert.Contains(t.TPage, stockTpages); Assert.Contains(t.Clut, stockCluts); });
        Assert.All(injected.Quads, q => { Assert.Contains(q.TPage, stockTpages); Assert.Contains(q.Clut, stockCluts); });
    }

    [Fact]
    public void QuadMerge_reconstitutes_a_strip_quad_and_leaves_mismatched_uvs_alone()
    {
        // Two tris forming the exporter's split of quad (0,1,2,3): (0,1,2) + (1,3,2).
        var mesh = new ImportedMesh();
        for (int i = 0; i < 6; i++)
            mesh.Vertices.Add(new ImportedMesh.Vertex { X = i, Y = 0, Z = 0, Joint = 0, NY = 4096 });
        var uv = new Uv[] { new(0, 0), new(10, 0), new(0, 10), new(10, 10) };
        mesh.Triangles.Add(new ImportedMesh.Triangle
        { V0 = 0, V1 = 1, V2 = 2, Uv0 = uv[0], Uv1 = uv[1], Uv2 = uv[2], TPage = 7, Clut = 9 });
        mesh.Triangles.Add(new ImportedMesh.Triangle
        { V0 = 1, V1 = 3, V2 = 2, Uv0 = uv[1], Uv1 = uv[3], Uv2 = uv[2], TPage = 7, Clut = 9 });
        // A second pair sharing an edge but with a DIFFERENT UV at the shared corner — must not merge.
        mesh.Triangles.Add(new ImportedMesh.Triangle
        { V0 = 3, V1 = 4, V2 = 5, Uv0 = uv[0], Uv1 = uv[1], Uv2 = uv[2], TPage = 7, Clut = 9 });
        mesh.Triangles.Add(new ImportedMesh.Triangle
        { V0 = 4, V1 = 0, V2 = 5, Uv0 = new Uv(99, 99), Uv1 = uv[3], Uv2 = uv[2], TPage = 7, Clut = 9 });

        int merges = QuadMerge.Merge(mesh);

        Assert.Equal(1, merges);
        Assert.Equal(2, mesh.Triangles.Count);
        var q = Assert.Single(mesh.Quads);
        Assert.Equal((0, 1, 2, 3), (q.V0, q.V1, q.V2, q.V3)); // strip order restored
        Assert.Equal((uv[0], uv[1], uv[2], uv[3]), (q.Uv0, q.Uv1, q.Uv2, q.Uv3));
        Assert.Equal(7, q.TPage);
        Assert.Equal(9, q.Clut);
    }

    [Dc2Fact("E00.DAT", "E00.TEX")]
    public void Golden_dc2_e00_full_circle_in_place_splice()
    {
        string datPath = FixtureGate.Pristine(FixtureGate.Dc2File("E00.DAT"));
        byte[] file = File.ReadAllBytes(datPath);
        byte[] texBytes = File.ReadAllBytes(FixtureGate.Dc2File("E00.TEX"));

        var target = DcmLocator.FromPackage(file).First(m =>
            m.Header.PartCount == 21 && m.Header.TriangleCount == 300 && m.Header.QuadCount == 86);
        var tex = DcmTextureDecoder.TryDecodeDc2(texBytes, target)!;
        Assert.NotNull(tex);

        var gltf = Reload(GltfInterchange.FromDcm(target, tex));
        var mesh = GltfImporter.ReadSameRig(gltf, target, tex);
        Assert.Empty(mesh.Warnings);

        var result = Dc2ModelSplicer.Splice(file, target.LoadBase, mesh);
        Assert.DoesNotContain("WARNING", result.Report); // no polys left the header page

        var injected = DcmLocator.FromPackage(result.Package).First(m => m.Header.PartCount == 21);
        Assert.True(DcmModel.Validate(injected));
        Assert.Equal(target.LoadBase, injected.LoadBase);
        Assert.Equal(target.Header.TriangleCount + 2 * target.Header.QuadCount,
                     injected.Header.TriangleCount + 2 * injected.Header.QuadCount);
        Assert.True(injected.Header.QuadCount >= target.Header.QuadCount);
        Assert.Equal(target.Header.TPage, injected.Header.TPage);
        Assert.Equal(target.Header.Clut, injected.Header.Clut);

        // Skeleton verbatim.
        Assert.Equal(target.Parts.Select(p => (p.X, p.Y, p.Z, p.Parent)),
                     injected.Parts.Select(p => (p.X, p.Y, p.Z, p.Parent)));
        Assert.Equal(ObjExporter.ComputeWorldPositions(target), ObjExporter.ComputeWorldPositions(injected));

        // Pool preserved byte-exactly (see the DC1 golden for why).
        Assert.Equal(target.Vertices.Count, injected.Vertices.Count);
        for (int i = 0; i < target.Vertices.Count; i++)
        {
            Assert.Equal(target.Vertices[i], injected.Vertices[i]);
            Assert.Equal(target.Normals[i], injected.Normals[i]);
        }

        // Corner multiset oracle, DC2 form (header tpage for every poly).
        var before = CornerKeys(target, tex);
        var after = CornerKeys(injected, tex);
        Assert.Equal(before.Count, after.Count);
        foreach (var kv in before)
            Assert.True(after.TryGetValue(kv.Key, out int n) && n == kv.Value,
                $"corner {kv.Key} count {kv.Value} -> {(after.TryGetValue(kv.Key, out int m2) ? m2 : 0)}");
    }

    /// <summary>Corner multiset: (FK world position of the corner, its texel) → count. Quads are
    /// triangulated PSX-strip-wise so both sides count 3 corners per final triangle.</summary>
    private static Dictionary<(int, int, int, int, int), int> CornerKeys(DcmModel m, DcmTexture tex)
    {
        var world = ObjExporter.ComputeWorldPositions(m);
        var keys = new Dictionary<(int, int, int, int, int), int>();
        bool dc2 = m.Variant.Game == DcmGame.Dc2;
        void Add(int vi, int part, Uv uv, ushort polyTpage)
        {
            ushort tpage = dc2 ? m.Header.TPage : polyTpage; // DC2 polys carry no tpage; the header does
            var v = m.Vertices[vi];
            var (s, t) = tex.MapUv(uv.U, uv.V, tpage);
            var key = (v.X + world[part].X, v.Y + world[part].Y, v.Z + world[part].Z,
                       (int)System.Math.Round(s * tex.Image.Width), (int)System.Math.Round((1.0 - t) * tex.Image.Height));
            keys[key] = keys.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < m.Parts.Count; pi++)
        {
            var part = m.Parts[pi];
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tr = m.Triangles[triCursor + t];
                Add(tr.V0, pi, tr.Uv0, tr.TPage); Add(tr.V1, pi, tr.Uv1, tr.TPage); Add(tr.V2, pi, tr.Uv2, tr.TPage);
            }
            triCursor += part.TriangleCount;
            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = m.Quads[quadCursor + q];
                // PSX strip → (0,1,2)+(1,3,2): corners 0,2 appear once, 1,3... corner counts: v0×1,v1×2,v2×2,v3×1.
                Add(qd.V0, pi, qd.Uv0, qd.TPage);
                Add(qd.V1, pi, qd.Uv1, qd.TPage); Add(qd.V1, pi, qd.Uv1, qd.TPage);
                Add(qd.V2, pi, qd.Uv2, qd.TPage); Add(qd.V2, pi, qd.Uv2, qd.TPage);
                Add(qd.V3, pi, qd.Uv3, qd.TPage);
            }
            quadCursor += part.QuadCount;
        }
        return keys;
    }
}
