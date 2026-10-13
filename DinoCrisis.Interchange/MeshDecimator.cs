using System;
using System.Collections.Generic;
using System.Linq;
using DinoCrisis.Model;

namespace DinoCrisis.Interchange;

/// <summary>
/// Byte-budget mesh decimation for injected models, specialized to the Dino Crisis constraints. It is the
/// union of two industry-standard techniques adapted to our data:
/// <list type="bullet">
///   <item><b>Quadric Error Metrics</b> (Garland–Heckbert 1997) choose which edge to collapse (lowest
///     quadric cost first) and preserve boundaries, instead of the old shortest-edge heuristic. NOTE: the
///     survivor is placed by DEFAULT on the original surface (best of the two endpoints / midpoint), NOT at
///     QEM's solved error-minimizing point — measured on the real Jill→Regina DC1 retarget, the solved point
///     bulges the mesh off-surface (new→orig error 1.1u vs ≤0.3u) AND worsens coverage (8.0→9.4u), because
///     our meshes are small, low-curvature, and rigidly single-bone. Pass <c>optimalPosition: true</c> to use the
///     solved point (better for aggressive LOD on large organic meshes). See docs/dc1/DECIMATION-DECODE.md.</item>
///   <item><b>Quad-dominant single-edge collapse</b> (Daniels 2008 / Frostbite; arXiv 2411.16874) — the
///     accepted way to simplify a <b>mixed triangle+quad</b> mesh: a quad that loses one edge becomes a
///     <b>triangle</b> (not deleted), preserving most quads and all the surface they cover.</item>
/// </list>
/// DC specifics baked in: collapses are <b>same-bone only</b> (rigid single-bone skinning — a welded vertex
/// must belong to one bone); the budget is measured in <b>bytes</b> (vertices cost as much as triangles),
/// with per-game strides via <see cref="DecByteModel"/>; <b>boundary edges</b> get Garland–Heckbert boundary
/// quadrics so open cuffs/hems don't shrink; and a <b>flip guard</b> penalizes collapses that would invert a
/// face. UVs need no attribute-quadric because they are stored <b>per corner</b> on each face, so welding a
/// position never corrupts texture mapping.
/// </summary>
public static class MeshDecimator
{
    /// <summary>Per-game geometry byte strides. DC1 = vert 16 / tri 16 / quad 20; DC2 = vert 16 / tri 12 /
    /// quad 16 (from the shipped model writers).</summary>
    public readonly record struct DecByteModel(int VertexBytes, int TriBytes, int QuadBytes)
    {
        public static DecByteModel Dc1 { get; } = new(16, 16, 20);
        public static DecByteModel Dc2 { get; } = new(16, 12, 16);
    }

    /// <summary>A vertex in the flat, bone-assigned pool. Position/normal are kept as doubles through the
    /// solve; the caller rounds to int16 on emit.</summary>
    public sealed class DecVertex
    {
        public double X, Y, Z;
        public short Bone;
        public double NX, NY, NZ;
    }

    /// <summary>A face — 3 corners (triangle) or 4 (quad) — with per-corner UVs and a single bone (every
    /// corner shares it, as the injector assigns one bone per primitive).</summary>
    public sealed class DecFace
    {
        public int[] V = Array.Empty<int>();
        public Uv[] Uv = Array.Empty<Uv>();
        public short Bone;
        public ushort Page;
        public bool IsQuad => V.Length == 4;
    }

    public sealed class DecimationResult
    {
        public required List<DecVertex> Vertices { get; init; }
        public required List<DecFace> Faces { get; init; }
        public int Collapses { get; init; }
        public int Bytes { get; init; }
        public int Triangles { get; init; }
        public int Quads { get; init; }
    }

