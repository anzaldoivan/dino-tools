using System.Buffers.Binary;
using System.Text;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;

namespace DinoCrisis.Interchange;

/// <summary>
/// Splices an edited mesh into a DC2 model file (<c>E*.DAT</c> / <c>WEP_*.DAT</c>) — the DC2 half
/// of the P2 same-rig import, built on the mechanics of the DC2 injection probes:
///
/// <list type="bullet">
/// <item><b>In-place fit.</b> The DC2 blob loads at a fixed resident base and its animation tail is
///   a table of ABSOLUTE pointers into keyframe data, so nothing may move and the blob must keep
///   its stock size: vertices+normals+triangles+quads are all rebuilt inside the stock geometry
///   region. DC2 has no append headroom; the current safe DC1 path also elects not to grow its
///   physically different core arena.</item>
/// <item><b>Binding is per-poly part ownership</b> (DC2 does not skin by the SVECTOR pad word):
///   each polygon's corners are expressed local to the polygon's part, and the pool dedups by the
///   resulting LOCAL coordinates — exactly how stock pools share seam vertices across parts. Pads
///   are stamped with the first-referencing part (cosmetic, matches the probes).</item>
/// <item><b>Texture codes are header-level</b> (one tpage/clut for the whole model). A polygon
///   whose reconstructed page differs from the header's cannot be expressed and is reported.</item>
/// </list>
/// </summary>
public static class Dc2ModelSplicer
{
    /// <summary>Splice <paramref name="mesh"/> over the model at <paramref name="modelBase"/> inside
    /// <paramref name="fileBytes"/>. Throws <see cref="InvalidDataException"/> (bad package/model) or
    /// <see cref="InvalidOperationException"/> (budget refusal, with numbers).</summary>
    public static SpliceResult Splice(byte[] fileBytes, uint modelBase, ImportedMesh mesh,
                                      bool allowUnsafeMovedVertices = false)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ArgumentNullException.ThrowIfNull(mesh);

        var pkg = GianPackage.TryParse(fileBytes) ?? throw new InvalidDataException("not a Gian package.");
        if (!pkg.IsDc2) throw new InvalidDataException("not a DC2 package (use the DC1 splicer).");

        int entryIdx = -1;
        for (int i = 0; i < pkg.Entries.Count; i++)
            if (pkg.Entries[i].Reserve0 == modelBase) { entryIdx = i; break; }
        if (entryIdx < 0)
            throw new InvalidDataException($"no entry with load base 0x{modelBase:X} in the package.");
        var entry = pkg.Entries[entryIdx];

        byte[] blobSrc = pkg.DecodeEntry(fileBytes, entry);
        var target = DcmModel.TryReadAt(blobSrc, 0, modelBase, DcmVariant.Dc2)
            ?? throw new InvalidDataException($"entry at 0x{modelBase:X} does not parse as a DC2 model.");
        var transformCoverage = Dc2RendererDiagnostics.ValidateTransformCoverage(
            target.Vertices.Count,
            target.Parts.Select(part => part.Child).ToArray());

        int nb = target.Parts.Count;
        var world = ObjExporter.ComputeWorldPositions(target);
        int geomStart = (int)(target.Header.VertexPtr - modelBase);
        int geomEnd = (int)(target.Header.QuadPtr - modelBase) + target.Header.QuadCount * DcmVariant.Dc2.QuadStride;

        // ---- PRESERVE THE STOCK POOL VERBATIM and map every polygon corner back onto its
        // ORIGINAL stock index via (bone-local position vs the POLY's part, part) — the same
        // engine-indistinguishability rule as the DC1 splicer (the exact DC2 binding semantics
        // are unproven; a byte-identical pool is correct under every candidate). New/moved
        // vertices append at the tail. ----
        var stockIndexByRef = new Dictionary<(short, short, short, short, short, short, int), int>();
        var ambiguousStockRefs = new HashSet<(short, short, short, short, short, short, int)>();
        {
            int tc = 0, qc = 0;
            for (int pi = 0; pi < nb; pi++)
            {
                void Add((short, short, short, short, short, short, int) key, int vi)
                {
                    if (stockIndexByRef.TryGetValue(key, out int existing) && existing != vi)
                        ambiguousStockRefs.Add(key);
                    else
                        stockIndexByRef[key] = vi;
                }
                void Refer(int vi)
                {
                    var sv = target.Vertices[vi];
                    var n = target.Normals[vi];
                    var cn = Dc1ModelSplicer.CanonicalNormal(n.X, n.Y, n.Z);
                    Add((sv.X, sv.Y, sv.Z, cn.X, cn.Y, cn.Z, pi), vi);
                    if (n.X == 0 && n.Y == 0 && n.Z == 0)
                        Add((sv.X, sv.Y, sv.Z, 0, (short)-cn.Y, 0, pi), vi);
                }
                for (int t = 0; t < target.Parts[pi].TriangleCount; t++)
                {
                    var tr = target.Triangles[tc + t];
                    Refer(tr.V0); Refer(tr.V1); Refer(tr.V2);
                }
                tc += target.Parts[pi].TriangleCount;
                for (int q = 0; q < target.Parts[pi].QuadCount; q++)
                {
                    var qd = target.Quads[qc + q];
                    Refer(qd.V0); Refer(qd.V1); Refer(qd.V2); Refer(qd.V3);
                }
                qc += target.Parts[pi].QuadCount;
            }
        }

