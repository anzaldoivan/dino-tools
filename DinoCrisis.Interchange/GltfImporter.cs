using System.Numerics;
using DinoCrisis.Model;
using DinoCrisis.Model.Textures;
using SharpGLTF.Schema2;

namespace DinoCrisis.Interchange;

/// <summary>A mesh read back from an edited glTF, in DC world units (the target model's own frame),
/// ready for a same-rig splice. Positions are world rest-pose ints (bone-local conversion is the
/// splicer's job, since it owns the target FK); triangles carry DC1-ready per-poly texture codes.</summary>
public sealed class ImportedMesh
{
    public sealed class Vertex
    {
        public int X, Y, Z;
        /// <summary>PSX fixed-point normal (ONE = 4096).</summary>
        public short NX, NY, NZ;
        /// <summary>Target part (bone) index — the vertex's dominant glTF joint.</summary>
        public short Joint;
        /// <summary>Exact stock pool index from reserved TEXCOORD_1 metadata, or -1 for legacy/new vertices.</summary>
        public int StockIndex = -1;
    }

    public sealed class Triangle
    {
        public int V0, V1, V2;
        /// <summary>Original DCM polygon-part slice, or -1 for legacy inputs without metadata.</summary>
        public int Part = -1;
        public Uv Uv0, Uv1, Uv2;
        public ushort TPage;
        public ushort Clut;
    }

    /// <summary>A PSX strip-order quad (v0,v1 / v2,v3) — produced by <see cref="QuadMerge"/>, since
    /// glTF itself is triangle-only.</summary>
    public sealed class Quad
    {
        public int V0, V1, V2, V3;
        /// <summary>Original DCM polygon-part slice, or -1 for legacy inputs without metadata.</summary>
        public int Part = -1;
        public Uv Uv0, Uv1, Uv2, Uv3;
        public ushort TPage;
        public ushort Clut;
    }