    /// <summary>Decimate <paramref name="faces"/> over <paramref name="vertices"/> until the geometry fits
    /// <paramref name="budgetBytes"/> under <paramref name="model"/> AND the live vertex count is at most
    /// <paramref name="maxVertices"/>. Returns a compacted mesh (dead vertices removed, face indices
    /// remapped); the input lists are not mutated.
    /// <paramref name="maxVertices"/> exists because the DC1 renderer transforms character-model vertices
    /// into three FIXED 400-entry per-vertex tables in EXE .data (color 0x6B51A0 / OT-depth 0x6B57E0 /
    /// screen-XY 0x6B5E20, 4 B stride); a model with more vertices indexes past the depth table and the
    /// packet-insert helper (0x5F2AF0) dereferences a wild OT slot → AV. Byte budget and vertex count are
    /// INDEPENDENT constraints (append-geometry layouts have byte headroom but the same vertex ceiling).</summary>
    public static DecimationResult Decimate(
        IReadOnlyList<DecVertex> vertices,
        IReadOnlyList<DecFace> faces,
        int budgetBytes,
        DecByteModel model,
        double boundaryWeight = 25.0,
        double flipPenalty = 1e9,
        bool optimalPosition = false,
        int maxVertices = int.MaxValue)
    {
        if (vertices is null) throw new ArgumentNullException(nameof(vertices));
        if (faces is null) throw new ArgumentNullException(nameof(faces));

        // ---- working copies ----
        var V = vertices.Select(v => new DecVertex { X = v.X, Y = v.Y, Z = v.Z, Bone = v.Bone, NX = v.NX, NY = v.NY, NZ = v.NZ }).ToList();
        var F = faces.Select(f => new DecFace
        {
            V = (int[])f.V.Clone(), Uv = (Uv[])f.Uv.Clone(), Bone = f.Bone, Page = f.Page,
        }).ToList();
        int n = V.Count;
        var dead = new bool[n];
        var ver = new int[n];                       // version per vertex (for lazy PQ invalidation)
        var faceOf = new List<int>[n];              // vertex -> incident live face indices
        for (int i = 0; i < n; i++) faceOf[i] = new List<int>();
        var faceAlive = new bool[F.Count];
        int triCount = 0, quadCount = 0;
        for (int fi = 0; fi < F.Count; fi++)
        {
            faceAlive[fi] = true;
            if (F[fi].IsQuad) quadCount++; else triCount++;
            foreach (int vi in F[fi].V.Distinct()) faceOf[vi].Add(fi);
        }

        // ---- quadrics (4x4 symmetric, 10 unique terms) ----
        var Q = new double[n][];
        for (int i = 0; i < n; i++) Q[i] = new double[10];
        void AddPlane(int vi, double a, double b, double c, double d, double w)
        {
            var q = Q[vi];
            q[0] += w * a * a; q[1] += w * a * b; q[2] += w * a * c; q[3] += w * a * d;
            q[4] += w * b * b; q[5] += w * b * c; q[6] += w * b * d;
            q[7] += w * c * c; q[8] += w * c * d; q[9] += w * d * d;
        }
        // triangulate each face (fan) for the plane quadrics + boundary detection
        var edgeTriCount = new Dictionary<(int, int), int>();
        (int, int) EK(int a, int b) => a < b ? (a, b) : (b, a);
        foreach (var f in F)
        {
            for (int t = 1; t + 1 < f.V.Length; t++)
            {
                int i0 = f.V[0], i1 = f.V[t], i2 = f.V[t + 1];
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;
                var (nx, ny, nz, area) = TriNormal(V[i0], V[i1], V[i2]);
                if (area <= 1e-9) continue;
                double d = -(nx * V[i0].X + ny * V[i0].Y + nz * V[i0].Z);
                AddPlane(i0, nx, ny, nz, d, 1); AddPlane(i1, nx, ny, nz, d, 1); AddPlane(i2, nx, ny, nz, d, 1);
                foreach (var (a, b) in new[] { EK(i0, i1), EK(i1, i2), EK(i2, i0) })
                    edgeTriCount[(a, b)] = edgeTriCount.TryGetValue((a, b), out int c) ? c + 1 : 1;
            }
        }
        // boundary edges (one incident triangle) → perpendicular boundary plane, heavy weight
        foreach (var f in F)
            for (int t = 1; t + 1 < f.V.Length; t++)
            {
                int i0 = f.V[0], i1 = f.V[t], i2 = f.V[t + 1];
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;
                var (nx, ny, nz, area) = TriNormal(V[i0], V[i1], V[i2]);
                if (area <= 1e-9) continue;
                foreach (var (a, b) in new[] { EK(i0, i1), EK(i1, i2), EK(i2, i0) })
                {
                    if (edgeTriCount[(a, b)] != 1) continue;
                    // boundary plane: contains the edge, perpendicular to the face
                    double ex = V[b].X - V[a].X, ey = V[b].Y - V[a].Y, ez = V[b].Z - V[a].Z;
                    double px = ey * nz - ez * ny, py = ez * nx - ex * nz, pz = ex * ny - ey * nx;
                    double pl = Math.Sqrt(px * px + py * py + pz * pz);
                    if (pl <= 1e-9) continue;
                    px /= pl; py /= pl; pz /= pl;
                    double pd = -(px * V[a].X + py * V[a].Y + pz * V[a].Z);
                    AddPlane(a, px, py, pz, pd, boundaryWeight); AddPlane(b, px, py, pz, pd, boundaryWeight);
                }
            }

        // ---- candidate edges (same-bone only) ----
        var pq = new PriorityQueue<(int a, int b, int va, int vb), double>();
        void PushEdge(int a, int b)
        {
            if (a == b || dead[a] || dead[b]) return;
            if (V[a].Bone != V[b].Bone) return;
            var (pos, cost) = BestContraction(a, b);
            pq.Enqueue((a, b, ver[a], ver[b]), cost);
        }
        (double[] pos, double cost) BestContraction(int a, int b)
        {
            var q = new double[10];
            for (int i = 0; i < 10; i++) q[i] = Q[a][i] + Q[b][i];
            double[]? opt = optimalPosition ? SolveOptimal(q) : null;
            var cands = new List<double[]>();
            if (opt is not null) cands.Add(opt);
            cands.Add(new[] { (V[a].X + V[b].X) / 2, (V[a].Y + V[b].Y) / 2, (V[a].Z + V[b].Z) / 2 });
            cands.Add(new[] { V[a].X, V[a].Y, V[a].Z });
            cands.Add(new[] { V[b].X, V[b].Y, V[b].Z });
            double[] best = cands[0]; double bc = double.MaxValue;
            foreach (var p in cands)
            {
                double c = QuadricAt(q, p[0], p[1], p[2]) + FlipCost(a, b, p);
                if (c < bc) { bc = c; best = p; }
            }
            return (best, bc);
        }
        // flip guard: penalize a placement that inverts any surviving incident triangle
        double FlipCost(int a, int b, double[] p)
        {
            double penalty = 0;
            foreach (int fi in faceOf[a].Concat(faceOf[b]))
            {
                if (!faceAlive[fi]) continue;
                var f = F[fi];
                for (int t = 1; t + 1 < f.V.Length; t++)
                {
                    int i0 = f.V[0], i1 = f.V[t], i2 = f.V[t + 1];
                    if (i0 == i1 || i1 == i2 || i0 == i2) continue;
                    if ((i0 == a || i0 == b) && (i1 == a || i1 == b) || (i1 == a || i1 == b) && (i2 == a || i2 == b) || (i0 == a || i0 == b) && (i2 == a || i2 == b))
                        continue; // triangle collapses away with this edge — no flip to test
                    var (ox, oy, oz, oa) = TriNormal(V[i0], V[i1], V[i2]);
                    if (oa <= 1e-9) continue;
                    double P(int idx, int dim) => (idx == a || idx == b) ? p[dim] : (dim == 0 ? V[idx].X : dim == 1 ? V[idx].Y : V[idx].Z);
                    var (nx, ny, nz, na) = TriNormalRaw(P(i0, 0), P(i0, 1), P(i0, 2), P(i1, 0), P(i1, 1), P(i1, 2), P(i2, 0), P(i2, 1), P(i2, 2));
                    if (na <= 1e-9) continue;
                    if (ox * nx + oy * ny + oz * nz < 0) penalty += flipPenalty;
                }
            }
            return penalty;
        }

        for (int fi = 0; fi < F.Count; fi++)
        {
            var f = F[fi];
            for (int k = 0; k < f.V.Length; k++) PushEdge(f.V[k], f.V[(k + 1) % f.V.Length]);
        }

        int collapses = 0;
        int Bytes() => (n - collapses) * model.VertexBytes + triCount * model.TriBytes + quadCount * model.QuadBytes;

        // ---- collapse loop ----
        while ((Bytes() > budgetBytes || n - collapses > maxVertices) && pq.Count > 0)
        {
            var (a, b, va, vb) = pq.Dequeue();
            if (dead[a] || dead[b] || ver[a] != va || ver[b] != vb) continue; // stale
            if (V[a].Bone != V[b].Bone) continue;

            var (pos, _) = BestContraction(a, b);
            // move a to the contraction point, merge b -> a
            V[a].X = pos[0]; V[a].Y = pos[1]; V[a].Z = pos[2];
            V[a].NX += V[b].NX; V[a].NY += V[b].NY; V[a].NZ += V[b].NZ; // survivor normal = sum (renormalized on emit)
            for (int i = 0; i < 10; i++) Q[a][i] += Q[b][i];
            dead[b] = true;

            foreach (int fi in faceOf[b])
            {
                if (!faceAlive[fi]) continue;
                var f = F[fi];
                for (int k = 0; k < f.V.Length; k++) if (f.V[k] == b) f.V[k] = a;
                if (!faceOf[a].Contains(fi)) faceOf[a].Add(fi);
            }
            // resolve degeneracies on every face incident to a
            foreach (int fi in faceOf[a].ToList())
            {
                if (!faceAlive[fi]) continue;
                var f = F[fi];
                int distinct = f.V.Distinct().Count();
                if (f.IsQuad)
                {
                    if (distinct <= 2) { faceAlive[fi] = false; quadCount--; }
                    else if (distinct == 3) { DowngradeQuad(f); quadCount--; triCount++; } // quad → triangle
                    // distinct == 4: still a valid quad
                }
                else
                {
                    if (distinct <= 2) { faceAlive[fi] = false; triCount--; }
                }
            }

            ver[a]++;
            collapses++;

            // re-push edges incident to the moved survivor
            var nbrs = new HashSet<int>();
            foreach (int fi in faceOf[a]) { if (!faceAlive[fi]) continue; foreach (int vi in F[fi].V) if (vi != a && !dead[vi]) nbrs.Add(vi); }
            foreach (int nb in nbrs) PushEdge(a, nb);
        }

        // ---- compact ----
        var remap = new int[n];
        var outV = new List<DecVertex>();
        for (int i = 0; i < n; i++)
        {
            if (dead[i]) { remap[i] = -1; continue; }
            remap[i] = outV.Count; outV.Add(V[i]);
        }
        var outF = new List<DecFace>();
        int oT = 0, oQ = 0;
        for (int fi = 0; fi < F.Count; fi++)
        {
            if (!faceAlive[fi]) continue;
            var f = F[fi];
            var nv = new int[f.V.Length];
            for (int k = 0; k < f.V.Length; k++) nv[k] = remap[f.V[k]];
            if (nv.Any(x => x < 0)) continue; // referenced a dead vertex (shouldn't happen)
            outF.Add(new DecFace { V = nv, Uv = f.Uv, Bone = f.Bone, Page = f.Page });
            if (f.IsQuad) oQ++; else oT++;
        }
        // drop vertices no live face references
        var used = new HashSet<int>();
        foreach (var f in outF) foreach (int vi in f.V) used.Add(vi);
        if (used.Count != outV.Count)
        {
            var remap2 = new int[outV.Count]; var v2 = new List<DecVertex>();
            for (int i = 0; i < outV.Count; i++) { if (used.Contains(i)) { remap2[i] = v2.Count; v2.Add(outV[i]); } else remap2[i] = -1; }
            foreach (var f in outF) for (int k = 0; k < f.V.Length; k++) f.V[k] = remap2[f.V[k]];
            outV = v2;
        }

        int bytes = outV.Count * model.VertexBytes + oT * model.TriBytes + oQ * model.QuadBytes;
        return new DecimationResult { Vertices = outV, Faces = outF, Collapses = collapses, Bytes = bytes, Triangles = oT, Quads = oQ };
    }