        int stockCount = target.Vertices.Count;
        var appended = new List<(short X, short Y, short Z, short Pad, ImportedMesh.Vertex Src)>();
        var appendDedup = new Dictionary<(short, short, short, short, short, short, short), int>();
        int pageMismatches = 0;

        int LocalIndex(int vi, short part)
        {
            var v = mesh.Vertices[vi];
            if (v.Joint < 0 || v.Joint >= nb)
                throw new InvalidOperationException($"vertex bound to joint {v.Joint} but the rig has {nb} bones.");
            int lx = v.X - world[part].X, ly = v.Y - world[part].Y, lz = v.Z - world[part].Z;
            if (lx is < short.MinValue or > short.MaxValue || ly is < short.MinValue or > short.MaxValue ||
                lz is < short.MinValue or > short.MaxValue)
                throw new InvalidOperationException(
                    $"vertex at world ({v.X},{v.Y},{v.Z}) is beyond int16 range from part {part} — " +
                    "the mesh drifted too far from the skeleton (check scale / accidental object move).");
            var stockKey = ((short)lx, (short)ly, (short)lz, v.NX, v.NY, v.NZ, (int)part);
            if (ambiguousStockRefs.Contains(stockKey))
                throw new InvalidOperationException(
                    "DC2 stock vertex mapping is ambiguous after glTF normal unitization; " +
                    "coincident stock records with indistinguishable normal directions cannot be safely selected.");
            if (stockIndexByRef.TryGetValue(stockKey, out int si))
                return si;
            var akey = ((short)lx, (short)ly, (short)lz, v.NX, v.NY, v.NZ, part);
            if (!appendDedup.TryGetValue(akey, out int ai))
            {
                ai = appended.Count;
                appended.Add(((short)lx, (short)ly, (short)lz, part, v));
                appendDedup[akey] = ai;
            }
            return stockCount + ai;
        }

        var jbuf = new short[4];
        short MajorityOf3(int a, int b, int c)
        {
            jbuf[0] = mesh.Vertices[a].Joint; jbuf[1] = mesh.Vertices[b].Joint; jbuf[2] = mesh.Vertices[c].Joint;
            return Dc1ModelSplicer.Majority(jbuf.AsSpan(0, 3));
        }
        short MajorityOf4(int a, int b, int c, int d)
        {
            jbuf[0] = mesh.Vertices[a].Joint; jbuf[1] = mesh.Vertices[b].Joint;
            jbuf[2] = mesh.Vertices[c].Joint; jbuf[3] = mesh.Vertices[d].Joint;
            return Dc1ModelSplicer.Majority(jbuf);
        }

        var triByBone = new List<(int A, int B, int C, ImportedMesh.Triangle T)>[nb];
        var quadByBone = new List<(int A, int B, int C, int D, ImportedMesh.Quad Q)>[nb];
        for (int b = 0; b < nb; b++) { triByBone[b] = new(); quadByBone[b] = new(); }
        foreach (var t in mesh.Triangles)
        {
            short part = t.Part >= 0 ? checked((short)t.Part) : MajorityOf3(t.V0, t.V1, t.V2);
            if (part >= nb) throw new InvalidOperationException($"triangle polygon part {part} is outside the target rig.");
            if (t.TPage != target.Header.TPage) pageMismatches++;
            triByBone[part].Add((LocalIndex(t.V0, part), LocalIndex(t.V1, part), LocalIndex(t.V2, part), t));
        }
        foreach (var q in mesh.Quads)
        {
            short part = q.Part >= 0 ? checked((short)q.Part) : MajorityOf4(q.V0, q.V1, q.V2, q.V3);
            if (part >= nb) throw new InvalidOperationException($"quad polygon part {part} is outside the target rig.");
            if (q.TPage != target.Header.TPage) pageMismatches++;
            quadByBone[part].Add((LocalIndex(q.V0, part), LocalIndex(q.V1, part), LocalIndex(q.V2, part),
                                  LocalIndex(q.V3, part), q));
        }