    public List<Vertex> Vertices { get; } = new();
    public List<Triangle> Triangles { get; } = new();
    public List<Quad> Quads { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads an edited glTF/GLB back into DC form under the <b>same-rig contract</b>
/// (DC-MODEL-TOOLKIT-PLAN D4): the file must contain exactly one skin whose joints are named
/// <c>joint_00..joint_NN</c> matching the target model's part count and parent hierarchy; mesh
/// topology and UVs may be edited within the target splicer's native budgets; positions and
/// canonicalized normal directions must still map to stock records unless the DC2 caller selects
/// its explicit unsafe moved/new-vertex path. Anything that changes the rig is refused with a
/// diagnostic. Consumption is <b>semantic</b> — joints are matched by name and data comes from
/// accessors — because Blender's exporter may normalize node order/structure.
///
/// <para>Inverts the <see cref="GltfInterchange"/> export exactly: ÷scale becomes ×1/scale with
/// round-to-nearest (exact for int16 sources), flipY un-negates Y while preserving the DCM index
/// order emitted by the exporter, and
/// normalized UVs map back to PSX texel (u, v) + a reconstructed per-poly tpage via the target's
/// decoded texture (any (u,v,tpage) encoding that addresses the same texel is valid — the importer
/// re-derives page columns rather than trying to reproduce the original bit pattern).</para>
/// </summary>
public static class GltfImporter
{
    /// <summary>Read a GLB/glTF file and convert it against the target model. Throws
    /// <see cref="InvalidDataException"/> with an actionable message on a contract violation.</summary>
    public static ImportedMesh ReadSameRig(string gltfPath, DcmModel target, DcmTexture? tex,
                                           float scale = GltfInterchange.DefaultScale, bool flipY = true)
        => ReadSameRig(ModelRoot.Load(gltfPath), target, tex, scale, flipY);

    public static ImportedMesh ReadSameRig(ModelRoot gltf, DcmModel target, DcmTexture? tex,
                                           float scale = GltfInterchange.DefaultScale, bool flipY = true)
    {
        ArgumentNullException.ThrowIfNull(gltf);
        ArgumentNullException.ThrowIfNull(target);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "glTF scale must be finite and positive.");

        // ---- same-rig contract: one skin, joint_NN names covering exactly the target's parts ----
        if (gltf.LogicalSkins.Count != 1)
            throw new InvalidDataException(
                $"same-rig import needs exactly one skin; found {gltf.LogicalSkins.Count}. " +
                "Export the whole armature+mesh from Blender as one skinned object.");
        var skin = gltf.LogicalSkins[0];

        int parts = target.Parts.Count;
        var jointToPart = new int[skin.JointsCount];
        var seen = new bool[parts];
        for (int j = 0; j < skin.JointsCount; j++)
        {
            var node = skin.GetJoint(j).Joint;
            int part = ParseJointName(node.Name);
            if (part < 0 || part >= parts)
                throw new InvalidDataException(
                    $"joint '{node.Name}' is not a target bone (expected joint_00..joint_{parts - 1:D2}). " +
                    "The rig was changed — same-rig import only accepts mesh/UV edits.");
            if (seen[part])
                throw new InvalidDataException($"duplicate joint '{node.Name}'.");
            seen[part] = true;
            jointToPart[j] = part;
        }
        if (skin.JointsCount != parts)
        {
            var missing = Enumerable.Range(0, parts).Where(i => !seen[i]).Select(GltfInterchange.JointName);
            throw new InvalidDataException(
                $"skin has {skin.JointsCount} joints but the target rig has {parts} " +
                $"(missing: {string.Join(", ", missing)}). Same-rig import requires the full skeleton.");
        }

        // Hierarchy must match the target's parent array (a re-parented bone changes deformation
        // under the game's bone-indexed animation). The root's parent is the armature node — skipped.
        var nodeByPart = new Node[parts];
        for (int j = 0; j < skin.JointsCount; j++) nodeByPart[jointToPart[j]] = skin.GetJoint(j).Joint;
        for (int i = 0; i < parts; i++)
        {
            int expectedParent = target.Parts[i].Parent;
            var actualParent = nodeByPart[i].VisualParent;
            if (expectedParent < 0)
            {
                if (actualParent is not null && ParseJointName(actualParent.Name) >= 0)
                    throw new InvalidDataException(
                        $"root joint '{GltfInterchange.JointName(i)}' is parented to " +
                        $"'{actualParent.Name}'; a target root cannot have another joint as its parent.");
                continue;
            }
            if (actualParent is null || ParseJointName(actualParent.Name) != expectedParent)
                throw new InvalidDataException(
                    $"joint '{GltfInterchange.JointName(i)}' is parented to " +
                    $"'{actualParent?.Name ?? "<none>"}' but the target rig expects " +
                    $"'{GltfInterchange.JointName(expectedParent)}'. The hierarchy was edited — refused.");
        }

        // The game's animation addresses this exact rest rig. Names and parents alone are not
        // sufficient: edited TRS or inverse binds can silently deform an otherwise valid mesh.
        const float matrixTolerance = 0.00001f;
        var targetWorld = ObjExporter.ComputeWorldPositions(target);
        for (int i = 0; i < parts; i++)
        {
            var p = target.Parts[i];
            var expectedLocal = Matrix4x4.CreateTranslation(
                p.X * scale, p.Y * scale * (flipY ? -1 : 1), p.Z * scale);
            if (!MatrixClose(nodeByPart[i].LocalMatrix, expectedLocal, matrixTolerance))
                throw new InvalidDataException(
                    $"joint '{GltfInterchange.JointName(i)}' rest transform was edited; " +
                    "same-rig import requires the target translation with identity rotation and scale.");
        }

        if (skin.InverseBindMatrices.Count != parts)
            throw new InvalidDataException(
                $"skin has {skin.InverseBindMatrices.Count} inverse bind matrices but the target rig has {parts} joints.");
        for (int j = 0; j < skin.JointsCount; j++)
        {
            int part = jointToPart[j];
            var w = targetWorld[part];
            var expectedWorld = Matrix4x4.CreateTranslation(
                w.X * scale, w.Y * scale * (flipY ? -1 : 1), w.Z * scale);
            if (!Matrix4x4.Invert(expectedWorld, out var expectedInverse) ||
                !MatrixClose(skin.InverseBindMatrices[j], expectedInverse, matrixTolerance))
                throw new InvalidDataException(
                    $"joint '{GltfInterchange.JointName(part)}' inverse bind matrix does not match the target rest pose.");
        }

        // ---- gather every triangle primitive of every mesh bound to this skin ----
        var result = new ImportedMesh();
        var dedup = new Dictionary<(int X, int Y, int Z, short Joint, short NX, short NY, short NZ, int Stock), int>();
        bool sawPrimitive = false;
        // DC1: only ever emit tpage values the target model already uses — the PC port's renderer
        // may key texture lookup by the tpage word, so a GPU-equivalent alternative encoding
        // (pages overlap: 256 texels vs 128-texel column steps) can still miss its texture.
        var stockTpages = target.Variant.Game == DcmGame.Dc2
            ? new[] { target.Header.TPage }
            : target.Triangles.Select(t => t.TPage).Concat(target.Quads.Select(q => q.TPage))
                    .Distinct().OrderBy(t => t).ToArray();

        foreach (var node in gltf.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin != skin) continue;
            if (!MatrixClose(node.WorldMatrix, Matrix4x4.Identity, matrixTolerance))
                throw new InvalidDataException(
                    $"skinned mesh node '{node.Name ?? "<unnamed>"}' has a non-identity transform; " +
                    "apply mesh transforms in Blender before export.");
            foreach (var prim in node.Mesh.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                    throw new InvalidDataException(
                        $"primitive mode {prim.DrawPrimitiveType} unsupported — triangles only.");
                sawPrimitive = true;

                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array()
                    ?? throw new InvalidDataException("primitive has no POSITION accessor.");
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array()
                    ?? throw new InvalidDataException("primitive has no NORMAL accessor.");
                var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array()
                    ?? throw new InvalidDataException("primitive has no TEXCOORD_0 accessor.");
                var polygonParts = prim.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array()
                    ?? throw new InvalidDataException(
                        "primitive is not skinned (no JOINTS_0) — export with the armature.");
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array()
                    ?? throw new InvalidDataException("primitive has JOINTS_0 but no WEIGHTS_0.");
                if (joints.Count != positions.Count || weights.Count != positions.Count)
                    throw new InvalidDataException(
                        $"skinning accessor count mismatch: {positions.Count} positions, {joints.Count} joints, " +
                        $"{weights.Count} weights.");
                if (normals.Count != positions.Count)
                    throw new InvalidDataException(
                        $"NORMAL accessor has {normals.Count} values for {positions.Count} positions.");
                if (uvs.Count != positions.Count)
                    throw new InvalidDataException(
                        $"TEXCOORD_0 accessor has {uvs.Count} values for {positions.Count} positions.");
                if (polygonParts is not null && polygonParts.Count != positions.Count)
                    throw new InvalidDataException(
                        $"reserved TEXCOORD_1 polygon-part accessor has {polygonParts.Count} values for " +
                        $"{positions.Count} positions.");

                // Per-primitive corner → pool vertex (deduped by position+joint; DCM has no
                // per-vertex UV, so UV seams collapse back and the count returns to DC scale).
                var toPool = new int[positions.Count];
                for (int v = 0; v < positions.Count; v++)
                {
                    var p = positions[v];
                    if (!VectorFinite(p))
                        throw new InvalidDataException($"vertex {v} POSITION values must be finite.");
                    var n = normals[v];
                    if (!VectorFinite(n))
                        throw new InvalidDataException($"vertex {v} NORMAL values must be finite.");
                    int x = Round(p.X / scale);
                    int y = Round(p.Y / scale) * (flipY ? -1 : 1);
                    int z = Round(p.Z / scale);
                    short joint = (short)jointToPart[RigidJoint(joints[v], weights[v], skin.JointsCount, v)];
                    short nx = Fx(n.X), ny = Fx(flipY ? -n.Y : n.Y), nz = Fx(n.Z);
                    int stockIndex = polygonParts is null ? -1 : StockIndex(polygonParts[v], target.Vertices.Count);

                    if (!dedup.TryGetValue((x, y, z, joint, nx, ny, nz, stockIndex), out int pool))
                    {
                        pool = result.Vertices.Count;
                        result.Vertices.Add(new ImportedMesh.Vertex
                        {
                            X = x, Y = y, Z = z, Joint = joint,
                            NX = nx, NY = ny, NZ = nz, StockIndex = stockIndex,
                        });
                        dedup[(x, y, z, joint, nx, ny, nz, stockIndex)] = pool;
                    }
                    toPool[v] = pool;
                }

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    var (v0, v1, v2) = (a, b, c);
                    var tri = new ImportedMesh.Triangle
                    {
                        V0 = toPool[v0], V1 = toPool[v1], V2 = toPool[v2],
                        Part = polygonParts is null ? -1
                            : PolygonPart(polygonParts[v0], polygonParts[v1], polygonParts[v2], parts),
                    };
                    var uv0 = uvs[v0];
                    var uv1 = uvs[v1];
                    var uv2 = uvs[v2];
                    InvertUvs(tex, target, stockTpages, uv0, uv1, uv2, tri);
                    result.Triangles.Add(tri);
                }
            }
        }

