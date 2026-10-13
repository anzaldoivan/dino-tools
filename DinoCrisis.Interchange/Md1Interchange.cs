using DinoCrisis.Model;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

/// <summary>Converts the neutral <see cref="InterchangeMesh"/> into an RE2-style <see cref="Md1"/>
/// mesh chunk (the interchange→EMD half). One interchange part becomes one MD1 part (a triangle
/// object + a quad object sharing a vertex/normal pool, per <c>Md1.Builder</c>).</summary>
public static class Md1Interchange
{
    /// <summary>Build an MD1 mesh from the interchange mesh.</summary>
    public static Md1 ToMd1(InterchangeMesh mesh)
    {
        var builder = new Md1.Builder();

        foreach (var part in mesh.Parts)
        {
            var p = new Md1.Builder.Part();

            foreach (var v in part.Positions)
                p.Positions.Add(new Md1.Vector(v.X, v.Y, v.Z));
            foreach (var n in part.Normals)
                p.Normals.Add(new Md1.Vector(n.X, n.Y, n.Z));

            foreach (var t in part.Triangles)
            {
                // DCM normals are per-vertex (no separate poly normal index), so the MD1 normal
                // index equals the vertex index: n := v. (G6, DCM→MD1 direction — trivial.)
                p.Triangles.Add(new Md1.Triangle
                {
                    v0 = (ushort)t.A, n0 = (ushort)t.A,
                    v1 = (ushort)t.B, n1 = (ushort)t.B,
                    v2 = (ushort)t.C, n2 = (ushort)t.C,
                });
                p.TriangleTextures.Add(new Md1.TriangleTexture
                {
                    u0 = t.Ua.U, v0 = t.Ua.V,
                    u1 = t.Ub.U, v1 = t.Ub.V,
                    u2 = t.Uc.U, v2 = t.Uc.V,
                    page = (ushort)(t.Page & 0x0F),
                    clutId = 0,
                });
            }

            foreach (var q in part.Quads)
            {
                p.Quads.Add(new Md1.Quad
                {
                    v0 = (ushort)q.A, n0 = (ushort)q.A,
                    v1 = (ushort)q.B, n1 = (ushort)q.B,
                    v2 = (ushort)q.C, n2 = (ushort)q.C,
                    v3 = (ushort)q.D, n3 = (ushort)q.D,
                });
                p.QuadTextures.Add(new Md1.QuadTexture
                {
                    u0 = q.Ua.U, v0 = q.Ua.V,
                    u1 = q.Ub.U, v1 = q.Ub.V,
                    u2 = q.Uc.U, v2 = q.Uc.V,
                    u3 = q.Ud.U, v3 = q.Ud.V,
                    page = (ushort)(q.Page & 0x0F),
                    clutId = 0,
                });
            }

            builder.Parts.Add(p);
        }

        return builder.ToMesh();
    }
}
