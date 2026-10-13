using System.Buffers.Binary;
using DinoCrisis.Model;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

public enum Re2DonorProvenanceKind
{
    NativeOrCopyCandidate,
    ReserializedNativeCandidate,
    CrossGameConversion,
    Custom,
    Unknown,
}

public sealed record Re2DonorProvenance(Re2DonorProvenanceKind Kind, string? Detail = null);

public enum Re2SiblingRelationship
{
    NotPathLike,
    Valid,
    Invalid,
}

public sealed record Re2EmdIdentity(
    bool ExactV2DirectoryClosure,
    int OffsetCount,
    BioVersion ParsedFamily);

public sealed record Re2ChunkFact(
    int Index,
    ModelFile.ChunkKind Kind,
    int Offset,
    int Length);

public readonly record struct Re2Vector(short X, short Y, short Z);

public sealed record Re2Md1PartFact(
    int PartIndex,
    int PositionCount,
    int NormalCount,
    int TriangleCount,
    int QuadCount,
    int ReferencedPositionCount,
    int UnreferencedPositionCount,
    int MultiNormalPositionCount);

public sealed record Re2EmrFact(
    int ChunkIndex,
    IReadOnlyList<Re2Vector> RelativeOffsets,
    IReadOnlyList<int> Parents,
    IReadOnlyList<IReadOnlyList<int>> ChildOwnership);

public sealed record Re2SeamEdgeFact(
    int ParentPart,
    int ChildPart,
    double RawMinimum,
    double FkRestMinimum);

public sealed record Re2SeamAggregate(double Minimum, double Mean, double Maximum);

public sealed record Re2SeamFacts(
    IReadOnlyList<Re2SeamEdgeFact> Edges,
    Re2SeamAggregate Raw,
    Re2SeamAggregate FkRest);

public enum Re2PrimitiveKind
{
    Triangle,
    Quad,
}

public sealed record Re2UvBounds(byte MinimumU, byte MinimumV, byte MaximumU, byte MaximumV);

public sealed record Re2MaterialFact(
    int PartIndex,
    Re2PrimitiveKind PrimitiveKind,
    byte LowNibblePage,
    ushort RawClut,
    int PrimitiveCount,
    int CornerCount,
    Re2UvBounds UvBounds,
    int? PaletteRow,
    bool TimRectangleCovered,
    bool IsZeroUvClutZeroException);

public sealed record Re2TextureFacts(
    bool IsComplete,
    bool MaterialSemanticsResolved,
    string? RefusalReason,
    int? PixelFormat,
    int? Width,
    int? Height,
    int? ImageOriginX,
    int? ImageOriginY,
    int? PaletteOriginX,
    int? PaletteOriginY,
    int? PaletteRows,
    IReadOnlyList<Re2MaterialFact> Materials);

public sealed record Re2TopologyMapping(
    IReadOnlyList<int?> SourceMeshToTarget,
    bool IsComplete);

public sealed record Re2CapacityFacts(
    int RawRecordCount,
    IReadOnlyList<int> PerPartPaddedRecordCounts,
    int PaddedRecordCount,
    int RawAllPoolGeometryBytes,
    int PaddedAllPoolGeometryBytes,
    int PolygonPoolBytes,
    int PacketContributionBytes,
    bool Stock400Fits,
    bool Padded400Fits,
    bool Padded1024Fits,
    bool RawStockRegionFits,
    bool PaddedStockRegionFits,
    ulong RawDecodedEndAddress,
    ulong PaddedDecodedEndAddress,
    bool RawDecodedEndFits,
    bool PaddedDecodedEndFits);

public sealed record Re2EmdPreflightResult(
    string EmdSource,
    string TimSource,
    Re2DonorProvenance Provenance,
    Re2SiblingRelationship SiblingRelationship,
    Re2EmdIdentity Identity,
    IReadOnlyList<Re2ChunkFact> Chunks,
    IReadOnlyList<Re2Md1PartFact> MeshParts,
    int TotalMultiNormalPositionCount,
    IReadOnlyList<Re2EmrFact> Armatures,
    IReadOnlyList<int> MeshPartsBeyondEmr,
    Re2SeamFacts Seams,
    Re2TextureFacts Texture,
    IReadOnlyList<Re2TopologyMapping> TopologyMappings,
    Re2CapacityFacts Capacity,
    bool CoordinatePolicySelected,
    bool BindingReady,
    bool TransformRunLegal,
    IReadOnlyList<string> RefusalReasons);