        if (pageMismatches > 0)
            throw new InvalidOperationException(
                $"DC2 header texture page cannot represent {pageMismatches} polygon(s); " +
                "keep every polygon on the target model's single header-level texture page.");

        if (appended.Count > 0 && !allowUnsafeMovedVertices)
            throw new InvalidOperationException(
                $"DC2 moved/new vertices are unverified: {appended.Count} vertex record(s) do not map to the stock pool. " +
                "Refused by default; pass the explicit unsafe opt-in only for controlled validation.");

        int vc = stockCount + appended.Count;
        int tN = mesh.Triangles.Count, qN = mesh.Quads.Count;
        int need = vc * 16 + tN * DcmVariant.Dc2.TriangleStride + qN * DcmVariant.Dc2.QuadStride;
        int budget = geomEnd - geomStart;
        if (need > budget)
            throw new InvalidOperationException(
                $"geometry needs {need} B but the stock in-place region holds {budget} B " +
                $"({vc} verts ×16 + {tN} tris ×12 + {qN} quads ×16). DC2 blobs cannot grow " +
                $"(absolute animation pointers follow) — reduce vertices/polygons by ~{need - budget} B.");

        // ---- emit into a stock-size clone ----
        var blob = (byte[])blobSrc.Clone();
        void WI16(int o, int val) => BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(o, 2), (short)val);
        void WU16(int o, int val) => BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(o, 2), (ushort)val);
        void W32(int o, uint val) => BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(o, 4), val);

        int vtxOff = geomStart, nrmOff = vtxOff + vc * 8, triOff = nrmOff + vc * 8,
            quadOff = triOff + tN * DcmVariant.Dc2.TriangleStride,
            newGeomEnd = quadOff + qN * DcmVariant.Dc2.QuadStride;

        // Stock vertex and normal arrays byte-verbatim from the source blob; appended records follow.
        int stockVtx = (int)(target.Header.VertexPtr - modelBase);
        int stockNrm = (int)(target.Header.NormalPtr - modelBase);
        Array.Copy(blobSrc, stockVtx, blob, vtxOff, stockCount * 8);
        Array.Copy(blobSrc, stockNrm, blob, nrmOff, stockCount * 8);
        for (int ai = 0; ai < appended.Count; ai++)
        {
            var p = appended[ai];
            int ov = vtxOff + (stockCount + ai) * 8;
            WI16(ov, p.X); WI16(ov + 2, p.Y); WI16(ov + 4, p.Z); WI16(ov + 6, p.Pad);
            int on = nrmOff + (stockCount + ai) * 8;
            WI16(on, p.Src.NX); WI16(on + 2, p.Src.NY); WI16(on + 4, p.Src.NZ); WI16(on + 6, 0);
        }
        int tcur = triOff;
        foreach (var bucket in triByBone)
            foreach (var (a, b, c, t) in bucket)
            {
                int o = tcur; tcur += 12;
                WU16(o, a); WU16(o + 2, b); WU16(o + 4, c);
                blob[o + 6] = t.Uv0.U; blob[o + 7] = t.Uv0.V;
                blob[o + 8] = t.Uv1.U; blob[o + 9] = t.Uv1.V;
                blob[o + 10] = t.Uv2.U; blob[o + 11] = t.Uv2.V;
            }
        int qcur = quadOff;
        foreach (var bucket in quadByBone)
            foreach (var (a, b, c, d, q) in bucket)
            {
                int o = qcur; qcur += 16;
                WU16(o, a); WU16(o + 2, b); WU16(o + 4, c); WU16(o + 6, d);
                blob[o + 8] = q.Uv0.U; blob[o + 9] = q.Uv0.V;
                blob[o + 10] = q.Uv1.U; blob[o + 11] = q.Uv1.V;
                blob[o + 12] = q.Uv2.U; blob[o + 13] = q.Uv2.V;
                blob[o + 14] = q.Uv3.U; blob[o + 15] = q.Uv3.V;
            }
        for (int o = newGeomEnd; o < geomEnd; o++) blob[o] = 0; // clear the unused stock remainder

        W32(0x00, modelBase + (uint)vtxOff); W32(0x04, modelBase + (uint)nrmOff);
        W32(0x08, modelBase + (uint)triOff); W32(0x0c, modelBase + (uint)quadOff);
        WU16(0x10, tN); WU16(0x12, qN);
        // header tpage/clut @0x18/0x1a stay verbatim (the skin/palette are untouched by a mesh edit)
        uint tptr = modelBase + (uint)triOff, qptr = modelBase + (uint)quadOff;
        for (int i = 0; i < nb; i++)
        {
            int o = 0x1c + i * 0x14; // rest xyz + parent/child bytes stay verbatim
            W32(o + 8, tptr); W32(o + 0xc, qptr);
            WU16(o + 0x10, triByBone[i].Count); WU16(o + 0x12, quadByBone[i].Count);
            tptr += (uint)(triByBone[i].Count * DcmVariant.Dc2.TriangleStride);
            qptr += (uint)(quadByBone[i].Count * DcmVariant.Dc2.QuadStride);
        }

        // ---- self-verify ----
        if (blob.Length != blobSrc.Length)
            throw new InvalidOperationException("internal: DC2 blob size changed.");
        if (!blob.AsSpan(geomEnd).SequenceEqual(blobSrc.AsSpan(geomEnd)))
            throw new InvalidOperationException("internal: animation tail not byte-exact after splice.");
        var reread = DcmModel.TryReadAt(blob, 0, modelBase, DcmVariant.Dc2)
            ?? throw new InvalidOperationException("internal: spliced model does not re-parse.");
        if (!DcmModel.Validate(reread))
            throw new InvalidOperationException("internal: spliced model fails Validate.");
        if (!ObjExporter.ComputeWorldPositions(reread).SequenceEqual(world))
            throw new InvalidOperationException("internal: target skeleton not preserved verbatim.");

        // ---- rebuild the package: clone the 2048-byte descriptor table VERBATIM (DC2 entries carry
        // words our builder doesn't model), patch only the replaced entry's size, re-lay payloads ----
        byte[] payload = entry.IsCompressed ? Lzss.Compress(blob) : blob;
        if (entry.IsCompressed && !Lzss.Decompress(payload).AsSpan().SequenceEqual(blob))
            throw new InvalidOperationException("internal: LZSS round-trip failed.");
        byte[] injected = RebuildDc2Package(fileBytes, pkg, entryIdx, payload);

        var report = new StringBuilder()
            .AppendLine($"spliced (DC2, in-place): {vc} verts, {tN} tris, {qN} quads")
            .AppendLine($"transform triplets: {transformCoverage.TransformTriplets} " +
                        $"({transformCoverage.CoveredVertices} stock vertices covered exactly)")
            .AppendLine($"geometry {need}/{budget} B of the stock region; blob {blob.Length} B (stock size kept)")
            .AppendLine(Dc2RendererDiagnostics.FormatPacketContribution(tN, qN))
            .AppendLine("actor-path ceiling: advisory only; renderer-family classification is not proven, so no 420-vertex refusal is applied")
            .Append(appended.Count > 0
                ? $"WARNING: unsafe DC2 moved/new vertex opt-in accepted {appended.Count} appended record(s) outside proven transform-run coverage; descriptor +7 remains unchanged and runtime binding is unverified.{Environment.NewLine}"
                : "")
            .AppendLine($"skeleton+animation verbatim" +
                        (pageMismatches > 0
                            ? $"; WARNING: {pageMismatches} polygon(s) sample outside the header texture page"
                            : ""))
            .ToString();
        return new SpliceResult { Package = injected, Vertices = vc, Triangles = tN, Quads = qN, Report = report };
    }

    /// <summary>Rebuild a DC2 package around one replaced payload: descriptor table cloned verbatim
    /// (only the replaced entry's size word changes), payloads sector-aligned in entry order.</summary>
    private static byte[] RebuildDc2Package(byte[] fileBytes, GianPackage pkg, int replaceIdx, byte[] newPayload)
    {
        const int header = 2048, sector = 2048;
        static int Align(int v) => (v + (sector - 1)) & ~(sector - 1);

        var payloads = new byte[pkg.Entries.Count][];
        int total = header;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            payloads[i] = i == replaceIdx ? newPayload
                : fileBytes.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();
            total += Align(payloads[i].Length);
        }

        var buf = new byte[total];
        Array.Copy(fileBytes, buf, header); // full 32-byte descriptors, verbatim
        BinaryPrimitives.WriteUInt32LittleEndian(
            buf.AsSpan(replaceIdx * GianPackage.Dc2EntrySize + 4, 4), (uint)newPayload.Length);
        int pos = header;
        foreach (var p in payloads)
        {
            p.CopyTo(buf, pos);
            pos += Align(p.Length);
        }
        return buf;
    }
}
