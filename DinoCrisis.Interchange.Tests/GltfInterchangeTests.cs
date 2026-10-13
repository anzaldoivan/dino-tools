using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using DinoCrisis.Model.Textures;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;

namespace DinoCrisis.Interchange.Tests;

/// <summary>
/// P1 glTF export (DC-MODEL-TOOLKIT-PLAN): synthetic tests on the clean-room DCM blob assert the
/// skinned-GLB structure (joints, rigid weights, triangulation, orientation), and gated goldens
/// run the real witnesses (DC1 core00 Regina textured, st605 Theri; DC2 E00 + its .TEX) end to
/// end, including the FK oracle: every glTF joint's world translation must equal the DCM's own
/// forward-kinematic rest position (scaled/flipped) — the same dual-oracle style EmrInterchange used.
/// </summary>
public class GltfInterchangeTests
{
    private readonly ITestOutputHelper _output;

    public GltfInterchangeTests(ITestOutputHelper output) => _output = output;

    private static DcmModel SyntheticDc1() =>
        DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);

    private static ModelRoot RoundTrip(ModelRoot root)
    {
        // Serialize to GLB and re-read: proves the DOM passes SharpGLTF's writer validation and
        // that everything asserted below survives the actual byte format.
        using var ms = new MemoryStream();
        root.WriteGLB(ms);
        ms.Position = 0;
        return ModelRoot.ReadGLB(ms);
    }

    private static JsonElement RawGlbJson(ModelRoot root)
    {
        using var stream = new MemoryStream();
        root.WriteGLB(stream);
        byte[] glb = stream.ToArray();
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4)));
        int jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        return document.RootElement.Clone();
    }

    private static DcmTexture SyntheticTexture()
    {
        var image = new PsxImage(256, 512);
        var constructor = typeof(DcmTexture).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (DcmTexture)constructor.Invoke(new object[] { image, 0, 0, 2 });
    }

    [Fact]
    public void Synthetic_exports_skinned_glb_with_one_joint_per_part()
    {
        var model = SyntheticDc1();
        var root = RoundTrip(GltfInterchange.FromDcm(model));

        var skin = Assert.Single(root.LogicalSkins);
        Assert.Equal(model.Parts.Count, skin.JointsCount);

        // Joint naming is the P2 same-rig import contract.
        for (int i = 0; i < model.Parts.Count; i++)
            Assert.Contains(root.LogicalNodes, n => n.Name == GltfInterchange.JointName(i));

        // Synthetic part 1: rest (10,20,30) parented to 0 → local translation scaled + Y-flipped.
        var j1 = root.LogicalNodes.First(n => n.Name == GltfInterchange.JointName(1));
        Assert.Equal(new Vector3(10, -20, 30) * GltfInterchange.DefaultScale, j1.LocalTransform.Translation);
    }

    [Fact]
    public void Synthetic_triangulates_quads_and_binds_rigidly()
    {
        var model = SyntheticDc1();
        var root = RoundTrip(GltfInterchange.FromDcm(model));

        var prim = Assert.Single(Assert.Single(root.LogicalMeshes).Primitives);

        // Every quad becomes two triangles.
        int expected = model.Triangles.Count + 2 * model.Quads.Count;
        Assert.Equal(expected, prim.GetTriangleIndices().Count());

        // Rigid binding: exactly one joint per vertex at weight 1, joint index in range.
        var joints = prim.GetVertexAccessor("JOINTS_0")!.AsVector4Array();
        var weights = prim.GetVertexAccessor("WEIGHTS_0")!.AsVector4Array();
        Assert.Equal(joints.Count, weights.Count);
        for (int i = 0; i < joints.Count; i++)
        {
            Assert.Equal(1f, weights[i].X, 3);
            Assert.Equal(0f, weights[i].Y + weights[i].Z + weights[i].W, 3);
            Assert.InRange(joints[i].X, 0, model.Parts.Count - 1);
        }
    }

    [Fact]
    public void Synthetic_positions_are_fk_world_scaled_and_y_flipped()
    {
        var model = SyntheticDc1();
        var root = RoundTrip(GltfInterchange.FromDcm(model));
        var prim = root.LogicalMeshes[0].Primitives[0];
        var positions = prim.GetVertexAccessor("POSITION")!.AsVector3Array();

        // Build the expected world-space vertex set exactly as the exporter must compute it.
        var world = ObjExporter.ComputeWorldPositions(model);
        var expected = new HashSet<Vector3>();
        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            void Add(int vi)
            {
                var v = model.Vertices[vi];
                int vertexPart = ObjExporter.ResolveVertexTransformPart(model, vi, pi);
                var w = world[vertexPart];
                expected.Add(new Vector3(v.X + w.X, -(v.Y + w.Y), v.Z + w.Z) * GltfInterchange.DefaultScale);
            }
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                Add(tri.V0); Add(tri.V1); Add(tri.V2);
            }
            triCursor += part.TriangleCount;
            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                Add(qd.V0); Add(qd.V1); Add(qd.V2); Add(qd.V3);
            }
            quadCursor += part.QuadCount;
        }

        Assert.All(positions, p => Assert.Contains(p, expected));

        // Normals are unit-length (glTF requirement; degenerate sources fall back to +Y).
        var normals = prim.GetVertexAccessor("NORMAL")!.AsVector3Array();
        Assert.All(normals, n => Assert.Equal(1f, n.Length(), 2));
    }

    [Fact]
    public void Synthetic_exported_face_winding_agrees_with_custom_normals()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        int verticesOffset = checked((int)(
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4)) - SyntheticBlob.Base));
        int normalsOffset = checked((int)(
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4)) - SyntheticBlob.Base));
        (short X, short Y, short Z)[] positions = [(0, 0, 0), (100, 0, 0), (0, 0, 100)];
        for (int i = 0; i < positions.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(verticesOffset + i * 8, 2), positions[i].X);
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(verticesOffset + i * 8 + 2, 2), positions[i].Y);
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(verticesOffset + i * 8 + 4, 2), positions[i].Z);
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + i * 8, 2), 0);
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + i * 8 + 2, 2), 4096);
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + i * 8 + 4, 2), 0);
        }

        var model = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var primitive = Assert.Single(Assert.Single(RoundTrip(GltfInterchange.FromDcm(model)).LogicalMeshes).Primitives);
        var exportedPositions = primitive.GetVertexAccessor("POSITION")!.AsVector3Array();
        var exportedNormals = primitive.GetVertexAccessor("NORMAL")!.AsVector3Array();
        var (a, b, c) = primitive.GetTriangleIndices().First();
        var geometric = Vector3.Cross(exportedPositions[b] - exportedPositions[a], exportedPositions[c] - exportedPositions[a]);
        var custom = exportedNormals[a] + exportedNormals[b] + exportedNormals[c];

        Assert.True(Vector3.Dot(geometric, custom) > 0,
            $"exported geometric face normal {geometric} is opposed to averaged custom normal {custom}");
    }

    [Fact]
    public void Synthetic_exported_material_is_single_sided()
    {
        JsonElement json = RawGlbJson(GltfInterchange.FromDcm(SyntheticDc1()));
        JsonElement material = json.GetProperty("materials")[0];

        Assert.True(!material.TryGetProperty("doubleSided", out var doubleSided) || !doubleSided.GetBoolean(),
            "native DCM faces are single-sided");
    }

    [Fact]
    public void Synthetic_exported_texture_uses_explicit_nearest_clamp_sampler()
    {
        JsonElement json = RawGlbJson(GltfInterchange.FromDcm(SyntheticDc1(), SyntheticTexture()));
        JsonElement sampler = Assert.Single(json.GetProperty("samplers").EnumerateArray());

        Assert.Equal(9728, sampler.GetProperty("magFilter").GetInt32());
        Assert.Equal(9728, sampler.GetProperty("minFilter").GetInt32());
        Assert.Equal(33071, sampler.GetProperty("wrapS").GetInt32());
        Assert.Equal(33071, sampler.GetProperty("wrapT").GetInt32());
    }

    [Fact]
    public void Synthetic_cross_part_vertex_uses_its_pool_binding_not_the_polygon_owner()
    {
        byte[] blob = SyntheticBlob.Build(DcmVariant.Dc1);
        int verticesOffset = checked((int)(
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4)) - SyntheticBlob.Base));
        short[] poolParts = [0, 0, 1, 1];
        for (int i = 0; i < poolParts.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(verticesOffset + i * 8 + 6, 2), poolParts[i]);
        int normalsOffset = checked((int)(
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4)) - SyntheticBlob.Base));
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + 8, 2), 4096);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + 10, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(normalsOffset + 12, 2), 0);

        var model = DcmModel.Read(blob, 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var root = RoundTrip(GltfInterchange.FromDcm(model));
        var prim = Assert.Single(Assert.Single(root.LogicalMeshes).Primitives);
        var positions = prim.GetVertexAccessor("POSITION")!.AsVector3Array();
        var normals = prim.GetVertexAccessor("NORMAL")!.AsVector3Array();
        var joints = prim.GetVertexAccessor("JOINTS_0")!.AsVector4Array();

        // Vertex 1 is referenced by both parts, but its pool binding is part 0. Every UV-seam copy
        // must therefore have the same part-0 FK position and rigid joint instead of being pulled to
        // whichever part owns the polygon that happens to reference it.
        var source = model.Vertices[1];
        var normal = model.Normals[1];
        var expectedPosition = new Vector3(source.X, -(source.Y - 100), source.Z) * GltfInterchange.DefaultScale;
        var expectedNormal = Vector3.Normalize(new Vector3(normal.X, -normal.Y, normal.Z));
        int[] copies = Enumerable.Range(0, normals.Count)
            .Where(i => Vector3.Distance(normals[i], expectedNormal) < 0.00001f)
            .ToArray();

        Assert.True(copies.Length >= 2, "the shared vertex should have at least one corner copy per referencing part");
        Assert.All(copies, i =>
        {
            Assert.Equal(expectedPosition.X, positions[i].X, 5);
            Assert.Equal(expectedPosition.Y, positions[i].Y, 5);
            Assert.Equal(expectedPosition.Z, positions[i].Z, 5);
            Assert.Equal(0f, joints[i].X);
        });
    }

    [Fact]
    public void Synthetic_exports_polygon_owner_as_reserved_second_uv_set()
    {
        var model = SyntheticDc1();
        var root = RoundTrip(GltfInterchange.FromDcm(model));
        var prim = Assert.Single(Assert.Single(root.LogicalMeshes).Primitives);
        var owners = prim.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();

        Assert.NotNull(owners);
        var triangles = prim.GetTriangleIndices().ToArray();
        Assert.Equal(4, triangles.Length);
        for (int t = 0; t < triangles.Length; t++)
        {
            float expectedPart = t < 3 ? 0 : 1;
            var (a, b, c) = triangles[t];
            Assert.Equal(expectedPart, owners![a].X);
            Assert.Equal(expectedPart, owners[b].X);
            Assert.Equal(expectedPart, owners[c].X);
            Assert.All(new[] { owners[a].Y, owners[b].Y, owners[c].Y },
                stockIndex => Assert.InRange(stockIndex, 0, model.Vertices.Count - 1));
        }
    }

    [Fact]
    public void Png_encoder_emits_valid_signature_and_dimensions()
    {
        var img = new PsxImage(2, 2);
        img.Set(0, 0, 255, 0, 0, 255);
        img.Set(1, 0, 0, 255, 0, 255);
        img.Set(0, 1, 0, 0, 255, 255);
        img.Set(1, 1, 255, 255, 255, 0);

        byte[] png = PngEncoder.Encode(img);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));
        // IHDR width/height are big-endian at offsets 16/20.
        Assert.Equal(2, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(2, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        // SharpGLTF must recognize it as an embeddable PNG.
        Assert.True(new SharpGLTF.Memory.MemoryImage(png).IsPng);
    }

    // ---- gated goldens (skip when game data is absent; no copyrighted bytes committed) ----

    [Dc1Fact("core00.dat")]
    public void Golden_regina_reports_face_normal_alignment_and_triangle_417_semantics()
    {
        string core = FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat"));
        byte[] package = File.ReadAllBytes(core);
        var model = DcmLocator.FromPackage(package).First(candidate =>
            candidate.Header.PartCount == 15 && candidate.Header.TriangleCount == 314 &&
            candidate.Header.QuadCount == 141);
        var primitive = Assert.Single(Assert.Single(RoundTrip(GltfInterchange.FromDcm(model)).LogicalMeshes).Primitives);
        var positions = primitive.GetVertexAccessor("POSITION")!.AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL")!.AsVector3Array();
        var owners = primitive.GetVertexAccessor("TEXCOORD_1")!.AsVector2Array();
        var triangles = primitive.GetTriangleIndices().ToArray();
        int aligned = 0, opposed = 0, degenerate = 0;
        float triangle417Dot = 0;
        int[]? triangle417Stock = null;
        for (int i = 0; i < triangles.Length; i++)
        {
            var (a, b, c) = triangles[i];
            var geometric = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (geometric.LengthSquared() < 1e-12f)
            {
                degenerate++;
                continue;
            }
            float dot = Vector3.Dot(geometric, normals[a] + normals[b] + normals[c]);
            if (dot > 0) aligned++; else if (dot < 0) opposed++;
            if (i == 417)
            {
                int[] stock = [(int)owners[a].Y, (int)owners[b].Y, (int)owners[c].Y];
                triangle417Dot = dot;
                triangle417Stock = stock;
                _output.WriteLine(
                    $"triangle 417: owner={(int)owners[a].X}, stock={string.Join('/', stock)}, dot={dot:G9}; " +
                    $"vertices={string.Join("; ", stock.Select(index => model.Vertices[index]))}; " +
                    $"normals={string.Join("; ", stock.Select(index => model.Normals[index]))}");
            }
        }

        _output.WriteLine($"Regina face/custom-normal relation: aligned={aligned}, opposed={opposed}, degenerate={degenerate}");
        Assert.Equal(596, triangles.Length);
        Assert.Equal(0, degenerate);
        Assert.Equal(595, aligned);
        Assert.Equal(1, opposed);
        Assert.True(triangle417Dot < 0, "triangle 417 must remain the truthful stock-normal exception");
        Assert.Equal(new[] { 205, 213, 224 }, triangle417Stock);
    }

    [Dc1Fact("core00.dat")]
    public void Golden_regina_core00_exports_textured_glb_with_fk_exact_joints()
    {
        // Prefer the pristine backup: a character injection may have replaced the live core00.dat.
        string core = FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat"));
        byte[] file = File.ReadAllBytes(core);
        var model = DcmLocator.FromPackage(file).First(m =>
            m.Header.PartCount == 15 && m.Header.TriangleCount == 314 && m.Header.QuadCount == 141);

        var pkg = GianPackage.TryParse(file)!;
        var tex = DcmTextureDecoder.TryDecode(pkg, file, model);
        Assert.NotNull(tex); // Regina's 256×464 skin (DCM-TOOLSET-PLAN Inc 6)

        var gltf = RoundTrip(GltfInterchange.FromDcm(model, tex));

        // Textured: exactly one image, and it is our PNG.
        var image = Assert.Single(gltf.LogicalImages);
        Assert.True(image.Content.IsPng);

        // FK oracle: every joint's WORLD translation equals the DCM's own FK rest position.
        var world = ObjExporter.ComputeWorldPositions(model);
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var node = gltf.LogicalNodes.First(n => n.Name == GltfInterchange.JointName(i));
            var expected = new Vector3(world[i].X, -world[i].Y, world[i].Z) * GltfInterchange.DefaultScale;
            var actual = node.WorldMatrix.Translation;
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
            Assert.Equal(expected.Z, actual.Z, 3);
        }

        // Texture↔UV coherence: every UV must land inside [0,1] of the decoded skin.
        var prim = gltf.LogicalMeshes[0].Primitives[0];
        var uvs = prim.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        var joints = prim.GetVertexAccessor("JOINTS_0")!.AsVector4Array();
        Assert.Contains(joints, joint => joint.X == 3); // Regina's hand transform run has no owning polygons.
        Assert.All(uvs, uv =>
        {
            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
        });
    }

    [Dc1Theory("st605.dat", "st106.dat")]
    [InlineData("st605.dat", 22, 334, 57)]   // Therizinosaurus
    [InlineData("st106.dat", 15, 282, 179)]  // Velociraptor
    public void Golden_dc1_room_models_export_valid_skinned_glb(string file, int parts, int tris, int quads)
    {
        byte[] bytes = File.ReadAllBytes(FixtureGate.Dc1File(file));
        var model = DcmLocator.FromPackage(bytes).First(m =>
            m.Header.PartCount == parts && m.Header.TriangleCount == tris && m.Header.QuadCount == quads);

        var gltf = RoundTrip(GltfInterchange.FromDcm(model));
        Assert.Equal(parts, gltf.LogicalSkins[0].JointsCount);
        Assert.Equal(tris + 2 * quads, gltf.LogicalMeshes[0].Primitives[0].GetTriangleIndices().Count());
    }

    [Dc2Fact("E00.DAT", "E00.TEX")]
    public void Golden_dc2_e00_exports_textured_glb_from_sibling_tex()
    {
        // Prefer the pristine backup if a randomizer install has overwritten the live file.
        string datPath = FixtureGate.Pristine(FixtureGate.Dc2File("E00.DAT"));

        byte[] dat = File.ReadAllBytes(datPath);
        byte[] tex = File.ReadAllBytes(FixtureGate.Dc2File("E00.TEX"));
        var model = DcmLocator.FromPackage(dat).First(m =>
            m.Header.PartCount == 21 && m.Header.TriangleCount == 300 && m.Header.QuadCount == 86);

        string outPath = Path.Combine(Path.GetTempPath(), "dcm-gltf-test-e00.glb");
        try
        {
            bool textured = GltfInterchange.ExportGlb(dat, model, outPath, texFileBytes: tex);
            Assert.True(textured);

            var gltf = ModelRoot.Load(outPath);
            Assert.Equal(21, gltf.LogicalSkins[0].JointsCount);
            Assert.Single(gltf.LogicalImages);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
