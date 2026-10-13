using System.Buffers.Binary;
using System.Text;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;

namespace DinoCrisis.Interchange;

/// <summary>The outcome of a splice: the rebuilt package plus the numbers a caller reports.</summary>
public sealed class SpliceResult
{
    public required byte[] Package { get; init; }
    public required int Vertices { get; init; }
    public required int Triangles { get; init; }
    public required int Quads { get; init; }
    public required string Report { get; init; }
}

internal readonly record struct Dc1PacketContributionResult(int Bytes, double ArenaPercentage);

internal static class Dc1PacketContribution
{
    internal const int ArenaBytes = 128 * 1024;
    private const int Gt3Bytes = 40;
    private const int Gt4Bytes = 52;

    internal static Dc1PacketContributionResult Calculate(int triangles, int quads)
    {
        int bytes = checked(Gt3Bytes * triangles + Gt4Bytes * quads);
        return new(bytes, bytes * 100d / ArenaBytes);
    }

    internal static string Format(int triangles, int quads)
    {
        var contribution = Calculate(triangles, quads);
        return FormattableString.Invariant($"packet contribution: {contribution.Bytes:N0}/{ArenaBytes:N0} B ({contribution.ArenaPercentage:F1}%); model contribution, not whole-frame headroom");
    }
}

/// <summary>
/// Splices an edited mesh into a DC1 standalone character package (<c>core*.dat</c>-style) while
/// keeping the target's <b>skeleton and animation verbatim</b> — the production form of the emit
/// mechanics proven in-game by the Dylan/Jill injection probes (DC-MODEL-TOOLKIT-PLAN P2,
/// docs/dc1/DYLAN-DC2-TO-DC1-CONVERSION.md):
///
/// <list type="bullet">
/// <item>the stock vertex+normal pools are copied byte-for-byte and every imported corner must map
///   back to an existing stock record and its transform-run joint;</item>
/// <item>triangles and quads are rebuilt after those pools inside the stock geometry region;</item>
/// <item>the header + per-part pointers/counts are rewritten; everything else — including the
///   animation/motion tail — must survive <b>byte-exact</b> (self-verified).</item>
/// </list>
///
/// Every budget violation throws with the actual numbers so the caller can act (decimate, drop
/// detail) rather than guess.
/// </summary>
public static class Dc1ModelSplicer
{
    /// <summary>The lifted renderer vertex ceiling (paired DINO.exe + ddraw.dll <c>.dinovtx</c>
    /// patches). Pass 400 for an unpatched install.</summary>
    public const int LiftedVertexCeiling = 1024;

    /// <summary>Legacy compatibility input retained for callers of the former append path. The safe
    /// stock-region implementation does not grow the decoded blob.</summary>
    public const uint DefaultCapEnd = 0x801A6000;