/// <summary>
/// Read-only RE2 EMD measurement boundary. It never constructs an interchange mesh, selects a
/// coordinate/atlas policy, edits the target, or emits model/package bytes.
/// </summary>
public static class Re2EmdPreflight
{
    public const int StockRecordLimit = 400;
    public const int LiftedRecordLimit = 1024;
    public const int StockGeometryRegionBytes = 12_884;

    private sealed record PrimitiveSample(
        int PartIndex,
        Re2PrimitiveKind Kind,
        byte Page,
        ushort Clut,
        IReadOnlyList<(byte U, byte V)> Corners);

    private sealed record MeshMeasurement(
        IReadOnlyList<Re2Md1PartFact> Parts,
        IReadOnlyList<IReadOnlyList<Md1.Vector>> Positions,
        IReadOnlyList<PrimitiveSample> Primitives);

    private sealed record TimMeasurement(
        int PixelFormat,
        int Width,
        int Height,
        int TexelsPerWord,
        int ImageOriginX,
        int ImageOriginY,
        int PaletteOriginX,
        int PaletteOriginY,
        int PaletteRows);

    public static Re2EmdPreflightResult Analyze(
        ReadOnlyMemory<byte> emd,
        string emdSource,
        ReadOnlyMemory<byte> siblingTim,
        string timSource,
        DcmModel target,
        Re2DonorProvenance provenance,
        uint maximumDecodedEndAddress)
    {
        ArgumentNullException.ThrowIfNull(emdSource);
        ArgumentNullException.ThrowIfNull(timSource);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provenance);
        Require(target.Variant.Game == DcmGame.Dc1 && DcmModel.Validate(target),
            "target must be a strictly parsed DC1 model");

        byte[] emdBytes = emd.ToArray();
        var (identity, chunks, file) = ParseEmd(emdBytes);
        var mesh = MeasureMesh(file.Md1);
        var armatures = new[] { MeasureEmr(chunks[2].Index, file.GetEmr(0)) };

        var primaryArmature = armatures[0];
        int[] extraMeshParts = Enumerable.Range(primaryArmature.Parents.Count,
                Math.Max(0, mesh.Parts.Count - primaryArmature.Parents.Count))
            .ToArray();
        var seams = MeasureSeams(mesh.Positions, primaryArmature);
        var siblingRelationship = ValidateSiblingRelationship(emdSource, timSource);
        var texture = MeasureTexture(siblingTim, siblingRelationship, mesh.Primitives);
        var mappings = EnumerateMappings(
            primaryArmature.Parents,
            target.Parts.Select(part => (int)part.Parent).ToArray(),
            mesh.Parts.Count);
        var capacity = MeasureCapacity(mesh.Parts, target, maximumDecodedEndAddress);
        int totalConflicts = mesh.Parts.Sum(part => part.MultiNormalPositionCount);

        var reasons = new List<string>
        {
            "RE2 coordinate axis, pivot, forward, determinant, normal, and winding contracts are unresolved.",
            "Topology mappings are index-only and do not establish semantic part identity or ownership.",
            "No caller-supplied exact target-part ownership partition establishes DC1 transform-run legality.",
            "RE2 binding, animation, attachment, and runtime contracts remain unresolved.",
        };
        if (totalConflicts != 0)
            reasons.Add($"MD1 has {totalConflicts} position record(s) referenced through multiple corner-normal indices.");
        if (mappings.Count == 0)
            reasons.Add("The source EMR and target DCM rooted trees have no topology-compatible index mapping.");
        if (extraMeshParts.Length != 0)
            reasons.Add($"Mesh parts {string.Join(", ", extraMeshParts)} are beyond the primary EMR and remain unmapped.");
        if (!texture.IsComplete)
            reasons.Add("Texture-complete analysis refused: " + texture.RefusalReason);
        if (!texture.MaterialSemanticsResolved)
            reasons.Add("Zero-UV/CLUT-zero material semantics remain unresolved.");

