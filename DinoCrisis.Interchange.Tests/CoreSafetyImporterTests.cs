using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DinoCrisis.Model;
using DinoCrisis.Model.Tests;
using DinoCrisis.Model.Textures;
using SharpGLTF.Schema2;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class CoreSafetyImporterTests
{
    private static DcmModel Synthetic(DcmVariant variant) =>
        DcmModel.Read(SyntheticBlob.Build(variant), 0, SyntheticBlob.Base, variant);

    [Fact]
    public void Same_rig_rejects_edited_local_rest_transform()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var joint = gltf.LogicalNodes.Single(n => n.Name == "joint_01");
        joint.LocalMatrix *= Matrix4x4.CreateTranslation(0.01f, 0, 0);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("rest transform", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_rig_rejects_edited_inverse_bind_matrix()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var skin = Assert.Single(gltf.LogicalSkins);
        var bindings = Enumerable.Range(0, skin.JointsCount)
            .Select(i => (skin.GetJoint(i).Joint, skin.InverseBindMatrices[i]))
            .ToArray();
        bindings[0] = (bindings[0].Joint, bindings[0].Item2 * Matrix4x4.CreateTranslation(0.01f, 0, 0));
        skin.BindJoints(bindings);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("inverse bind", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Vector4, Vector4, string> InvalidWeights => new()
    {
        { new Vector4(0, 1, 0, 0), new Vector4(0.5f, 0.5f, 0, 0), "rigid" },
        { Vector4.Zero, new Vector4(0.5f, 0, 0, 0), "normalized" },
        { new Vector4(99, 0, 0, 0), new Vector4(1, 0, 0, 0), "joint" },
        { Vector4.Zero, new Vector4(float.NaN, 0, 0, 0), "finite" },
        { Vector4.Zero, new Vector4(float.PositiveInfinity, 0, 0, 0), "finite" },
        { Vector4.Zero, new Vector4(-0.01f, 1, 0, 0), "outside 0..1" },
        { Vector4.Zero, new Vector4(1.01f, 0, 0, 0), "outside 0..1" },
    };

    [Theory]
    [MemberData(nameof(InvalidWeights))]
    public void Same_rig_rejects_invalid_skin_weights(Vector4 joints, Vector4 weights, string diagnostic)
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        primitive.GetVertexAccessor("JOINTS_0")!.AsVector4Array()[0] = joints;
        var weightAccessor = primitive.GetVertexAccessor("WEIGHTS_0")!;
        if (float.IsFinite(weights.X))
        {
            weightAccessor.AsVector4Array()[0] = weights;
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                weightAccessor.SourceBufferView.Content.AsSpan(weightAccessor.ByteOffset, sizeof(float)),
                BitConverter.SingleToInt32Bits(weights.X));
        }

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains(diagnostic, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_rig_refuses_polygon_that_crosses_a_texture_page()
    {
        var target = Synthetic(DcmVariant.Dc2);
        var skin = new DcSkin(512, 256, new byte[512 * 256], new ushort[256]);
        var texture = Dc2TexFile.Read(DcTextureEncoder.WriteDc2Tex(skin))!.DecodeSinglePage()!;
        var gltf = GltfInterchange.FromDcm(target, texture);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        uvs[0] = new Vector2(0.75f, uvs[0].Y);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, texture));
        Assert.Contains("texture", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_rig_refuses_uv_outside_decoded_texture_instead_of_clamping()
    {
        var target = Synthetic(DcmVariant.Dc2);
        var skin = new DcSkin(512, 256, new byte[512 * 256], new ushort[256]);
        var texture = Dc2TexFile.Read(DcTextureEncoder.WriteDc2Tex(skin, texY: 256))!.DecodeSinglePage()!;
        var gltf = GltfInterchange.FromDcm(target, texture);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        uvs[0] = new Vector2(-0.0001f, uvs[0].Y);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, texture));
        Assert.Contains("decoded texture", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_rig_refuses_uv_outside_placeholder_page_when_texture_is_unavailable()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        uvs[0] = new Vector2(-0.0001f, uvs[0].Y);

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("texture page", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("joint_1")]
    [InlineData("joint_001")]
    [InlineData("joint_01suffix")]
    [InlineData("JOINT_01")]
    public void Same_rig_requires_canonical_exact_joint_names(string editedName)
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        gltf.LogicalNodes.Single(n => n.Name == "joint_01").Name = editedName;

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("joint_01", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_rig_rejects_a_root_joint_parent_that_looks_like_another_joint()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        gltf = RewriteGlbJson(gltf, json =>
        {
            var nodes = json["nodes"]!.AsArray();
            int rootIndex = nodes.Select((n, i) => (n, i)).Single(x => (string?)x.n!["name"] == "joint_00").i;
            int parentIndex = nodes.Count;
            nodes.Add(new JsonObject
            {
                ["name"] = "joint_99",
                ["children"] = new JsonArray(rootIndex),
            });
            foreach (var scene in json["scenes"]!.AsArray())
            {
                var roots = scene!["nodes"]!.AsArray();
                for (int i = roots.Count - 1; i >= 0; i--)
                    if ((int)roots[i]! == rootIndex) roots.RemoveAt(i);
                roots.Add(parentIndex);
            }
        });

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_rig_rejects_non_identity_mesh_node_transform()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        gltf = RewriteGlbJson(gltf, json =>
        {
            var meshNode = json["nodes"]!.AsArray().Single(
                n => n!["mesh"] is not null && n["skin"] is not null)!;
            meshNode["translation"] = new JsonArray(1, 0, 0);
        });

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains("mesh", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transform", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Same_rig_requires_a_finite_positive_scale(float scale)
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => GltfImporter.ReadSameRig(gltf, target, null, scale));
        Assert.Equal("scale", ex.ParamName);
    }

    [Theory]
    [InlineData("POSITION", float.NaN)]
    [InlineData("POSITION", float.PositiveInfinity)]
    [InlineData("NORMAL", float.NaN)]
    [InlineData("NORMAL", float.NegativeInfinity)]
    [InlineData("TEXCOORD_0", float.NaN)]
    [InlineData("TEXCOORD_0", float.PositiveInfinity)]
    public void Same_rig_rejects_non_finite_geometry(string semantic, float value)
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var accessor = primitive.GetVertexAccessor(semantic)!;
        BinaryPrimitives.WriteInt32LittleEndian(
            accessor.SourceBufferView.Content.AsSpan(accessor.ByteOffset, sizeof(float)),
            BitConverter.SingleToInt32Bits(value));

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains(semantic, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NORMAL")]
    [InlineData("TEXCOORD_0")]
    public void Same_rig_requires_normal_and_uv_accessors(string semantic)
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        gltf = RewriteGlbJson(gltf, json =>
        {
            var attributes = json["meshes"]![0]!["primitives"]![0]!["attributes"]!.AsObject();
            Assert.True(attributes.Remove(semantic));
        });

        var ex = Assert.Throws<InvalidDataException>(() => GltfImporter.ReadSameRig(gltf, target, null));
        Assert.Contains(semantic, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_rig_keeps_coincident_vertices_with_distinct_normals_separate()
    {
        var target = Synthetic(DcmVariant.Dc1);
        var gltf = GltfInterchange.FromDcm(target);
        var primitive = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        var positions = primitive.GetVertexAccessor("POSITION")!.AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL")!.AsVector3Array();
        var joints = primitive.GetVertexAccessor("JOINTS_0")!.AsVector4Array();
        var weights = primitive.GetVertexAccessor("WEIGHTS_0")!.AsVector4Array();
        positions[1] = positions[0];
        joints[1] = joints[0];
        weights[1] = weights[0];
        normals[0] = Vector3.UnitX;
        normals[1] = Vector3.UnitY;

        var imported = GltfImporter.ReadSameRig(gltf, target, null);

        var coincident = imported.Vertices.Where(v =>
            v.X == imported.Vertices[0].X && v.Y == imported.Vertices[0].Y &&
            v.Z == imported.Vertices[0].Z && v.Joint == imported.Vertices[0].Joint).ToArray();
        Assert.Contains(coincident, v => (v.NX, v.NY, v.NZ) == (4096, 0, 0));
        Assert.Contains(coincident, v => (v.NX, v.NY, v.NZ) == (0, -4096, 0));
    }

    private static ModelRoot RewriteGlbJson(ModelRoot gltf, Action<JsonObject> edit)
    {
        byte[] source = gltf.WriteGLB().ToArray();
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(12, 4));
        int binOffset = 20 + jsonLength;
        var json = JsonNode.Parse(source.AsSpan(20, jsonLength))!.AsObject();
        edit(json);

        byte[] encoded = Encoding.UTF8.GetBytes(json.ToJsonString(
            new JsonSerializerOptions { WriteIndented = false }));
        int paddedLength = (encoded.Length + 3) & ~3;
        byte[] rewritten = new byte[20 + paddedLength + source.Length - binOffset];
        source.AsSpan(0, 12).CopyTo(rewritten);
        BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(8, 4), rewritten.Length);
        BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(12, 4), paddedLength);
        source.AsSpan(16, 4).CopyTo(rewritten.AsSpan(16));
        encoded.CopyTo(rewritten.AsSpan(20));
        rewritten.AsSpan(20 + encoded.Length, paddedLength - encoded.Length).Fill((byte)' ');
        source.AsSpan(binOffset).CopyTo(rewritten.AsSpan(20 + paddedLength));
        return ModelRoot.ParseGLB(new ArraySegment<byte>(rewritten));
    }
}
