using System.Buffers.Binary;
using System.Reflection;
using DinoCrisis.Model;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Re2EmdPreflightTests
{
    private static readonly Re2DonorProvenance Unknown =
        new(Re2DonorProvenanceKind.Unknown, "synthetic");

    [Fact]
    public void Exact_eight_offset_closure_and_valid_re2_chunks_parse()
    {
        var fixture = Fixture.Basic();

        var result = Analyze(fixture);

        Assert.True(result.Identity.ExactV2DirectoryClosure);
        Assert.Equal(8, result.Identity.OffsetCount);
        Assert.Equal(BioVersion.Biohazard2, result.Identity.ParsedFamily);
        Assert.Equal(8, result.Chunks.Count);
        Assert.Equal(ModelFile.ChunkKind.Mesh, result.Chunks[7].Kind);
        Assert.Equal(fixture.Mesh.Data.Length, result.Chunks[7].Length);
        Assert.Equal("synthetic/EM001.EMD", result.EmdSource);
        Assert.Equal("SYNTHETIC/em001.tim", result.TimSource);
        Assert.Equal(Unknown, result.Provenance);
        Assert.Equal(Re2SiblingRelationship.Valid, result.SiblingRelationship);
    }

    [Fact]
    public void Malformed_eight_offset_chunks_and_four_offset_player_framing_refuse()
    {
        var fixture = Fixture.Basic();
        var malformed = BuildEmd(fixture.Emr.Data.ToArray(), new byte[4]);
        var fourOffset = BuildV2(new byte[4], new byte[4], new byte[4], new byte[4]);

        Assert.Throws<InvalidDataException>(() => Re2EmdPreflight.Analyze(
            malformed, "EM001.EMD", fixture.Tim, "EM001.TIM", fixture.Target, Unknown, uint.MaxValue));
        Assert.Throws<InvalidDataException>(() => Re2EmdPreflight.Analyze(
            fourOffset, "PL00.PLD", fixture.Tim, "PL00.TIM", fixture.Target, Unknown, uint.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Missing_malformed_or_mismatched_sibling_tim_refuses_texture_complete_analysis(int kind)
    {
        var fixture = Fixture.Basic();
        ReadOnlyMemory<byte> tim = kind switch
        {
            0 => ReadOnlyMemory<byte>.Empty,
            1 => new byte[] { 0x10, 0, 0, 0 },
            _ => fixture.Tim,
        };
        string timSource = kind == 2 ? "synthetic/EM999.TIM" : "synthetic/EM001.TIM";

        var result = Re2EmdPreflight.Analyze(
            fixture.Emd, "synthetic/EM001.EMD", tim, timSource,
            fixture.Target, Unknown, uint.MaxValue);

        Assert.False(result.Texture.IsComplete);
        Assert.NotNull(result.Texture.RefusalReason);
        if (kind == 2)
            Assert.Equal(Re2SiblingRelationship.Invalid, result.SiblingRelationship);
    }

    [Fact]
    public void Independent_corner_normals_and_unreferenced_positions_are_counted_exactly()
    {
        var part = Part(
            positions: new[] { V(0), V(10), V(20), V(30) },
            normals: new[] { V(100), V(200), V(300) },
            triangles: new[]
            {
                T(0, 1, 2, 0, 1, 2),
                T(0, 2, 1, 1, 2, 1),
            });
        var fixture = Fixture.Create(new[] { part }, new[] { -1 }, new[] { V(0) });

        var result = Analyze(fixture);
        var measured = Assert.Single(result.MeshParts);

        Assert.Equal(3, measured.ReferencedPositionCount);
        Assert.Equal(1, measured.UnreferencedPositionCount);
        Assert.Equal(1, measured.MultiNormalPositionCount);
        Assert.Equal(1, result.TotalMultiNormalPositionCount);
    }

    [Fact]
    public void Hierarchy_mismatch_has_no_implicit_mapping()
    {
        var fixture = Fixture.Create(
            new[] { Part(), Part(), Part() },
            new[] { -1, 0, 1 },
            new[] { V(0), V(10), V(10) },
            targetParents: new[] { -1, 0, 0 });

        var result = Analyze(fixture);

        Assert.Empty(result.TopologyMappings);
        Assert.False(result.TransformRunLegal);
    }

    [Fact]
    public void Isomorphic_rooted_trees_return_every_index_mapping_deterministically()
    {
        var fixture = Fixture.Create(
            new[] { Part(), Part(), Part() },
            new[] { -1, 0, 0 },
            new[] { V(0), V(10), V(-10) },
            targetParents: new[] { -1, 0, 0 });

        var mappings = Analyze(fixture).TopologyMappings;

        Assert.Equal(2, mappings.Count);
        Assert.Equal(new int?[] { 0, 1, 2 }, mappings[0].SourceMeshToTarget);
        Assert.Equal(new int?[] { 0, 2, 1 }, mappings[1].SourceMeshToTarget);
        Assert.All(mappings, mapping => Assert.True(mapping.IsComplete));
    }

    [Fact]
    public void Extra_mesh_parts_stay_explicit_and_unmapped()
    {
        var fixture = Fixture.Create(
            new[] { Part(), Part(), Part() },
            new[] { -1, 0 },
            new[] { V(0), V(10) },
            targetParents: new[] { -1, 0 });

        var result = Analyze(fixture);
        var mapping = Assert.Single(result.TopologyMappings);

        Assert.Equal(new int?[] { 0, 1, null }, mapping.SourceMeshToTarget);
        Assert.False(mapping.IsComplete);
        Assert.Equal(new[] { 2 }, result.MeshPartsBeyondEmr);
    }

    [Fact]
    public void Raw_and_fk_seams_are_per_edge_and_do_not_select_a_coordinate_policy()
    {
        var fixture = Fixture.Create(
            new[]
            {
                Part(positions: new[] { V(0) }, normals: new[] { V(1) }),
                Part(positions: new[] { V(0) }, normals: new[] { V(1) }),
                Part(positions: new[] { V(100) }, normals: new[] { V(1) }),
            },
            new[] { -1, 0, 0 },
            new[] { V(0), V(100), V(-100) },
            targetParents: new[] { -1, 0, 0 });

        var result = Analyze(fixture);

        Assert.Equal(2, result.Seams.Edges.Count);
        Assert.Equal(0, result.Seams.Edges[0].RawMinimum, 8);
        Assert.Equal(100, result.Seams.Edges[0].FkRestMinimum, 8);
        Assert.Equal(100, result.Seams.Edges[1].RawMinimum, 8);
        Assert.Equal(0, result.Seams.Edges[1].FkRestMinimum, 8);
        Assert.False(result.CoordinatePolicySelected);
        Assert.False(result.BindingReady);
    }

    [Fact]
    public void Per_part_padding_is_three_times_ceiling_visible_over_three()
    {
        var fixture = Fixture.Create(
            new[]
            {
                Part(positions: Enumerable.Range(0, 1).Select(V).ToArray(), normals: new[] { V(1) }),
                Part(positions: Enumerable.Range(0, 4).Select(V).ToArray(), normals: Enumerable.Range(0, 4).Select(V).ToArray()),
                Part(positions: Enumerable.Range(0, 6).Select(V).ToArray(), normals: Enumerable.Range(0, 6).Select(V).ToArray()),
            },
            new[] { -1, 0, 1 },
            new[] { V(0), V(0), V(0) },
            targetParents: new[] { -1, 0, 1 });

        var capacity = Analyze(fixture).Capacity;

        Assert.Equal(new[] { 3, 6, 6 }, capacity.PerPartPaddedRecordCounts);
        Assert.Equal(11, capacity.RawRecordCount);
        Assert.Equal(15, capacity.PaddedRecordCount);
    }

    [Fact]
    public void Capacity_polygon_and_packet_facts_remain_separate()
    {
        var fixture = Fixture.Create(
            new[]
            {
                Part(
                    positions: Enumerable.Range(0, 401).Select(V).ToArray(),
                    normals: Enumerable.Range(0, 401).Select(V).ToArray(),
                    triangles: new[] { T(0, 1, 2) },
                    quads: new[] { Q(0, 1, 2, 3) }),
            },
            new[] { -1 }, new[] { V(0) }, targetRawPadding: 100);

        var result = Re2EmdPreflight.Analyze(
            fixture.Emd, "EM001.EMD", fixture.Tim, "EM001.TIM", fixture.Target,
            Unknown, fixture.Target.LoadBase + (uint)fixture.Target.Raw.Length + 10_000);
        var c = result.Capacity;

        Assert.Equal(401, c.RawRecordCount);
        Assert.Equal(402, c.PaddedRecordCount);
        Assert.False(c.Stock400Fits);
        Assert.False(c.Padded400Fits);
        Assert.True(c.Padded1024Fits);
        Assert.Equal(36, c.PolygonPoolBytes);
        Assert.Equal(92, c.PacketContributionBytes);
        Assert.Equal(401 * 16 + 36, c.RawAllPoolGeometryBytes);
        Assert.Equal(402 * 16 + 36, c.PaddedAllPoolGeometryBytes);
        Assert.Equal(c.PaddedAllPoolGeometryBytes <= Re2EmdPreflight.StockGeometryRegionBytes,
            c.PaddedStockRegionFits);
        Assert.True(c.PaddedDecodedEndFits);
    }

    [Fact]
    public void Zero_uv_clut_zero_primitives_remain_explicit_unresolved_material_entries()
    {
        var part = Part(triangles: new[] { T(0, 1, 2) });
        part.TriangleTextures[0] = TT(0, 0, 0);
        var fixture = Fixture.Create(
            new[] { part },
            new[] { -1 }, new[] { V(0) });

        var result = Analyze(fixture);
        var material = Assert.Single(result.Texture.Materials);

        Assert.True(material.IsZeroUvClutZeroException);
        Assert.Equal(1, material.PrimitiveCount);
        Assert.Equal(3, material.CornerCount);
        Assert.False(result.Texture.MaterialSemanticsResolved);
    }

    [Fact]
    public void Inputs_and_target_raw_are_byte_identical_and_no_output_payload_is_returned()
    {
        var fixture = Fixture.Basic();
        var emdBefore = (byte[])fixture.Emd.Clone();
        var timBefore = (byte[])fixture.Tim.Clone();
        var targetBefore = (byte[])fixture.Target.Raw.Clone();

        _ = Analyze(fixture);

        Assert.Equal(emdBefore, fixture.Emd);
        Assert.Equal(timBefore, fixture.Tim);
        Assert.Equal(targetBefore, fixture.Target.Raw);
        Assert.DoesNotContain(typeof(Re2EmdPreflightResult).GetProperties(),
            property => property.PropertyType == typeof(byte[]) ||
                        property.PropertyType == typeof(ReadOnlyMemory<byte>));
    }

    [Fact]
    public void Existing_re2_coordinate_and_atlas_refusals_remain_unchanged()
    {
        Assert.Throws<InvalidDataException>(() =>
            Dc1DonorCoordinatePolicy.Resolve(BioVersion.Biohazard2));

        var validate = typeof(Dc1TargetNativeAtlasConverter).GetMethod(
            "ValidateDonor", BindingFlags.NonPublic | BindingFlags.Static)!;
        var donor = new Dc1DonorTexture(
            BioVersion.Biohazard2, 128, 256, 2, new byte[128 * 256], new ushort[256],
            new[] { new Dc1DonorPage(0x80, 0, 0) });
        var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { donor }));
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [LocalRe2CorpusFact]
    public void Authorized_local_stress_corpus_reproduces_signed_off_ledgers_read_only()
    {
        string root = LocalRe2CorpusFactAttribute.RepositoryRoot!;
        var cases = new[]
        {
            (Path: "biorand/datapacks/base/data/re2/emd/orca/EM020.EMD",
                MeshParts: 10, EmrParts: 10, Positions: 409, Normals: 409, Triangles: 641, Quads: 0,
                Padded: 417, Conflicts: 0, RawMin: 17.00, RawMean: 57.15, RawMax: 132.02,
                FkMin: 1.00, FkMean: 32.72, FkMax: 254.99, ZeroMaterials: 0),
            (Path: "biorand/datapacks/classic/data/re2/emd/hunter.re1/EM022.EMD",
                MeshParts: 23, EmrParts: 22, Positions: 466, Normals: 900, Triangles: 799, Quads: 0,
                Padded: 495, Conflicts: 125, RawMin: 13.00, RawMean: 66.18, RawMax: 129.62,
                FkMin: 13.49, FkMean: 140.02, FkMax: 576.67, ZeroMaterials: 6),
            (Path: "biorand/datapacks/classic/data/re2/emd/zombies.re1/EM010.EMD",
                MeshParts: 17, EmrParts: 15, Positions: 312, Normals: 1163, Triangles: 181, Quads: 173,
                Padded: 336, Conflicts: 312, RawMin: 10.20, RawMean: 61.84, RawMax: 121.03,
                FkMin: 10.20, FkMean: 61.33, FkMax: 135.07, ZeroMaterials: 0),
        };
        DcmModel target = BuildTarget(new[] { -1 }, 0);

        foreach (var expected in cases)
        {
            string emdPath = Path.Combine(root, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            string timPath = Path.ChangeExtension(emdPath, ".TIM");
            var result = Re2EmdPreflight.Analyze(
                File.ReadAllBytes(emdPath), emdPath, File.ReadAllBytes(timPath), timPath,
                target, Unknown, uint.MaxValue);

            Assert.Equal(expected.MeshParts, result.MeshParts.Count);
            Assert.Equal(expected.EmrParts, result.Armatures[0].Parents.Count);
            Assert.Equal(expected.Positions, result.MeshParts.Sum(part => part.PositionCount));
            Assert.Equal(expected.Normals, result.MeshParts.Sum(part => part.NormalCount));
            Assert.Equal(expected.Triangles, result.MeshParts.Sum(part => part.TriangleCount));
            Assert.Equal(expected.Quads, result.MeshParts.Sum(part => part.QuadCount));
            Assert.Equal(expected.Padded, result.Capacity.PaddedRecordCount);
            Assert.Equal(expected.Conflicts, result.TotalMultiNormalPositionCount);
            Assert.Equal(expected.RawMin, result.Seams.Raw.Minimum, 2);
            Assert.Equal(expected.RawMean, result.Seams.Raw.Mean, 2);
            Assert.Equal(expected.RawMax, result.Seams.Raw.Maximum, 2);
            Assert.Equal(expected.FkMin, result.Seams.FkRest.Minimum, 2);
            Assert.Equal(expected.FkMean, result.Seams.FkRest.Mean, 2);
            Assert.Equal(expected.FkMax, result.Seams.FkRest.Maximum, 2);
            Assert.True(result.Texture.IsComplete);
            Assert.Equal(expected.ZeroMaterials,
                result.Texture.Materials.Where(material => material.IsZeroUvClutZeroException)
                    .Sum(material => material.PrimitiveCount));
        }
    }

    private static Re2EmdPreflightResult Analyze(Fixture fixture) => Re2EmdPreflight.Analyze(
        fixture.Emd, "synthetic/EM001.EMD", fixture.Tim, "SYNTHETIC/em001.tim",
        fixture.Target, Unknown, uint.MaxValue);

    private static Md1.Builder.Part Part(
        Md1.Vector[]? positions = null,
        Md1.Vector[]? normals = null,
        Md1.Triangle[]? triangles = null,
        Md1.Quad[]? quads = null)
    {
        positions ??= new[] { V(0), V(10), V(20), V(30) };
        normals ??= positions.Select((_, i) => V(100 + i)).ToArray();
        triangles ??= Array.Empty<Md1.Triangle>();
        quads ??= Array.Empty<Md1.Quad>();
        var part = new Md1.Builder.Part();
        part.Positions.AddRange(positions);
        part.Normals.AddRange(normals);
        part.Triangles.AddRange(triangles);
        part.Quads.AddRange(quads);
        part.TriangleTextures.AddRange(triangles.Select(_ => TT(1, 2, 0)));
        part.QuadTextures.AddRange(quads.Select(_ => QT(1, 2, 0)));
        return part;
    }

    private static Md1.Vector V(int x) => new((short)x, 0, 0);

    private static Md1.Triangle T(
        ushort v0, ushort v1, ushort v2,
        ushort n0 = 0, ushort n1 = 1, ushort n2 = 2)
        => new() { v0 = v0, v1 = v1, v2 = v2, n0 = n0, n1 = n1, n2 = n2 };

    private static Md1.Quad Q(ushort v0, ushort v1, ushort v2, ushort v3)
        => new() { v0 = v0, v1 = v1, v2 = v2, v3 = v3, n0 = 0, n1 = 1, n2 = 2, n3 = 3 };

    private static Md1.TriangleTexture TT(byte u, byte v, ushort clut) => new()
    {
        u0 = u, v0 = v, u1 = u, v1 = v, u2 = u, v2 = v, clutId = clut, page = 0,
    };

    private static Md1.QuadTexture QT(byte u, byte v, ushort clut) => new()
    {
        u0 = u, v0 = v, u1 = u, v1 = v, u2 = u, v2 = v, u3 = u, v3 = v,
        clutId = clut, page = 0,
    };

    private static byte[] BuildEmd(byte[] emr, byte[] md1)
        => BuildV2(new byte[4], new byte[4], emr, new byte[4], emr, new byte[4], emr, md1);

    private static byte[] BuildV2(params byte[][] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(chunks.Length);
        var offsets = new int[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            offsets[i] = checked((int)stream.Position);
            writer.Write(chunks[i]);
        }
        int directoryOffset = checked((int)stream.Position);
        foreach (int offset in offsets) writer.Write(offset);
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, directoryOffset);
        return bytes;
    }

    private static byte[] BuildTim()
    {
        var tim = new TimFile(256, 256, 8);
        tim.SetPalette(0, new ushort[256]);
        return tim.GetBytes();
    }

    private static DcmModel BuildTarget(int[] parents, int rawPadding)
    {
        const uint loadBase = 0x80180000;
        var builder = new DcmModelBuilder(DcmVariant.Dc1);
        builder.AddVertex(default, default);
        for (int i = 0; i < parents.Length; i++)
            builder.AddPart(new DcmPart { Parent = checked((sbyte)parents[i]) },
                Array.Empty<DcmTriangle>(), Array.Empty<DcmQuad>());
        byte[] raw = builder.Serialize(loadBase);
        if (rawPadding != 0) Array.Resize(ref raw, raw.Length + rawPadding);
        return DcmModel.Read(raw, 0, loadBase, DcmVariant.Dc1);
    }

    private sealed record Fixture(byte[] Emd, byte[] Tim, Md1 Mesh, Emr Emr, DcmModel Target)
    {
        public static Fixture Basic()
        {
            var part = Part(triangles: new[] { T(0, 1, 2) });
            return Create(new[] { part }, new[] { -1 }, new[] { V(0) });
        }

        public static Fixture Create(
            Md1.Builder.Part[] meshParts,
            int[] emrParents,
            Md1.Vector[] relativeOffsets,
            int[]? targetParents = null,
            int targetRawPadding = 0)
        {
            var meshBuilder = new Md1.Builder();
            meshBuilder.Parts.AddRange(meshParts);
            Md1 mesh = meshBuilder.ToMesh();

            var emrBuilder = new Emr.Builder(BioVersion.Biohazard2)
            {
                NumParts = checked((ushort)emrParents.Length),
            };
            emrBuilder.RelativePositions.AddRange(relativeOffsets.Select(v => new Emr.Vector(v.x, v.y, v.z)));
            for (int parent = 0; parent < emrParents.Length; parent++)
                emrBuilder.Armatures.Add(Enumerable.Range(0, emrParents.Length)
                    .Where(child => emrParents[child] == parent)
                    .Select(child => checked((byte)child)).ToArray());
            Emr emr = emrBuilder.ToEmr();

            return new Fixture(
                BuildEmd(emr.Data.ToArray(), mesh.Data.ToArray()),
                BuildTim(), mesh, emr,
                BuildTarget(targetParents ?? emrParents, targetRawPadding));
        }
    }
}

internal sealed class LocalRe2CorpusFactAttribute : FactAttribute
{
    public static string? RepositoryRoot { get; } = FindRepositoryRoot();

    public LocalRe2CorpusFactAttribute()
    {
        string? root = RepositoryRoot;
        string[] files =
        {
            "biorand/datapacks/base/data/re2/emd/orca/EM020.EMD",
            "biorand/datapacks/base/data/re2/emd/orca/EM020.TIM",
            "biorand/datapacks/classic/data/re2/emd/hunter.re1/EM022.EMD",
            "biorand/datapacks/classic/data/re2/emd/hunter.re1/EM022.TIM",
            "biorand/datapacks/classic/data/re2/emd/zombies.re1/EM010.EMD",
            "biorand/datapacks/classic/data/re2/emd/zombies.re1/EM010.TIM",
        };
        if (root is null || files.Any(file => !File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)))))
            Skip = "Local RE2 corpus skipped: the authorized biorand stress pairs are unavailable.";
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
        return null;
    }
}
