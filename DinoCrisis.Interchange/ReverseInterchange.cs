using DinoCrisis.Model;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

/// <summary>
/// The <b>reverse</b> pipeline (EMD/PLD → DCM): read an RE model's mesh + skeleton into the neutral
/// <see cref="InterchangeMesh"/> and emit a Dino Crisis DCM. First cut — geometry + rest-pose skeleton;
/// animation (the cross-rig motion-retarget gate) is out of scope, so the produced model is a static
/// rest-pose creature. Mesh of any RE version is normalized through MD1 (via the MIT MeshConverter,
/// remap disabled), then MD1's per-corner normal indices are collapsed to DCM's per-vertex normals.
/// </summary>
public static class ReverseInterchange
{
    /// <summary>Read an RE mesh (MD1/MD2/TMD) into the neutral mesh — geometry only (no skeleton yet).
    /// MD1 carries a separate normal index per corner; DCM is per-vertex, so each vertex takes the
    /// normal of a corner that references it (the MD1→DCM half of gap G6).</summary>
    public static InterchangeMesh FromReMesh(IModelMesh mesh)
    {
        if (mesh is null) throw new System.ArgumentNullException(nameof(mesh));
        var md1 = mesh.Version == BioVersion.Biohazard2
            ? (Md1)mesh
            : (Md1)new MeshConverter().ConvertMesh(mesh, BioVersion.Biohazard2, remap: false);

        var im = new InterchangeMesh();
        for (int pi = 0; pi < md1.NumParts; pi++)
        {
            var triObj = md1.Objects[pi * 2];
            var quadObj = md1.Objects[pi * 2 + 1];

            var positions = md1.GetPositionData(triObj);
            var normalPool = md1.GetNormalData(triObj);
            var part = new InterchangePart();

            var perVertexNormal = new Md1.Vector[positions.Length];
            // The 4th SVECTOR word carries the per-vertex bone index. RE MD1 has no such field — it rigs
            // by per-part vertex objects — so each object's vertices belong to this part's bone (pi). DC
            // (esp. DC2) skins by this per-vertex index, so a foreign rig that leaves it 0 binds every
            // vertex to bone 0 → correct at rest, stretched under animation. Stamp it as pi.
            for (int i = 0; i < positions.Length; i++)
                part.Positions.Add(new SVector(positions[i].x, positions[i].y, positions[i].z, (short)pi));

            var tris = md1.GetTriangles(triObj);
            var triTex = md1.GetTriangleTextures(triObj);
            for (int t = 0; t < tris.Length; t++)
            {
                var tri = tris[t];
                var tex = triTex[t];
                perVertexNormal[tri.v0] = normalPool[tri.n0];
                perVertexNormal[tri.v1] = normalPool[tri.n1];
                perVertexNormal[tri.v2] = normalPool[tri.n2];
                part.Triangles.Add(new InterchangeTriangle
                {
                    A = tri.v0, B = tri.v1, C = tri.v2,
                    Ua = new Uv(tex.u0, tex.v0), Ub = new Uv(tex.u1, tex.v1), Uc = new Uv(tex.u2, tex.v2),
                    Page = tex.page & 0x0F,
                });
            }

            var quads = md1.GetQuads(quadObj);
            var quadTex = md1.GetQuadTextures(quadObj);
            for (int q = 0; q < quads.Length; q++)
            {
                var qd = quads[q];
                var tex = quadTex[q];
                perVertexNormal[qd.v0] = normalPool[qd.n0];
                perVertexNormal[qd.v1] = normalPool[qd.n1];
                perVertexNormal[qd.v2] = normalPool[qd.n2];
                perVertexNormal[qd.v3] = normalPool[qd.n3];
                part.Quads.Add(new InterchangeQuad
                {
                    A = qd.v0, B = qd.v1, C = qd.v2, D = qd.v3,
                    Ua = new Uv(tex.u0, tex.v0), Ub = new Uv(tex.u1, tex.v1),
                    Uc = new Uv(tex.u2, tex.v2), Ud = new Uv(tex.u3, tex.v3),
                    Page = tex.page & 0x0F,
                });
            }

            foreach (var n in perVertexNormal)
                part.Normals.Add(new SVector(n.x, n.y, n.z, 0));

            im.Parts.Add(part);
        }
        return im;
    }

