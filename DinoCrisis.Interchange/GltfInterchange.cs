using System.Numerics;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Textures;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;

namespace DinoCrisis.Interchange;

/// <summary>
/// Exports a <see cref="DcmModel"/> to skinned glTF 2.0 (GLB) — the toolkit's Blender interchange
/// (DC-MODEL-TOOLKIT-PLAN P1). The rig is preserved as a joint hierarchy (one glTF node per DCM
/// part, named <c>joint_NN</c> — the P2 same-rig import contract key), vertices are skinned rigidly
/// (single joint, weight 1.0 — DCM's own binding class), joints carry translation-only TRS (the
/// DCM rest pose is translation FK), and the decoded skin embeds as a PNG.
///
/// <para><b>Orientation</b> follows the shipped <see cref="ObjExporter"/> precedent: PSX is Y-down,
/// so <c>flipY</c> (default) negates Y. Native DCM face order is opposite its custom-normal
/// convention, so that reflection supplies the handedness change without an additional index
/// reversal. <b>Scale</b> divides raw PSX units by
/// 1000 (Regina ≈ 1727 units → 1.73 glTF meters); the P2 importer multiplies back — exact for
/// int16 sources. <b>Quads</b> are PSX strip-ordered (v0,v1 / v2,v3 — the same convention RE MD1
/// uses, witness: biohazard-utils' ObjExporter emits quads as v2,v3,v1,v0) and triangulate as
/// (0,1,2)+(1,3,2).</para>
/// </summary>
public static class GltfInterchange
{
    /// <summary>Raw PSX units per glTF unit: 1000 (≈ millimeters → meters).</summary>
    public const float DefaultScale = 0.001f;

    /// <summary>The P2 import-contract joint name for part <paramref name="index"/>.</summary>
    public static string JointName(int index) => $"joint_{index:D2}";

    /// <summary>Convert a parsed model (+ optionally its decoded texture) to a glTF DOM.</summary>
    public static SharpGLTF.Schema2.ModelRoot FromDcm(
        DcmModel model, DcmTexture? tex = null,
        float scale = DefaultScale, bool flipY = true, string name = "dcm")
    {
        ArgumentNullException.ThrowIfNull(model);

        // Joints: one node per part, translation-only local TRS = the parent-relative rest offset.
        var joints = new NodeBuilder[model.Parts.Count];
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var p = model.Parts[i];
            joints[i] = p.Parent >= 0 && p.Parent < i
                ? joints[p.Parent].CreateNode(JointName(i))
                : new NodeBuilder(JointName(i));
            joints[i].UseTranslation().Value = Pos(p.X, p.Y, p.Z, scale, flipY);
        }

        var material = BuildMaterial(tex);
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4>(name);
        var prim = mesh.UsePrimitive(material);

