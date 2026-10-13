using System.Globalization;
using System.Linq;
using System.Text;

namespace DinoCrisis.Model;

/// <summary>Maps a polygon corner (texel <c>u</c>/<c>v</c> under page <c>tpage</c>) to OBJ texture
/// coordinates. The default emits raw normalized UVs; a textured export supplies a real mapper.</summary>
public delegate (double S, double T) UvMapper(byte u, byte v, ushort tpage);

/// <summary>
/// Exports a <see cref="DcmModel"/> to Wavefront OBJ for viewing in Blender/Noesis. Concept adapted
/// from ref/biohazard-utils' ObjExporter (own code; OBJ is a standard format).
///
/// <para><b>Assembly.</b> DCM vertices are <b>bone-local</b> (verified against real models): each part's
/// geometry is stored relative to its bone, so the exporter places each part's polygons at the bone's
/// <b>world</b> rest position — the forward-kinematic sum of the relative <c>xyz</c> up the parent chain
/// (the DC analog of EMR <c>GetFinalPosition</c>). Binding is rigid (no weights), so a polygon's
    /// DC1 corner takes the transform run identified by its referenced vertex's pad label, while DC2
    /// corners take the owning polygon part. Seam vertices shared between adjacent parts are emitted
    /// once per polygon part but retain that game-specific transform. This is the model's <b>rest
    /// pose</b> (no animation applied).</para>
///
/// <para><b>Orientation.</b> PSX is Y-down; with <c>flipY</c> (default) the exporter negates Y to stand
/// the model upright for OBJ's Y-up convention and reverses polygon winding so normals stay outward.</para>
/// </summary>
public static class ObjExporter
{
    /// <summary>Export to an OBJ string (placeholder UVs, no material).</summary>
    public static string Export(DcmModel model, bool flipY = true)
        => BuildObj(model, flipY, DefaultUv, mtlLib: null, material: null);

    /// <summary>Export to an OBJ file.</summary>
    public static void Export(DcmModel model, string objPath, bool flipY = true)
        => File.WriteAllText(objPath, Export(model, flipY));

    private static (double, double) DefaultUv(byte u, byte v, ushort tpage) => (u / 256.0, v / 256.0);

    /// <summary>Build OBJ text with a custom UV mapper and optional material reference (used by the
    /// textured exporter). Public so a caller can supply its own UV source.</summary>
    public static string BuildObj(DcmModel model, bool flipY, UvMapper uv, string? mtlLib, string? material)
    {
        var sb = new StringBuilder();
        sb.Append("# Dino Crisis DCM model — exported by DinoCrisis.Model\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"# {model.Variant.Game} | {model.Parts.Count} parts | {model.Triangles.Count} tris | {model.Quads.Count} quads | rest pose\n");
        if (mtlLib is not null) sb.Append(CultureInfo.InvariantCulture, $"mtllib {mtlLib}\n");
        if (material is not null) sb.Append(CultureInfo.InvariantCulture, $"usemtl {material}\n");

        var world = ComputeWorldPositions(model);
        int vCount = 0, vtCount = 0;
        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            var used = new SortedSet<int>();
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                used.Add(tri.V0); used.Add(tri.V1); used.Add(tri.V2);
            }
            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                used.Add(qd.V0); used.Add(qd.V1); used.Add(qd.V2); used.Add(qd.V3);
            }

            var remap = new Dictionary<int, int>(used.Count);
            foreach (int idx in used)
            {
                var vtx = model.Vertices[idx];
                int vertexPart = ResolveVertexTransformPart(model, idx, pi);
                var (wx, wy, wz) = world[vertexPart];
                int x = vtx.X + wx, y = vtx.Y + wy, z = vtx.Z + wz;
                if (flipY) y = -y;
                sb.Append(CultureInfo.InvariantCulture, $"v {x} {y} {z}\n");
                // Normals are per-vertex (the normal pool is parallel to the vertex pool — equal counts,
                // and DCM polygons carry no separate normal index; verified §7). Emit one vn per emitted
                // vertex, in lockstep, so its OBJ index equals the position index.
                var (nx, ny, nz) = UnitNormal(model.Normals[idx], flipY);
                sb.Append(CultureInfo.InvariantCulture, $"vn {nx} {ny} {nz}\n");
                remap[idx] = ++vCount;
            }

