using DinoCrisis.Model;

namespace DinoCrisis.Interchange;

/// <summary>Converts a parsed <see cref="DcmModel"/> into the neutral <see cref="InterchangeMesh"/>,
/// preserving the rig (each part's geometry stays in its own bone-local space; the parent tree and
/// rest poses are carried through). This is the DCM→interchange half of "G5".</summary>
public static class DcmInterchange
{
    /// <summary>Build a rig-preserving interchange mesh from a DCM model.</summary>
    public static InterchangeMesh ToInterchange(DcmModel model)
    {
        var mesh = new InterchangeMesh();

        // The DCM triangle/quad lists are one flat pool, partitioned by per-part counts in part
        // order (the same partition ObjExporter walks). Advance a cursor through each.
        int triCursor = 0, quadCursor = 0;

        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var src = model.Parts[pi];
            var dst = new InterchangePart
            {
                RestX = src.X,
                RestY = src.Y,
                RestZ = src.Z,
                Parent = src.Parent,
            };

            // Collect the GLOBAL vertex indices this part uses, then remap them to a dense,
            // part-LOCAL pool (MD1 wants per-part vertex bases; DCM verts are already bone-local).
            var used = new SortedSet<int>();
            for (int t = 0; t < src.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                used.Add(tri.V0); used.Add(tri.V1); used.Add(tri.V2);
            }
            for (int q = 0; q < src.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                used.Add(qd.V0); used.Add(qd.V1); used.Add(qd.V2); used.Add(qd.V3);
            }

            var remap = new Dictionary<int, int>(used.Count);
            foreach (int g in used)
            {
                remap[g] = dst.Positions.Count;
                dst.Positions.Add(model.Vertices[g]);
                // Normal pool is parallel to the vertex pool (DCM-TOOLSET-PLAN Inc 7): one normal
                // per position, indexed by the same vertex index.
                dst.Normals.Add(model.Normals[g]);
            }

            for (int t = 0; t < src.TriangleCount; t++)
            {
                var tri = model.Triangles[triCursor + t];
                dst.Triangles.Add(new InterchangeTriangle
                {
                    A = remap[tri.V0], B = remap[tri.V1], C = remap[tri.V2],
                    Ua = tri.Uv0, Ub = tri.Uv1, Uc = tri.Uv2,
                    Page = tri.TPage,
                });
            }
            for (int q = 0; q < src.QuadCount; q++)
            {
                var qd = model.Quads[quadCursor + q];
                dst.Quads.Add(new InterchangeQuad
                {
                    A = remap[qd.V0], B = remap[qd.V1], C = remap[qd.V2], D = remap[qd.V3],
                    Ua = qd.Uv0, Ub = qd.Uv1, Uc = qd.Uv2, Ud = qd.Uv3,
                    Page = qd.TPage,
                });
            }

            triCursor += src.TriangleCount;
            quadCursor += src.QuadCount;
            mesh.Parts.Add(dst);
        }

        return mesh;
    }
}