        // Vertices are bone-local; bake FK world rest positions (same assembly as ObjExporter) so
        // the bind pose is the world pose and SharpGLTF derives inverse bind matrices from the
        // joints' world transforms.
        var world = ObjExporter.ComputeWorldPositions(model);
        bool dc2 = model.Variant.Game == DcmGame.Dc2;

        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                ushort tpage = dc2 ? model.Header.TPage : tri.TPage;
                var a = Corner(model, tri.V0, tri.Uv0, tpage, world, pi, tex, scale, flipY);
                var b = Corner(model, tri.V1, tri.Uv1, tpage, world, pi, tex, scale, flipY);
                var c = Corner(model, tri.V2, tri.Uv2, tpage, world, pi, tex, scale, flipY);
                AddTri(prim, a, b, c);
            }
            triCursor += part.TriangleCount;

            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                ushort tpage = dc2 ? model.Header.TPage : qd.TPage;
                var a = Corner(model, qd.V0, qd.Uv0, tpage, world, pi, tex, scale, flipY);
                var b = Corner(model, qd.V1, qd.Uv1, tpage, world, pi, tex, scale, flipY);
                var c = Corner(model, qd.V2, qd.Uv2, tpage, world, pi, tex, scale, flipY);
                var d = Corner(model, qd.V3, qd.Uv3, tpage, world, pi, tex, scale, flipY);
                // PSX strip order: the quad's two triangles are (v0,v1,v2) and (v1,v3,v2).
                AddTri(prim, a, b, c);
                AddTri(prim, b, d, c);
            }
            quadCursor += part.QuadCount;
        }

        var scene = new SceneBuilder(name);
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, joints);
        return scene.ToGltf2();
    }

    /// <summary>
    /// Resolve the model's texture the same way the textured OBJ export does (DC1: from its own
    /// package bytes; DC2: from the sibling <c>E*.TEX</c> bytes) and write a GLB. Returns whether
    /// the output is textured (false → untextured GLB written, e.g. DC2 without its .TEX).
    /// </summary>
    public static bool ExportGlb(byte[] fileBytes, DcmModel model, string outPath,
                                 float scale = DefaultScale, bool flipY = true, byte[]? texFileBytes = null)
    {
        DcmTexture? tex;
        if (model.Variant.Game == DcmGame.Dc2)
        {
            tex = texFileBytes is null ? null : DcmTextureDecoder.TryDecodeDc2(texFileBytes, model);
        }
        else
        {
            var pkg = GianPackage.TryParse(fileBytes);
            tex = pkg is null ? null : DcmTextureDecoder.TryDecode(pkg, fileBytes, model);
        }
        FromDcm(model, tex, scale, flipY, Path.GetFileNameWithoutExtension(outPath)).SaveGLB(outPath);
        return tex is not null;
    }

    private static MaterialBuilder BuildMaterial(DcmTexture? tex)
    {
        // Fully rough and single-sided: PSX-era geometry has no PBR intent, and the native
        // renderer rejects backfaces rather than lighting both sides.
        var material = new MaterialBuilder("dcm")
            .WithMetallicRoughnessShader()
            .WithMetallicRoughness(0, 1);
        if (tex is not null)
        {
            material = material.WithChannelImage(
                KnownChannel.BaseColor, ImageBuilder.From(new MemoryImage(PngEncoder.Encode(tex.Image)), "skin"));
            var texture = material.GetChannel(KnownChannel.BaseColor).Texture;
            texture.WrapS = TextureWrapMode.CLAMP_TO_EDGE;
            texture.WrapT = TextureWrapMode.CLAMP_TO_EDGE;
            texture.MinFilter = TextureMipMapFilter.NEAREST;
            texture.MagFilter = TextureInterpolationFilter.NEAREST;
        }
        return material;
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4> Corner(
        DcmModel model, int vertexIndex, Uv uv, ushort tpage,
        (int X, int Y, int Z)[] world, int polygonPart, DcmTexture? tex, float scale, bool flipY)
    {
        var v = model.Vertices[vertexIndex];
        int vertexPart = ObjExporter.ResolveVertexTransformPart(model, vertexIndex, polygonPart);
        var bindingWorld = world[vertexPart];
        var pos = Pos(v.X + bindingWorld.X, v.Y + bindingWorld.Y, v.Z + bindingWorld.Z, scale, flipY);
        var nrm = UnitNormal(model.Normals[vertexIndex], flipY);

        Vector2 st;
        if (tex is not null)
        {
            // MapUv returns OBJ coords (V bottom-up); glTF V is top-down → undo the flip.
            var (s, t) = tex.MapUv(uv.U, uv.V, tpage);
            st = new Vector2((float)s, (float)(1.0 - t));
        }
        else
        {
            st = new Vector2(uv.U / 256f, uv.V / 256f);
        }

        // Reserved round-trip metadata: polygon owner + exact stock pool index.
        var owner = new Vector2(polygonPart, vertexIndex);
        return new(new VertexPositionNormal(pos, nrm), new VertexTexture2(st, owner), new VertexJoints4(vertexPart));
    }

    private static void AddTri(
        IPrimitiveBuilder prim,
        VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4> a,
        VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4> b,
        VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4> c)
    {
        prim.AddTriangle(a, b, c);
    }

    private static Vector3 Pos(int x, int y, int z, float scale, bool flipY)
        => new(x * scale, (flipY ? -y : y) * scale, z * scale);

    private static Vector3 UnitNormal(SVector n, bool flipY)
    {
        var v = new Vector3(n.X, flipY ? -n.Y : n.Y, n.Z);
        float len = v.Length();
        // glTF requires unit normals; a degenerate zero normal gets an arbitrary up (rare, cosmetic).
        return len < 1e-6f ? Vector3.UnitY : v / len;
    }
}