    /// <summary>Downgrade a degenerate quad (exactly 3 distinct corners) to a triangle, keeping each surviving
    /// corner's UV.</summary>
    private static void DowngradeQuad(DecFace f)
    {
        var v = new List<int>(); var uv = new List<Uv>();
        var seen = new HashSet<int>();
        for (int k = 0; k < f.V.Length; k++)
            if (seen.Add(f.V[k])) { v.Add(f.V[k]); uv.Add(f.Uv[k]); }
        f.V = v.ToArray(); f.Uv = uv.ToArray();
    }

    private static (double nx, double ny, double nz, double area2) TriNormal(DecVertex a, DecVertex b, DecVertex c)
        => TriNormalRaw(a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z);

    private static (double nx, double ny, double nz, double area2) TriNormalRaw(
        double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
    {
        double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len <= 1e-12) return (0, 0, 0, 0);
        return (nx / len, ny / len, nz / len, len);
    }

    /// <summary>Evaluate the quadric v^T Q v at homogeneous point (x,y,z,1).</summary>
    private static double QuadricAt(double[] q, double x, double y, double z)
        => q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x
         + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y
         + q[7] * z * z + 2 * q[8] * z + q[9];

    /// <summary>Solve for the error-minimizing contraction point (Garland–Heckbert): invert the top-left
    /// 3x3 of the quadric. Returns null if singular (caller falls back to midpoint/endpoints).</summary>
    private static double[]? SolveOptimal(double[] q)
    {
        // A = [[q0,q1,q2],[q1,q4,q5],[q2,q5,q7]] , solve A v = -[q3,q6,q8]
        double a = q[0], b = q[1], c = q[2], d = q[4], e = q[5], f = q[7];
        double det = a * (d * f - e * e) - b * (b * f - e * c) + c * (b * e - d * c);
        if (Math.Abs(det) < 1e-8) return null;
        double id = 1.0 / det;
        // inverse of symmetric 3x3
        double i00 = (d * f - e * e) * id, i01 = (c * e - b * f) * id, i02 = (b * e - c * d) * id;
        double i11 = (a * f - c * c) * id, i12 = (b * c - a * e) * id, i22 = (a * d - b * b) * id;
        double rx = -q[3], ry = -q[6], rz = -q[8];
        return new[]
        {
            i00 * rx + i01 * ry + i02 * rz,
            i01 * rx + i11 * ry + i12 * rz,
            i02 * rx + i12 * ry + i22 * rz,
        };
    }
}