        return new Re2EmdPreflightResult(
            emdSource,
            timSource,
            provenance,
            siblingRelationship,
            identity,
            Array.AsReadOnly(chunks),
            mesh.Parts,
            totalConflicts,
            Array.AsReadOnly(armatures),
            Array.AsReadOnly(extraMeshParts),
            seams,
            texture,
            mappings,
            capacity,
            CoordinatePolicySelected: false,
            BindingReady: false,
            TransformRunLegal: false,
            Array.AsReadOnly(reasons.ToArray()));
    }

    private static (Re2EmdIdentity Identity, Re2ChunkFact[] Chunks, EmdFile File) ParseEmd(byte[] bytes)
    {
        Require(bytes.Length >= 8, "input is too small for a V2 header");
        int directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        int offsetCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        Require(directoryOffset >= 8 && offsetCount > 0 && offsetCount < 64 &&
                (long)directoryOffset + (long)offsetCount * 4 == bytes.Length,
            "input does not have exact V2 directory closure");
        Require(offsetCount == 8,
            offsetCount is 4 or 5 or 9
                ? $"{offsetCount}-offset PLD/PLW input is not an RE2 EMD"
                : $"expected exactly eight RE2 EMD offsets, found {offsetCount}");

        var offsets = new int[offsetCount];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(directoryOffset + i * 4, 4));
        for (int i = 0; i < offsets.Length; i++)
        {
            int next = i + 1 == offsets.Length ? directoryOffset : offsets[i + 1];
            Require(offsets[i] >= 8 && offsets[i] <= next && next <= directoryOffset,
                $"chunk {i} offset/span is outside the ordered V2 payload");
        }

        EmdFile file;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            file = new EmdFile(BioVersion.Biohazard2, stream);
            Require(file.NumChunks == 8, $"parsed EMD contains {file.NumChunks} chunks instead of eight");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("RE2 EMD preflight: Biohazard2 EMD parsing failed", exception);
        }

        var chunks = new Re2ChunkFact[offsetCount];
        for (int i = 0; i < chunks.Length; i++)
        {
            int end = i + 1 == offsets.Length ? directoryOffset : offsets[i + 1];
            chunks[i] = new Re2ChunkFact(i, file.GetChunkKind(i), offsets[i], end - offsets[i]);
        }
        return (new Re2EmdIdentity(true, offsetCount, BioVersion.Biohazard2), chunks, file);
    }

    private static MeshMeasurement MeasureMesh(Md1 md1)
    {
        try
        {
            ReadOnlySpan<byte> data = md1.Data.Span;
            Require(data.Length >= 12, "MD1 header is truncated");
            int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data);
            int objectCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
            Require(objectCount > 0 && (objectCount & 1) == 0, "MD1 object count must be a positive triangle/quad pair count");
            int descriptorEnd = checked(12 + objectCount * 28);
            Require(declaredLength >= descriptorEnd && declaredLength <= data.Length,
                "MD1 declared geometry length is outside the chunk");

            var facts = new List<Re2Md1PartFact>(objectCount / 2);
            var positionsByPart = new List<IReadOnlyList<Md1.Vector>>(objectCount / 2);
            var primitives = new List<PrimitiveSample>();
            for (int partIndex = 0; partIndex < objectCount / 2; partIndex++)
            {
                Md1.ObjectDescriptor triangleObject = md1.Objects[partIndex * 2];
                Md1.ObjectDescriptor quadObject = md1.Objects[partIndex * 2 + 1];
                Require(triangleObject.vtx_offset == quadObject.vtx_offset &&
                        triangleObject.vtx_count == quadObject.vtx_count &&
                        triangleObject.nor_offset == quadObject.nor_offset &&
                        triangleObject.nor_count == quadObject.nor_count,
                    $"MD1 part {partIndex} triangle/quad objects do not share position/normal pools");
                Require(triangleObject.vtx_count >= 0 && triangleObject.nor_count >= 0 &&
                        triangleObject.pri_count >= 0 && quadObject.pri_count >= 0,
                    $"MD1 part {partIndex} has a negative count");

                CheckMd1Span(data.Length, triangleObject.vtx_offset, triangleObject.vtx_count, 8, partIndex, "positions");
                CheckMd1Span(data.Length, triangleObject.nor_offset, triangleObject.nor_count, 8, partIndex, "normals");
                CheckMd1Span(data.Length, triangleObject.pri_offset, triangleObject.pri_count, 12, partIndex, "triangles");
                CheckMd1Span(data.Length, triangleObject.tex_offset, triangleObject.pri_count, 12, partIndex, "triangle textures");
                CheckMd1Span(data.Length, quadObject.pri_offset, quadObject.pri_count, 16, partIndex, "quads");
                CheckMd1Span(data.Length, quadObject.tex_offset, quadObject.pri_count, 16, partIndex, "quad textures");

                Md1.Vector[] positions = md1.GetPositionData(triangleObject).ToArray();
                Md1.Vector[] normals = md1.GetNormalData(triangleObject).ToArray();
                var referencedNormals = Enumerable.Range(0, positions.Length)
                    .Select(_ => new HashSet<ushort>()).ToArray();

                Md1.Triangle[] triangles = md1.GetTriangles(triangleObject).ToArray();
                Md1.TriangleTexture[] triangleTextures = md1.GetTriangleTextures(triangleObject).ToArray();
                for (int i = 0; i < triangles.Length; i++)
                {
                    Md1.Triangle triangle = triangles[i];
                    CheckCorner(partIndex, triangle.v0, triangle.n0, positions.Length, normals.Length, referencedNormals);
                    CheckCorner(partIndex, triangle.v1, triangle.n1, positions.Length, normals.Length, referencedNormals);
                    CheckCorner(partIndex, triangle.v2, triangle.n2, positions.Length, normals.Length, referencedNormals);
                    Md1.TriangleTexture texture = triangleTextures[i];
                    primitives.Add(new PrimitiveSample(partIndex, Re2PrimitiveKind.Triangle,
                        (byte)(texture.page & 0x0F), texture.clutId,
                        Array.AsReadOnly(new[] { (texture.u0, texture.v0), (texture.u1, texture.v1), (texture.u2, texture.v2) })));
                }

                Md1.Quad[] quads = md1.GetQuads(quadObject).ToArray();
                Md1.QuadTexture[] quadTextures = md1.GetQuadTextures(quadObject).ToArray();
                for (int i = 0; i < quads.Length; i++)
                {
                    Md1.Quad quad = quads[i];
                    CheckCorner(partIndex, quad.v0, quad.n0, positions.Length, normals.Length, referencedNormals);
                    CheckCorner(partIndex, quad.v1, quad.n1, positions.Length, normals.Length, referencedNormals);
                    CheckCorner(partIndex, quad.v2, quad.n2, positions.Length, normals.Length, referencedNormals);
                    CheckCorner(partIndex, quad.v3, quad.n3, positions.Length, normals.Length, referencedNormals);
                    Md1.QuadTexture texture = quadTextures[i];
                    primitives.Add(new PrimitiveSample(partIndex, Re2PrimitiveKind.Quad,
                        (byte)(texture.page & 0x0F), texture.clutId,
                        Array.AsReadOnly(new[]
                        {
                            (texture.u0, texture.v0), (texture.u1, texture.v1),
                            (texture.u2, texture.v2), (texture.u3, texture.v3),
                        })));
                }

                int referenced = referencedNormals.Count(set => set.Count != 0);
                facts.Add(new Re2Md1PartFact(
                    partIndex, positions.Length, normals.Length, triangles.Length, quads.Length,
                    referenced, positions.Length - referenced, referencedNormals.Count(set => set.Count > 1)));
                positionsByPart.Add(Array.AsReadOnly(positions));
            }
            return new MeshMeasurement(
                Array.AsReadOnly(facts.ToArray()),
                Array.AsReadOnly(positionsByPart.ToArray()),
                Array.AsReadOnly(primitives.ToArray()));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("RE2 EMD preflight: malformed MD1 chunk", exception);
        }
    }

    private static void CheckMd1Span(int dataLength, int relativeOffset, int count, int stride, int part, string name)
    {
        long offset = 12L + relativeOffset;
        long end = offset + (long)count * stride;
        Require(offset >= 12 && end >= offset && end <= dataLength,
            $"MD1 part {part} {name} span is outside the chunk");
    }

    private static void CheckCorner(
        int part,
        ushort position,
        ushort normal,
        int positionCount,
        int normalCount,
        HashSet<ushort>[] referencedNormals)
    {
        Require(position < positionCount, $"MD1 part {part} references position {position} outside {positionCount}");
        Require(normal < normalCount, $"MD1 part {part} references normal {normal} outside {normalCount}");
        referencedNormals[position].Add(normal);
    }

    private static Re2EmrFact MeasureEmr(int chunkIndex, Emr emr)
    {
        try
        {
            Require(emr.Data.Length >= 8, $"EMR chunk {chunkIndex} header is truncated");
            int partCount = emr.NumParts;
            Require(partCount > 0, $"EMR chunk {chunkIndex} has no parts");
            int positionsEnd = checked(8 + partCount * 6);
            Require(positionsEnd <= emr.Data.Length, $"EMR chunk {chunkIndex} relative offsets are truncated");
            int physicalArmatureEnd = Math.Min(emr.KeyFrameOffset, emr.Data.Length);
            int alignedLength = checked((emr.Data.Length + 3) / 4 * 4);
            Require(emr.ArmatureOffset >= positionsEnd && emr.ArmatureOffset <= emr.KeyFrameOffset &&
                    emr.ArmatureOffset <= emr.Data.Length && emr.KeyFrameOffset <= alignedLength,
                $"EMR chunk {chunkIndex} armature/keyframe offsets are invalid");
            Require((long)emr.ArmatureOffset + partCount * 4 <= physicalArmatureEnd,
                $"EMR chunk {chunkIndex} child-ownership table is truncated");

            var relative = new Re2Vector[partCount];
            var parents = Enumerable.Repeat(-1, partCount).ToArray();
            var children = new IReadOnlyList<int>[partCount];
            for (int part = 0; part < partCount; part++)
            {
                Emr.Vector position = emr.GetRelativePosition(part);
                relative[part] = new Re2Vector(position.x, position.y, position.z);
                Emr.Armature armature = emr.GetArmature(part);
                Require(armature.count >= 0 && armature.offset >= 0 &&
                        (long)emr.ArmatureOffset + armature.offset + armature.count <= physicalArmatureEnd,
                    $"EMR chunk {chunkIndex} part {part} child span is invalid");
                int[] owned = emr.GetArmatureParts(part).ToArray().Select(value => (int)value).ToArray();
                Require(owned.Distinct().Count() == owned.Length,
                    $"EMR chunk {chunkIndex} part {part} repeats a child");
                foreach (int child in owned)
                {
                    Require(child > 0 && child < partCount,
                        $"EMR chunk {chunkIndex} part {part} owns invalid/root child {child}");
                    Require(parents[child] == -1,
                        $"EMR chunk {chunkIndex} child {child} has multiple owners");
                    parents[child] = part;
                }
                children[part] = Array.AsReadOnly(owned);
            }
            Require(parents[0] == -1 && Enumerable.Range(1, partCount - 1).All(part => parents[part] >= 0),
                $"EMR chunk {chunkIndex} is not one rooted tree at part 0");
            for (int part = 1; part < partCount; part++)
            {
                var seen = new HashSet<int>();
                int cursor = part;
                while (cursor != 0)
                {
                    Require(seen.Add(cursor), $"EMR chunk {chunkIndex} hierarchy contains a cycle");
                    cursor = parents[cursor];
                }
            }
            return new Re2EmrFact(
                chunkIndex,
                Array.AsReadOnly(relative),
                Array.AsReadOnly(parents),
                Array.AsReadOnly(children));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException($"RE2 EMD preflight: malformed EMR chunk {chunkIndex}", exception);
        }
    }

    private static Re2SeamFacts MeasureSeams(
        IReadOnlyList<IReadOnlyList<Md1.Vector>> positions,
        Re2EmrFact armature)
    {
        var world = new (int X, int Y, int Z)[armature.Parents.Count];
        var resolved = new bool[world.Length];
        (int X, int Y, int Z) ResolveWorld(int part)
        {
            if (resolved[part]) return world[part];
            Re2Vector relative = armature.RelativeOffsets[part];
            int parent = armature.Parents[part];
            world[part] = parent < 0
                ? (relative.X, relative.Y, relative.Z)
                : Add(ResolveWorld(parent), relative);
            resolved[part] = true;
            return world[part];
        }
        for (int part = 0; part < world.Length; part++) ResolveWorld(part);

        var edges = new List<Re2SeamEdgeFact>();
        for (int child = 1; child < armature.Parents.Count && child < positions.Count; child++)
        {
            int parent = armature.Parents[child];
            if (parent < 0 || parent >= positions.Count) continue;
            double raw = MinimumDistance(positions[parent], positions[child], default, default);
            double fk = MinimumDistance(positions[parent], positions[child], world[parent], world[child]);
            edges.Add(new Re2SeamEdgeFact(parent, child, raw, fk));
        }

        return new Re2SeamFacts(
            Array.AsReadOnly(edges.ToArray()),
            Aggregate(edges.Select(edge => edge.RawMinimum)),
            Aggregate(edges.Select(edge => edge.FkRestMinimum)));

        static (int X, int Y, int Z) Add((int X, int Y, int Z) left, Re2Vector right)
            => (left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    private static double MinimumDistance(
        IReadOnlyList<Md1.Vector> left,
        IReadOnlyList<Md1.Vector> right,
        (int X, int Y, int Z) leftOffset,
        (int X, int Y, int Z) rightOffset)
    {
        if (left.Count == 0 || right.Count == 0) return double.NaN;
        // ponytail: exact Cartesian seam scan; add a spatial index only if larger donor pools profile poorly.
        double minimumSquared = double.PositiveInfinity;
        foreach (Md1.Vector a in left)
            foreach (Md1.Vector b in right)
            {
                long x = (long)a.x + leftOffset.X - b.x - rightOffset.X;
                long y = (long)a.y + leftOffset.Y - b.y - rightOffset.Y;
                long z = (long)a.z + leftOffset.Z - b.z - rightOffset.Z;
                double squared = (double)x * x + (double)y * y + (double)z * z;
                if (squared < minimumSquared) minimumSquared = squared;
            }
        return Math.Sqrt(minimumSquared);
    }

    private static Re2SeamAggregate Aggregate(IEnumerable<double> values)
    {
        double[] finite = values.Where(double.IsFinite).ToArray();
        return finite.Length == 0
            ? new Re2SeamAggregate(double.NaN, double.NaN, double.NaN)
            : new Re2SeamAggregate(finite.Min(), finite.Average(), finite.Max());
    }

    private static Re2SiblingRelationship ValidateSiblingRelationship(string emdSource, string timSource)
    {
        var emd = SplitLabel(emdSource);
        var tim = SplitLabel(timSource);
        if (!emd.PathLike && !tim.PathLike) return Re2SiblingRelationship.NotPathLike;
        if (!emd.PathLike || !tim.PathLike) return Re2SiblingRelationship.Invalid;
        return emd.Extension.Equals(".emd", StringComparison.OrdinalIgnoreCase) &&
               tim.Extension.Equals(".tim", StringComparison.OrdinalIgnoreCase) &&
               emd.Directory.Equals(tim.Directory, StringComparison.OrdinalIgnoreCase) &&
               emd.Stem.Equals(tim.Stem, StringComparison.OrdinalIgnoreCase)
            ? Re2SiblingRelationship.Valid
            : Re2SiblingRelationship.Invalid;
    }

    private static (bool PathLike, string Directory, string Stem, string Extension) SplitLabel(string label)
    {
        string normalized = label.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        string directory = slash < 0 ? string.Empty : normalized[..slash].TrimEnd('/');
        string file = normalized[(slash + 1)..];
        int dot = file.LastIndexOf('.');
        bool pathLike = slash >= 0 || dot > 0;
        return dot > 0
            ? (pathLike, directory, file[..dot], file[dot..])
            : (pathLike, directory, file, string.Empty);
    }

    private static Re2TextureFacts MeasureTexture(
        ReadOnlyMemory<byte> bytes,
        Re2SiblingRelationship siblingRelationship,
        IReadOnlyList<PrimitiveSample> primitives)
    {
        TimMeasurement? tim = null;
        string? refusal = siblingRelationship == Re2SiblingRelationship.Invalid
            ? "EMD and TIM source labels are not a case-insensitive sibling pair"
            : null;
        if (bytes.IsEmpty)
        {
            refusal = JoinRefusal(refusal, "sibling TIM is missing");
        }
        else
        {
            try
            {
                tim = ParseTim(bytes.ToArray());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                refusal = JoinRefusal(refusal, "sibling TIM is malformed or unsupported: " + exception.Message);
            }
        }

        var materials = primitives
            .GroupBy(sample => new
            {
                sample.PartIndex,
                sample.Kind,
                sample.Page,
                sample.Clut,
                Exception = sample.Clut == 0 && sample.Corners.All(corner => corner == (0, 0)),
            })
            .OrderBy(group => group.Key.PartIndex)
            .ThenBy(group => group.Key.Kind)
            .ThenBy(group => group.Key.Page)
            .ThenBy(group => group.Key.Clut)
            .Select(group =>
            {
                var corners = group.SelectMany(sample => sample.Corners).ToArray();
                int? paletteRow = tim is null ? null : ResolvePaletteRow(tim, group.Key.Clut);
                bool coverage = tim is not null && corners.All(corner => IsCovered(tim, group.Key.Page, corner));
                return new Re2MaterialFact(
                    group.Key.PartIndex,
                    group.Key.Kind,
                    group.Key.Page,
                    group.Key.Clut,
                    group.Count(),
                    corners.Length,
                    new Re2UvBounds(
                        corners.Min(corner => corner.U), corners.Min(corner => corner.V),
                        corners.Max(corner => corner.U), corners.Max(corner => corner.V)),
                    paletteRow,
                    coverage,
                    group.Key.Exception);
            })
            .ToArray();

        if (tim is not null)
        {
            if (materials.Any(material => !material.IsZeroUvClutZeroException && !material.TimRectangleCovered))
                refusal = JoinRefusal(refusal, "one or more material UV/page rectangles are outside the TIM image");
            if (materials.Any(material => !material.IsZeroUvClutZeroException && material.PaletteRow is null))
                refusal = JoinRefusal(refusal, "one or more material CLUTs are outside the TIM palette rows");
        }

        bool complete = tim is not null && refusal is null;
        bool semanticsResolved = complete && materials.All(material => !material.IsZeroUvClutZeroException);
        return new Re2TextureFacts(
            complete,
            semanticsResolved,
            refusal,
            tim?.PixelFormat,
            tim?.Width,
            tim?.Height,
            tim?.ImageOriginX,
            tim?.ImageOriginY,
            tim?.PaletteOriginX,
            tim?.PaletteOriginY,
            tim?.PaletteRows,
            Array.AsReadOnly(materials));
    }

    private static TimMeasurement ParseTim(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        Require(data.Length >= 20, "TIM header is truncated");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(data) == 16, "TIM magic is invalid");
        int pixelFormat = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)));
        Require(pixelFormat == 9, $"RE2 EMD sibling TIM must be indexed 8bpp, found format {pixelFormat}");
        uint clutSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
        int paletteOriginX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
        int paletteOriginY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        int colours = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(16, 2));
        int rows = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18, 2));
        Require(colours == 256 && rows > 0 && clutSize == 12L + (long)colours * rows * 2,
            "TIM CLUT dimensions/size are inconsistent");
        int imageHeader = checked(8 + (int)clutSize);
        Require((long)imageHeader + 12 <= data.Length, "TIM image header is truncated");
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(imageHeader, 4));
        int imageOriginX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(imageHeader + 4, 2));
        int imageOriginY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(imageHeader + 6, 2));
        int widthWords = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(imageHeader + 8, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(imageHeader + 10, 2));
        int width = checked(widthWords * 2);
        Require(width > 0 && height > 0 && imageSize == 12L + (long)width * height,
            "TIM image dimensions/size are inconsistent");
        Require((long)imageHeader + imageSize == data.Length, "TIM has a truncated image or trailing data");

        var parsed = new TimFile(bytes);
        Require(parsed.PixelFormat == pixelFormat && parsed.Width == width && parsed.Height == height &&
                parsed.PaletteCount == rows && parsed.ImageOriginX == imageOriginX &&
                parsed.ImageOriginY == imageOriginY && parsed.PaletteOriginX == paletteOriginX &&
                parsed.PaletteOriginY == paletteOriginY,
            "TIM parser result disagrees with strict header measurements");
        return new TimMeasurement(pixelFormat, width, height, 2, imageOriginX, imageOriginY,
            paletteOriginX, paletteOriginY, rows);
    }

    private static int? ResolvePaletteRow(TimMeasurement tim, ushort clut)
    {
        int x = (clut & 0x3F) << 4;
        int y = clut >> 6;
        int row = y - tim.PaletteOriginY;
        return x == tim.PaletteOriginX && row >= 0 && row < tim.PaletteRows ? row : null;
    }

    private static bool IsCovered(TimMeasurement tim, byte page, (byte U, byte V) corner)
    {
        int x = page * 64 * tim.TexelsPerWord + corner.U;
        int y = corner.V;
        int imageX = tim.ImageOriginX * tim.TexelsPerWord;
        return x >= imageX && x < imageX + tim.Width &&
               y >= tim.ImageOriginY && y < tim.ImageOriginY + tim.Height;
    }

    private static string JoinRefusal(string? current, string next)
        => current is null ? next : current + "; " + next;

    private static IReadOnlyList<Re2TopologyMapping> EnumerateMappings(
        IReadOnlyList<int> sourceParents,
        IReadOnlyList<int> targetParents,
        int meshPartCount)
    {
        if (sourceParents.Count != targetParents.Count || sourceParents.Count == 0)
            return Array.Empty<Re2TopologyMapping>();
        int count = sourceParents.Count;
        var sourceChildren = BuildChildren(sourceParents);
        var targetChildren = BuildChildren(targetParents);
        string[] sourceSignatures = BuildSignatures(sourceChildren);
        string[] targetSignatures = BuildSignatures(targetChildren);
        if (sourceSignatures[0] != targetSignatures[0])
            return Array.Empty<Re2TopologyMapping>();

        int[] order = Traverse(sourceChildren, 0).ToArray();
        var map = Enumerable.Repeat(-1, count).ToArray();
        var used = new bool[count];
        map[0] = 0;
        used[0] = true;
        var results = new List<int?[]>();

        void Search(int orderIndex)
        {
            if (orderIndex == order.Length)
            {
                var expanded = new int?[meshPartCount];
                for (int i = 0; i < map.Length && i < expanded.Length; i++) expanded[i] = map[i];
                results.Add(expanded);
                return;
            }
            int source = order[orderIndex];
            if (source == 0)
            {
                Search(orderIndex + 1);
                return;
            }
            int mappedParent = map[sourceParents[source]];
            foreach (int candidate in targetChildren[mappedParent]
                         .Where(candidate => !used[candidate] &&
                                             sourceSignatures[source] == targetSignatures[candidate])
                         .OrderBy(candidate => candidate))
            {
                map[source] = candidate;
                used[candidate] = true;
                Search(orderIndex + 1);
                used[candidate] = false;
                map[source] = -1;
            }
        }

        Search(0);
        return Array.AsReadOnly(results
            .OrderBy(mapping => string.Join(",", mapping.Select(value => value?.ToString("D3") ?? "---")),
                StringComparer.Ordinal)
            .Select(mapping => new Re2TopologyMapping(
                Array.AsReadOnly(mapping),
                meshPartCount == count && mapping.All(value => value is not null)))
            .ToArray());
    }

    private static IReadOnlyList<int>[] BuildChildren(IReadOnlyList<int> parents)
    {
        if (parents.Count == 0 || parents[0] >= 0) return Array.Empty<IReadOnlyList<int>>();
        var children = Enumerable.Range(0, parents.Count).Select(_ => new List<int>()).ToArray();
        for (int child = 1; child < parents.Count; child++)
        {
            int parent = parents[child];
            if (parent < 0 || parent >= parents.Count) return Array.Empty<IReadOnlyList<int>>();
            children[parent].Add(child);
        }
        return children.Select(list => (IReadOnlyList<int>)Array.AsReadOnly(list.OrderBy(x => x).ToArray())).ToArray();
    }

    private static string[] BuildSignatures(IReadOnlyList<int>[] children)
    {
        var signatures = new string[children.Length];
        var visiting = new bool[children.Length];
        string Visit(int node)
        {
            if (signatures[node] is not null) return signatures[node];
            if (visiting[node]) return "!cycle";
            visiting[node] = true;
            signatures[node] = "(" + string.Concat(children[node].Select(Visit).OrderBy(value => value, StringComparer.Ordinal)) + ")";
            visiting[node] = false;
            return signatures[node];
        }
        for (int i = 0; i < children.Length; i++) Visit(i);
        return signatures;
    }

    private static IEnumerable<int> Traverse(IReadOnlyList<int>[] children, int root)
    {
        yield return root;
        foreach (int child in children[root])
            foreach (int descendant in Traverse(children, child))
                yield return descendant;
    }

    private static Re2CapacityFacts MeasureCapacity(
        IReadOnlyList<Re2Md1PartFact> parts,
        DcmModel target,
        uint maximumDecodedEndAddress)
    {
        int rawRecords = checked(parts.Sum(part => part.PositionCount));
        int[] paddedByPart = parts.Select(part => checked((part.PositionCount + 2) / 3 * 3)).ToArray();
        int paddedRecords = checked(paddedByPart.Sum());
        int triangles = checked(parts.Sum(part => part.TriangleCount));
        int quads = checked(parts.Sum(part => part.QuadCount));
        int polygonBytes = checked(triangles * DcmVariant.Dc1.TriangleStride + quads * DcmVariant.Dc1.QuadStride);
        int packetBytes = checked(40 * triangles + 52 * quads);
        int rawBytes = checked(rawRecords * 16 + polygonBytes);
        int paddedBytes = checked(paddedRecords * 16 + polygonBytes);
        ulong appendAddress = (ulong)target.LoadBase + (uint)target.Raw.Length;
        ulong rawEnd = appendAddress + (uint)rawBytes;
        ulong paddedEnd = appendAddress + (uint)paddedBytes;
        return new Re2CapacityFacts(
            rawRecords,
            Array.AsReadOnly(paddedByPart),
            paddedRecords,
            rawBytes,
            paddedBytes,
            polygonBytes,
            packetBytes,
            rawRecords <= StockRecordLimit,
            paddedRecords <= StockRecordLimit,
            paddedRecords <= LiftedRecordLimit,
            rawBytes <= StockGeometryRegionBytes,
            paddedBytes <= StockGeometryRegionBytes,
            rawEnd,
            paddedEnd,
            rawEnd <= maximumDecodedEndAddress,
            paddedEnd <= maximumDecodedEndAddress);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("RE2 EMD preflight: " + message);
    }
}