            for (int t = 0; t < part.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                EmitFace(sb, remap, new[] { tri.V0, tri.V1, tri.V2 },
                         new[] { tri.Uv0, tri.Uv1, tri.Uv2 }, tri.TPage, uv, ref vtCount, reverse: flipY);
            }
            triCursor += part.TriangleCount;

            for (int q = 0; q < part.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                EmitFace(sb, remap, new[] { qd.V0, qd.V1, qd.V2, qd.V3 },
                         new[] { qd.Uv0, qd.Uv1, qd.Uv2, qd.Uv3 }, qd.TPage, uv, ref vtCount, reverse: flipY);
            }
            quadCursor += part.QuadCount;
        }
        return sb.ToString();
    }

    /// <summary>Resolve the rigid rest-pose transform for a polygon corner. DC1's renderer consumes
    /// positional transform runs labelled by the referenced vertex pad; DC2 geometry is local to its
    /// polygon part. A referenced invalid DC1 label is refused instead of guessed.</summary>
    public static int ResolveVertexTransformPart(DcmModel model, int vertexIndex, int polygonPart)
    {
        if (model.Variant.Game == DcmGame.Dc2) return polygonPart;

        int vertexPart = model.Vertices[vertexIndex].Pad;
        if (vertexPart < 0 || vertexPart >= model.Parts.Count)
            throw new InvalidDataException(
                $"DC1 polygon part {polygonPart} references vertex {vertexIndex} with invalid " +
                $"transform-run label {vertexPart}; expected 0..{model.Parts.Count - 1}.");
        return vertexPart;
    }

    /// <summary>Forward-kinematic world rest position of each part (sum of relative xyz up the parent
    /// chain). Exposed because it is also what a future scaler / transplant needs.</summary>
    public static (int X, int Y, int Z)[] ComputeWorldPositions(DcmModel model)
    {
        var world = new (int X, int Y, int Z)[model.Parts.Count];
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var p = model.Parts[i];
            world[i] = p.Parent < 0 || p.Parent >= i
                ? (p.X, p.Y, p.Z)
                : (world[p.Parent].X + p.X, world[p.Parent].Y + p.Y, world[p.Parent].Z + p.Z);
        }
        return world;
    }

    private static void EmitFace(StringBuilder sb, Dictionary<int, int> remap, ushort[] vidx, Uv[] uvs,
                                 ushort tpage, UvMapper uv, ref int vtCount, bool reverse)
    {
        var vt = new int[vidx.Length];
        for (int k = 0; k < vidx.Length; k++)
        {
            var (s, t) = uv(uvs[k].U, uvs[k].V, tpage);
            sb.Append(CultureInfo.InvariantCulture, $"vt {s} {t}\n");
            vt[k] = ++vtCount;
        }
        sb.Append('f');
        var order = reverse ? Enumerable.Range(0, vidx.Length).Reverse() : Enumerable.Range(0, vidx.Length);
        // vn index == position index (emitted in lockstep with v): "pos/vt/pos".
        foreach (int k in order)
            sb.Append(CultureInfo.InvariantCulture, $" {remap[vidx[k]]}/{vt[k]}/{remap[vidx[k]]}");
        sb.Append('\n');
    }

    /// <summary>Unit-normalize a DCM SVECTOR normal (PSX fixed-point, ONE=4096, but real magnitudes vary)
    /// so the OBJ <c>vn</c> is scale-invariant; a degenerate zero normal stays zero (the viewer then
    /// falls back to the face normal). Under <paramref name="flipY"/> the Y component is negated to match
    /// the position Y-flip (a reflection across XZ also reflects the surface normal).</summary>
    private static (double X, double Y, double Z) UnitNormal(SVector n, bool flipY)
    {
        double x = n.X, y = flipY ? -n.Y : n.Y, z = n.Z;
        double len = Math.Sqrt(x * x + y * y + z * z);
        return len < 1e-9 ? (0.0, 0.0, 0.0) : (x / len, y / len, z / len);
    }
}