        if (!sawPrimitive)
            throw new InvalidDataException("no mesh is bound to the skin — nothing to import.");
        // Reconstitute PSX strip quads (glTF split them into triangle pairs) — render-exact and
        // required for DC2's in-place geometry budget.
        QuadMerge.Merge(result);
        return result;
    }

    private static int ParseJointName(string? name)
    {
        if (name is null || !name.StartsWith("joint_", StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(6), out int i))
            return -1;
        return name == GltfInterchange.JointName(i) ? i : -1;
    }

    private static int Round(float v) => (int)MathF.Round(v);

    private static int PolygonPart(Vector2 a, Vector2 b, Vector2 c, int partCount)
    {
        static int Decode(Vector2 value, int count)
        {
            if (!VectorFinite(value))
                throw new InvalidDataException("reserved TEXCOORD_1 polygon-part values must be finite.");
            int part = Round(value.X);
            const float tolerance = 0.001f;
            if (MathF.Abs(value.X - part) > tolerance || part < 0 || part >= count)
                throw new InvalidDataException(
                    $"reserved TEXCOORD_1 polygon-part value {value.X} must encode one integer part " +
                    $"in 0..{count - 1}; do not edit the secondary UV map.");
            return part;
        }

        int pa = Decode(a, partCount), pb = Decode(b, partCount), pc = Decode(c, partCount);
        if (pa != pb || pa != pc)
            throw new InvalidDataException(
                "triangle corners disagree in reserved TEXCOORD_1 polygon-part metadata; " +
                "do not edit the secondary UV map.");
        return pa;
    }

    private static int StockIndex(Vector2 value, int vertexCount)
    {
        if (!VectorFinite(value)) return -1;
        int index = Round(value.Y);
        return MathF.Abs(value.Y - index) <= 0.001f && index >= 0 && index < vertexCount ? index : -1;
    }

    private static short Fx(float unit) => (short)Math.Clamp(MathF.Round(unit * 4096f), short.MinValue, short.MaxValue);

    private static bool VectorFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool VectorFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static int RigidJoint(Vector4 joints, Vector4 weights, int jointCount, int vertex)
    {
        Span<float> js = stackalloc float[4] { joints.X, joints.Y, joints.Z, joints.W };
        Span<float> ws = stackalloc float[4] { weights.X, weights.Y, weights.Z, weights.W };
        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(js[i]) || !float.IsFinite(ws[i]))
                throw new InvalidDataException($"vertex {vertex} skin joints/weights must be finite.");
            if (js[i] != MathF.Truncate(js[i]) || js[i] < 0 || js[i] >= jointCount)
                throw new InvalidDataException(
                    $"vertex {vertex} joint index {js[i]} is outside the skin range 0..{jointCount - 1}.");
            if (ws[i] < 0 || ws[i] > 1)
                throw new InvalidDataException($"vertex {vertex} skin weight {ws[i]} is outside 0..1.");
        }

        const float tolerance = 0.0001f;
        float sum = ws[0] + ws[1] + ws[2] + ws[3];
        if (MathF.Abs(sum - 1) > tolerance)
            throw new InvalidDataException(
                $"vertex {vertex} skin weights sum to {sum}; normalized weights must sum to 1.");

        int active = -1;
        for (int i = 0; i < 4; i++)
        {
            if (ws[i] <= tolerance) continue;
            if (active >= 0)
                throw new InvalidDataException(
                    $"vertex {vertex} has non-rigid skin weights; exactly one joint must have weight 1.");
            active = i;
        }
        if (active < 0 || MathF.Abs(ws[active] - 1) > tolerance)
            throw new InvalidDataException(
                $"vertex {vertex} has non-rigid skin weights; exactly one joint must have weight 1.");
        return (int)js[active];
    }

    private static bool MatrixClose(Matrix4x4 a, Matrix4x4 b, float tolerance) =>
        MathF.Abs(a.M11 - b.M11) <= tolerance && MathF.Abs(a.M12 - b.M12) <= tolerance &&
        MathF.Abs(a.M13 - b.M13) <= tolerance && MathF.Abs(a.M14 - b.M14) <= tolerance &&
        MathF.Abs(a.M21 - b.M21) <= tolerance && MathF.Abs(a.M22 - b.M22) <= tolerance &&
        MathF.Abs(a.M23 - b.M23) <= tolerance && MathF.Abs(a.M24 - b.M24) <= tolerance &&
        MathF.Abs(a.M31 - b.M31) <= tolerance && MathF.Abs(a.M32 - b.M32) <= tolerance &&
        MathF.Abs(a.M33 - b.M33) <= tolerance && MathF.Abs(a.M34 - b.M34) <= tolerance &&
        MathF.Abs(a.M41 - b.M41) <= tolerance && MathF.Abs(a.M42 - b.M42) <= tolerance &&
        MathF.Abs(a.M43 - b.M43) <= tolerance && MathF.Abs(a.M44 - b.M44) <= tolerance;

    /// <summary>Map three normalized glTF UVs (V top-down, over the decoded skin image) back to PSX
    /// texel bytes + ONE per-poly tpage <b>chosen from the target model's own tpage set</b> — the
    /// PC renderer must only ever see codes the stock model already renders. A polygon whose corners
    /// fit none of the stock pages is refused. The CLUT comes from the target's codes (the skin/palette
    /// are unchanged by a mesh edit).</summary>
    private static void InvertUvs(DcmTexture? tex, DcmModel target, ushort[] stockTpages,
                                  Vector2 a, Vector2 b, Vector2 c,
                                  ImportedMesh.Triangle tri)
    {
        (ushort repTPage, ushort repClut) = RepresentativeCodes(target);

        if (tex is null)
        {
            // No texture context: undo the exporter's u/256 placeholder.
            static Uv PlaceholderUv(Vector2 uv)
            {
                if (!VectorFinite(uv))
                    throw new InvalidDataException("polygon TEXCOORD_0 values must be finite.");
                float rawU = uv.X * 256f, rawV = uv.Y * 256f;
                if (rawU is < 0 or > 255 || rawV is < 0 or > 255)
                    throw new InvalidDataException(
                        "polygon UVs leave the single placeholder texture page; keep each corner inside 0..255.");
                int u = Round(rawU), v = Round(rawV);
                return new Uv((byte)u, (byte)v);
            }
            tri.Uv0 = PlaceholderUv(a);
            tri.Uv1 = PlaceholderUv(b);
            tri.Uv2 = PlaceholderUv(c);
            tri.TPage = repTPage;
            tri.Clut = repClut;
            return;
        }

        int w = tex.Image.Width, h = tex.Image.Height;
        int pageTexels = 64 * tex.TexelsPerWord;               // texels per tpage COLUMN step (128 @8bpp)

        // Absolute VRAM texel column / row per corner.
        Span<int> col = stackalloc int[3];
        Span<int> row = stackalloc int[3];
        Span<Vector2> uv = stackalloc Vector2[3] { a, b, c };
        for (int k = 0; k < 3; k++)
        {
            if (!VectorFinite(uv[k]))
                throw new InvalidDataException("polygon TEXCOORD_0 values must be finite.");
            float rawImageCol = uv[k].X * w, rawImageRow = uv[k].Y * h;
            if (rawImageCol < 0 || rawImageCol >= w || rawImageRow < 0 || rawImageRow >= h)
                throw new InvalidDataException(
                    "polygon UVs leave the decoded texture; keep every corner inside its image bounds.");
            int imageCol = Round(rawImageCol), imageRow = Round(rawImageRow);
            if (imageCol is < 0 || imageCol >= w || imageRow is < 0 || imageRow >= h)
                throw new InvalidDataException(
                    "polygon UVs leave the decoded texture; keep every corner inside its image bounds.");
            col[k] = tex.EntryX16 * tex.TexelsPerWord + imageCol;
            row[k] = tex.EntryY + imageRow;
        }

        // Pick the first stock tpage whose 256×256 window contains all three corners.
        ushort chosen = 0;
        bool fits = false;
        foreach (ushort tp in stockTpages)
        {
            int baseCol = (tp & 0xF) * pageTexels, baseRow = ((tp >> 4) & 1) * 256;
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++)
                ok = col[k] - baseCol is >= 0 and <= 255 && row[k] - baseRow is >= 0 and <= 255;
            if (ok) { chosen = tp; fits = true; break; }
        }
        if (!fits)
        {
            throw new InvalidDataException(
                "polygon UVs cross or leave every stock texture page; keep each polygon's UV island " +
                "inside one 256x256 page window.");
        }

        int pcol = (chosen & 0xF) * pageTexels, prow = ((chosen >> 4) & 1) * 256;
        tri.Uv0 = new Uv((byte)(col[0] - pcol), (byte)(row[0] - prow));
        tri.Uv1 = new Uv((byte)(col[1] - pcol), (byte)(row[1] - prow));
        tri.Uv2 = new Uv((byte)(col[2] - pcol), (byte)(row[2] - prow));
        tri.TPage = chosen;
        tri.Clut = repClut;
    }

    private static (ushort TPage, ushort Clut) RepresentativeCodes(DcmModel target)
    {
        if (target.Variant.Game == DcmGame.Dc2)
            return (target.Header.TPage, target.Header.Clut);
        var t = target.Triangles.FirstOrDefault();
        if (t is not null) return (t.TPage, t.Clut);
        var q = target.Quads.FirstOrDefault();
        return q is not null ? (q.TPage, q.Clut) : ((ushort)0, (ushort)0);
    }

}