    /// <summary>Apply an RE EMR skeleton (rest poses + hierarchy) onto an interchange mesh. EMR stores
    /// child lists; this inverts them to the parent index DCM/interchange use, and copies the
    /// parent-relative rest positions. <b>Lenient on count</b>: RE <i>player</i> meshes (PLD) carry more
    /// mesh parts than armature bones (e.g. 21 mesh parts vs 15 bones — the extras are weapon/attachment
    /// objects), so the EMR is applied to the first <c>NumParts</c> body parts and any extra mesh parts
    /// are attached to the root (rest 0) — geometry stays valid; the body poses correctly.</summary>
    public static void ApplySkeleton(InterchangeMesh mesh, Emr emr)
    {
        if (mesh is null) throw new System.ArgumentNullException(nameof(mesh));
        if (emr is null) throw new System.ArgumentNullException(nameof(emr));

        int bones = System.Math.Min(emr.NumParts, mesh.Parts.Count);

        // Defaults: part 0 is root, everything else (incl. extra mesh parts beyond the armature) hangs
        // off the root at rest 0 until a parent/rest from the EMR claims it.
        for (int i = 0; i < mesh.Parts.Count; i++)
        {
            mesh.Parts[i].RestX = 0; mesh.Parts[i].RestY = 0; mesh.Parts[i].RestZ = 0;
            mesh.Parts[i].Parent = i == 0 ? -1 : 0;
        }
        for (int i = 0; i < bones; i++)
        {
            var rel = emr.GetRelativePosition(i);
            mesh.Parts[i].RestX = rel.x;
            mesh.Parts[i].RestY = rel.y;
            mesh.Parts[i].RestZ = rel.z;
            mesh.Parts[i].Parent = i == 0 ? -1 : (sbyte)0; // overwritten by the child-list pass below
        }
        for (int i = 0; i < bones; i++)
            foreach (var child in emr.GetArmatureParts(i).ToArray())
                if (child < bones)
                    mesh.Parts[child].Parent = i;
    }

    /// <summary>Emit a DCM blob from the neutral mesh (built from scratch via <see cref="DcmModelBuilder"/>).
    /// Part 0 must be the root (negative parent) and every other part's parent must precede it — the DCM
    /// invariant, which RE player/enemy rigs satisfy. <paramref name="scale"/> multiplies every vertex
    /// position and rest pose (RE model units are ≈2.6× DC — e.g. ~0.38 fits an RE character into DC
    /// proportions); normals are directions and stay unscaled. <paramref name="tpageOr"/> is OR-ed into
    /// each polygon's DC1 tpage (e.g. <c>0x80</c> = 8bpp depth) and <paramref name="clut"/> sets each
    /// polygon's CLUT code — set both so the model samples an in-package texture (see
    /// <c>DcPackageAssembler</c>); leave 0 for an untextured model.</summary>
    public static byte[] ToDcm(InterchangeMesh mesh, DcmVariant variant, uint loadBase,
                              double scale = 1.0, ushort tpageOr = 0, ushort clut = 0)
    {
        if (mesh is null) throw new System.ArgumentNullException(nameof(mesh));
        var builder = new DcmModelBuilder(variant);

        static short S(double v) => (short)System.Math.Clamp(System.Math.Round(v), short.MinValue, short.MaxValue);

        foreach (var part in mesh.Parts)
        {
            var scaled = part.Positions
                .Select(p => new SVector(S(p.X * scale), S(p.Y * scale), S(p.Z * scale), p.Pad))
                .ToList();
            int baseIndex = builder.AddVertices(scaled, part.Normals);

            var tris = part.Triangles.Select(t => new DcmTriangle
            {
                V0 = (ushort)(baseIndex + t.A), V1 = (ushort)(baseIndex + t.B), V2 = (ushort)(baseIndex + t.C),
                Uv0 = t.Ua, Uv1 = t.Ub, Uv2 = t.Uc, TPage = (ushort)((t.Page & 0xF) | tpageOr), Clut = clut,
            });
            var quads = part.Quads.Select(q => new DcmQuad
            {
                V0 = (ushort)(baseIndex + q.A), V1 = (ushort)(baseIndex + q.B),
                V2 = (ushort)(baseIndex + q.C), V3 = (ushort)(baseIndex + q.D),
                Uv0 = q.Ua, Uv1 = q.Ub, Uv2 = q.Uc, Uv3 = q.Ud, TPage = (ushort)((q.Page & 0xF) | tpageOr), Clut = clut,
            });

            builder.AddPart(
                new DcmPart { X = S(part.RestX * scale), Y = S(part.RestY * scale), Z = S(part.RestZ * scale), Parent = (sbyte)part.Parent, Child = 0 },
                tris, quads);
        }

        return builder.Serialize(loadBase);
    }
}
