namespace DinoCrisis.Interchange;

/// <summary>
/// Reconstitutes PSX strip quads from imported triangle pairs. glTF is triangle-only, so an export
/// splits every DCM quad (v0,v1,v2,v3) into (v0,v1,v2)+(v1,v3,v2); an all-triangle re-import costs
/// bytes (2 tris > 1 quad in both games' strides) and can push a DC2 model past its <b>in-place</b>
/// geometry budget. This pass greedily merges any two triangles that share a reversed directed edge
/// with matching UVs and texture codes back into one strip quad.
///
/// <para><b>Render-exactness:</b> the merged quad (u1,x,y,u2) rasterizes as exactly the two source
/// triangles (u1,x,y) and (x,u2,y), so ANY valid pairing — not necessarily the original one — draws
/// the identical picture. Byte size can only shrink (pairs of stock triangles that happen to share
/// a seamless edge also merge, harmlessly).</para>
/// </summary>
public static class QuadMerge
{
    /// <summary>Merge triangle pairs in place; <see cref="ImportedMesh.Triangles"/> shrinks and
    /// <see cref="ImportedMesh.Quads"/> grows. Returns the number of merges.</summary>
    public static int Merge(ImportedMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var tris = mesh.Triangles;

        // Directed-edge index: (a→b) → triangles containing that edge.
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (v, _) = Corners(tris[i]);
            for (int k = 0; k < 3; k++)
            {
                var key = (v[k], v[(k + 1) % 3]);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>();
                list.Add(i);
            }
        }

        var used = new bool[tris.Count];
        int merges = 0;
        for (int i = 0; i < tris.Count; i++)
        {
            if (used[i]) continue;
            var t1 = tris[i];
            var (v1, uv1) = Corners(t1);

            for (int k = 0; k < 3 && !used[i]; k++)
            {
                // Candidate strip: u1 = v1[k], diagonal x→y = the opposite edge.
                int u1 = v1[k], x = v1[(k + 1) % 3], y = v1[(k + 2) % 3];
                if (!byEdge.TryGetValue((y, x), out var candidates)) continue;

                foreach (int j in candidates)
                {
                    if (j == i || used[j]) continue;
                    var t2 = tris[j];
                    if (t2.TPage != t1.TPage || t2.Clut != t1.Clut || t2.Part != t1.Part) continue;
                    var (v2, uv2) = Corners(t2);

                    // T2 must be (x, u2, y) up to rotation — i.e. carry the directed edge y→x.
                    int m = -1;
                    for (int c = 0; c < 3; c++)
                        if (v2[c] == y && v2[(c + 1) % 3] == x) { m = c; break; }
                    if (m < 0) continue;
                    int u2 = v2[(m + 2) % 3];

                    // Shared corners must agree on UV, else the seam is a texture boundary.
                    if (uv1[(k + 1) % 3] != uv2[(m + 1) % 3]) continue; // UV at x
                    if (uv1[(k + 2) % 3] != uv2[m]) continue;           // UV at y

                    mesh.Quads.Add(new ImportedMesh.Quad
                    {
                        V0 = u1, V1 = x, V2 = y, V3 = u2,
                        Uv0 = uv1[k], Uv1 = uv1[(k + 1) % 3], Uv2 = uv1[(k + 2) % 3], Uv3 = uv2[(m + 2) % 3],
                        TPage = t1.TPage, Clut = t1.Clut, Part = t1.Part,
                    });
                    used[i] = used[j] = true;
                    merges++;
                    break;
                }
            }
        }

        if (merges > 0)
        {
            var kept = new List<ImportedMesh.Triangle>(tris.Count - 2 * merges);
            for (int i = 0; i < tris.Count; i++)
                if (!used[i]) kept.Add(tris[i]);
            tris.Clear();
            tris.AddRange(kept);
        }
        return merges;
    }

    private static (int[] V, DinoCrisis.Model.Uv[] Uv) Corners(ImportedMesh.Triangle t)
        => (new[] { t.V0, t.V1, t.V2 }, new[] { t.Uv0, t.Uv1, t.Uv2 });
}