    /// <summary>Majority element of ≤4 joint indices; ties go to the smallest index.</summary>
    internal static short Majority(ReadOnlySpan<short> bones)
    {
        short best = bones[0];
        int bestCount = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            int c = 0;
            for (int j = 0; j < bones.Length; j++)
                if (bones[j] == bones[i]) c++;
            if (c > bestCount || (c == bestCount && bones[i] < best)) { bestCount = c; best = bones[i]; }
        }
        return best;
    }

    /// <summary>Canonical fixed-point direction produced by the glTF export/import pair. glTF
    /// normals are unit vectors, so their original DCM magnitude is intentionally not available
    /// when matching a corner back to a stock record.</summary>
    internal static (short X, short Y, short Z) CanonicalNormal(short x, short y, short z)
    {
        float length = MathF.Sqrt(x * (float)x + y * (float)y + z * (float)z);
        if (length < 1e-6f)
            return (0, -4096, 0); // exporter substitutes +Y; the standard Y reflection restores -Y

        static short Fx(float value) =>
            (short)Math.Clamp(MathF.Round(value * 4096f), short.MinValue, short.MaxValue);
        return (Fx(x / length), Fx(y / length), Fx(z / length));
    }

    /// <summary>Splice <paramref name="mesh"/> over the model chosen by <paramref name="pickModel"/>
    /// inside <paramref name="fileBytes"/>. Returns the rebuilt package. Throws
    /// <see cref="InvalidDataException"/> (bad package / no model) or
    /// <see cref="InvalidOperationException"/> (budget refusal, with numbers).</summary>
    public static SpliceResult Splice(byte[] fileBytes, Func<DcmModel, bool> pickModel, ImportedMesh mesh,
                                      int vertexCeiling = LiftedVertexCeiling, uint capEnd = DefaultCapEnd)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ArgumentNullException.ThrowIfNull(mesh);
        _ = capEnd;

        var pkg = GianPackage.TryParse(fileBytes)
            ?? throw new InvalidDataException("not a Gian package.");

        // Locate the target model per entry (the splice needs the entry index + its decoded blob).
        DcmModel? target = null;
        byte[] blobSrc = Array.Empty<byte>();
        int entryIdx = -1;
        for (int i = 0; i < pkg.Entries.Count && target is null; i++)
        {
            var dec = pkg.DecodeEntry(fileBytes, pkg.Entries[i]);
            var m = DcmLocator.ScanBuffer(dec, DcmVariant.Dc1).FirstOrDefault(pickModel);
            if (m is not null) { target = m; blobSrc = dec; entryIdx = i; }
        }
        if (target is null)
            throw new InvalidDataException("no matching DC1 model found in the package.");

        int nb = target.Parts.Count;
        uint loadBase = target.LoadBase;
        int modelHead = target.ModelHead;
        var world = ObjExporter.ComputeWorldPositions(target);

        int geomStart = target.Resolve(target.Header.VertexPtr);
        int geomEnd = target.Resolve(target.Header.QuadPtr) + target.Header.QuadCount * DcmVariant.Dc1.QuadStride;
        int stockBudget = geomEnd - geomStart;

        // ---- PRESERVE THE STOCK POOL VERBATIM (order, duplicate records, pads, normals — byte
        // for byte) and map every imported vertex back onto its ORIGINAL stock index via
        // (bone-local position, transform-run part), with exact stock indices preferred from the
        // reserved GLB metadata. A pool the engine cannot distinguish from stock remains safe.
        // Genuinely new/moved vertices are rejected because
        // the runtime transform behavior of appended DC1 pool records is known to be unsafe. ----
        var stockIndexByRef = new Dictionary<(short, short, short, short, short, short, int), int>();
        var ambiguousStockRefs = new HashSet<(short, short, short, short, short, short, int)>();
        {
            for (int vi = 0; vi < target.Vertices.Count; vi++)
            {
                void Add((short, short, short, short, short, short, int) key, int vi)
                {
                    if (stockIndexByRef.TryGetValue(key, out int existing) && existing != vi)
                        ambiguousStockRefs.Add(key);
                    else
                        stockIndexByRef[key] = vi;
                }
                var sv = target.Vertices[vi];
                int vertexPart = sv.Pad;
                if (vertexPart < 0 || vertexPart >= nb) continue; // unreferenced stock run padding
                var n = target.Normals[vi];
                var cn = CanonicalNormal(n.X, n.Y, n.Z);
                Add((sv.X, sv.Y, sv.Z, cn.X, cn.Y, cn.Z, vertexPart), vi);
                // glTF requires a unit normal. The exporter substitutes +Y for a zero vector;
                // after import that is +Y without reflection and -Y with reflection.
                if (n.X == 0 && n.Y == 0 && n.Z == 0)
                    Add((sv.X, sv.Y, sv.Z, 0, (short)-cn.Y, 0, vertexPart), vi);
            }
        }

        int stockCount = target.Vertices.Count;
        var finalIndex = new int[mesh.Vertices.Count];
        var unsupported = new HashSet<(short X, short Y, short Z, short Joint)>();
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            if (v.Joint < 0 || v.Joint >= nb)
                throw new InvalidOperationException($"vertex bound to joint {v.Joint} but the rig has {nb} bones.");
            int lx = v.X - world[v.Joint].X, ly = v.Y - world[v.Joint].Y, lz = v.Z - world[v.Joint].Z;
            if (lx is < short.MinValue or > short.MaxValue || ly is < short.MinValue or > short.MaxValue ||
                lz is < short.MinValue or > short.MaxValue)
                throw new InvalidOperationException(
                    $"vertex at world ({v.X},{v.Y},{v.Z}) is {Math.Max(Math.Abs(lx), Math.Max(Math.Abs(ly), Math.Abs(lz)))} " +
                    $"units from its bone {v.Joint} — beyond int16 bone-local range. The mesh drifted " +
                    "too far from the skeleton (check scale / accidental move of the whole object).");

            if (v.StockIndex >= 0)
            {
                if (v.StockIndex >= target.Vertices.Count)
                    throw new InvalidOperationException(
                        $"vertex stock index {v.StockIndex} is outside the target pool of {target.Vertices.Count} records.");
                var stock = target.Vertices[v.StockIndex];
                int stockPart = stock.Pad;
                if (stockPart >= 0 && stockPart < nb && v.Joint == stockPart &&
                    v.X == stock.X + world[stockPart].X && v.Y == stock.Y + world[stockPart].Y &&
                    v.Z == stock.Z + world[stockPart].Z)
                {
                    finalIndex[i] = v.StockIndex;
                    continue;
                }
                unsupported.Add(((short)lx, (short)ly, (short)lz, v.Joint));
                finalIndex[i] = -1;
                continue;
            }

            var key = ((short)lx, (short)ly, (short)lz, v.NX, v.NY, v.NZ, (int)v.Joint);
            if (ambiguousStockRefs.Contains(key))
            {
                throw new InvalidOperationException(
                    "DC1 stock vertex mapping is ambiguous after glTF normal unitization; " +
                    "coincident stock records with indistinguishable normal directions cannot be safely selected.");
            }
            if (stockIndexByRef.TryGetValue(key, out int si))
            {
                finalIndex[i] = si;
            }
            else
            {
                unsupported.Add(((short)lx, (short)ly, (short)lz, v.Joint));
                finalIndex[i] = -1; // The refusal below runs before polygon indices are emitted.
            }
        }
        if (unsupported.Count > 0)
            throw new InvalidOperationException(
                $"DC1 moved/new vertices are unsafe: {unsupported.Count} vertex record(s) do not map to the stock pool. " +
                "Only UV and polygon-topology edits over stock vertices are supported.");
        int vc = stockCount;

        // ---- gates (actionable numbers, per the P2 refusal contract) ----
        if (vc > vertexCeiling)
            throw new InvalidOperationException(
                $"{vc} vertices exceed the renderer ceiling of {vertexCeiling} " +
                $"(unpatched installs: 400). Decimate the mesh by at least {vc - vertexCeiling} vertices.");
        int poolBytes = vc * 16; // 8 B SVECTOR position + 8 B normal, parallel pools
        if (poolBytes > stockBudget)
            throw new InvalidOperationException(
                $"vertex+normal pools need {poolBytes} B but the stock geometry region holds " +
                $"{stockBudget} B ({stockBudget / 16} vertices max for this target).");

        int tN = mesh.Triangles.Count, qN = mesh.Quads.Count;
        int geometryBytes = checked(poolBytes + tN * DcmVariant.Dc1.TriangleStride +
                                    qN * DcmVariant.Dc1.QuadStride);
        if (geometryBytes > stockBudget)
            throw new InvalidOperationException(
                $"geometry needs {geometryBytes} B but the stock region holds {stockBudget} B " +
                $"({vc} verts ×16 + {tN} tris ×16 + {qN} quads ×20). " +
                $"Reduce polygons by at least {geometryBytes - stockBudget} B.");

        int Final(int meshVertexIndex) => finalIndex[meshVertexIndex];

        int maxReferencedRawVertexIndex = -1;
        foreach (var t in mesh.Triangles)
        {
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(t.V0));
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(t.V1));
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(t.V2));
        }
        foreach (var q in mesh.Quads)
        {
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(q.V0));
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(q.V1));
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(q.V2));
            maxReferencedRawVertexIndex = Math.Max(maxReferencedRawVertexIndex, Final(q.V3));
        }

        var triByBone = new List<ImportedMesh.Triangle>[nb];
        var quadByBone = new List<ImportedMesh.Quad>[nb];
        for (int b = 0; b < nb; b++) { triByBone[b] = new(); quadByBone[b] = new(); }
        Span<short> jbuf = stackalloc short[4];
        foreach (var t in mesh.Triangles)
        {
            jbuf[0] = mesh.Vertices[t.V0].Joint; jbuf[1] = mesh.Vertices[t.V1].Joint; jbuf[2] = mesh.Vertices[t.V2].Joint;
            int part = t.Part >= 0 ? t.Part : Majority(jbuf[..3]);
            if (part >= nb) throw new InvalidOperationException($"triangle polygon part {part} is outside the target rig.");
            triByBone[part].Add(t);
        }
        foreach (var q in mesh.Quads)
        {
            jbuf[0] = mesh.Vertices[q.V0].Joint; jbuf[1] = mesh.Vertices[q.V1].Joint;
            jbuf[2] = mesh.Vertices[q.V2].Joint; jbuf[3] = mesh.Vertices[q.V3].Joint;
            int part = q.Part >= 0 ? q.Part : Majority(jbuf);
            if (part >= nb) throw new InvalidOperationException($"quad polygon part {part} is outside the target rig.");
            quadByBone[part].Add(q);
        }

        // ---- emit ----
        var blob = (byte[])blobSrc.Clone();
        void WU16(int o, int val) => BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(o, 2), (ushort)val);
        void W32(int o, uint val) => BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(o, 4), val);

        // Stock vertex and normal arrays are copied byte-verbatim from the source blob; any
        // record-level rewrite risks the engine's unproven pool semantics.
        int vtxOff = geomStart, nrmOff = vtxOff + vc * 8;
        int triOff = nrmOff + vc * 8;
        int quadOff = triOff + tN * DcmVariant.Dc1.TriangleStride;
        int newGeomEnd = quadOff + qN * DcmVariant.Dc1.QuadStride;
        int stockVtx = target.Resolve(target.Header.VertexPtr);
        int stockNrm = target.Resolve(target.Header.NormalPtr);
        Array.Copy(blobSrc, stockVtx, blob, vtxOff, stockCount * 8);
        Array.Copy(blobSrc, stockNrm, blob, nrmOff, stockCount * 8);

        int tcur = triOff;
        foreach (var bucket in triByBone)
            foreach (var t in bucket)
            {
                int o = tcur; tcur += 16;
                WU16(o, Final(t.V0)); WU16(o + 2, Final(t.V1)); WU16(o + 4, Final(t.V2));
                blob[o + 6] = t.Uv0.U; blob[o + 7] = t.Uv0.V;
                blob[o + 8] = t.Uv1.U; blob[o + 9] = t.Uv1.V;
                WU16(o + 0xa, t.Clut);
                blob[o + 0xc] = t.Uv2.U; blob[o + 0xd] = t.Uv2.V;
                WU16(o + 0xe, t.TPage);
            }
        int qcur = quadOff;
        foreach (var bucket in quadByBone)
            foreach (var q in bucket)
            {
                int o = qcur; qcur += 20;
                WU16(o, Final(q.V0)); WU16(o + 2, Final(q.V1)); WU16(o + 4, Final(q.V2)); WU16(o + 6, Final(q.V3));
                blob[o + 8] = q.Uv0.U; blob[o + 9] = q.Uv0.V;
                WU16(o + 0xa, q.Clut);
                blob[o + 0xc] = q.Uv1.U; blob[o + 0xd] = q.Uv1.V;
                WU16(o + 0xe, q.TPage);
                blob[o + 0x10] = q.Uv2.U; blob[o + 0x11] = q.Uv2.V;
                blob[o + 0x12] = q.Uv3.U; blob[o + 0x13] = q.Uv3.V;
            }
        for (int o = newGeomEnd; o < geomEnd; o++) blob[o] = 0;

        W32(modelHead + 0x00, loadBase + (uint)vtxOff);
        W32(modelHead + 0x04, loadBase + (uint)nrmOff);
        W32(modelHead + 0x08, loadBase + (uint)triOff);
        W32(modelHead + 0x0c, loadBase + (uint)quadOff);
        WU16(modelHead + 0x10, tN);
        WU16(modelHead + 0x12, qN);
        uint tptr = loadBase + (uint)triOff, qptr = loadBase + (uint)quadOff;
        for (int b = 0; b < nb; b++)
        {
            int o = modelHead + 0x18 + b * 0x14; // rest xyz + parent/child bytes stay verbatim
            W32(o + 8, tptr); W32(o + 0xc, qptr);
            WU16(o + 0x10, triByBone[b].Count); WU16(o + 0x12, quadByBone[b].Count);
            tptr += (uint)(triByBone[b].Count * 16);
            qptr += (uint)(quadByBone[b].Count * 20);
        }

        // ---- self-verify (the probes' oracle set) ----
        if (!blob.AsSpan(geomEnd).SequenceEqual(blobSrc.AsSpan(geomEnd)))
            throw new InvalidOperationException("internal: animation/motion tail not byte-exact after splice.");
        var reread = DcmModel.Read(blob, modelHead, loadBase, DcmVariant.Dc1);
        if (!DcmModel.Validate(reread))
            throw new InvalidOperationException("internal: spliced model fails Validate.");
        if (!ObjExporter.ComputeWorldPositions(reread).SequenceEqual(world))
            throw new InvalidOperationException("internal: target skeleton not preserved verbatim.");

        // ---- re-encode the entry + rebuild the package ----
        var entry = pkg.Entries[entryIdx];
        byte[] payload = entry.IsCompressed ? Lzss.Compress(blob) : blob;
        if (entry.IsCompressed && !Lzss.Decompress(payload).AsSpan().SequenceEqual(blob))
            throw new InvalidOperationException("internal: LZSS round-trip failed.");

        var builder = new GianPackageBuilder();
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            byte[] data = i == entryIdx ? payload
                : fileBytes.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();
            builder.Add(e.Type, data, e.GfxX, e.GfxY, e.GfxW, e.GfxH);
        }
        byte[] injected = builder.Build();

        var report = new StringBuilder()
            .AppendLine($"spliced: {vc} verts (ceiling {vertexCeiling}), {tN} tris, {qN} quads")
            .AppendLine($"geometry {geometryBytes}/{stockBudget} B of the stock region; blob {blob.Length} B (stock size kept)")
            .AppendLine(Dc1PacketContribution.Format(tN, qN))
            .AppendLine($"maximum referenced raw vertex index: " +
                        (maxReferencedRawVertexIndex >= 0
                            ? maxReferencedRawVertexIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "none"))
            .AppendLine("skeleton+animation verbatim")
            .ToString();
        return new SpliceResult { Package = injected, Vertices = vc, Triangles = tN, Quads = qN, Report = report };
    }
}
