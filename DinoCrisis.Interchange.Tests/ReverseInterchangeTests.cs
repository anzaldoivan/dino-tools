using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Reverse pipeline (EMD/PLD → DCM). Validated by a forward+reverse round-trip: a DCM model
/// converted out to RE (MD1 + EMR) and back must rebuild into a structurally valid DCM whose rig and
/// polygon budget survive.</summary>
public class ReverseInterchangeTests
{
    private const uint Base = 0x80100000;

    private static DcmModel RoundTrip(DcmModel original)
    {
        var fwd = DcmInterchange.ToInterchange(original);
        var md1 = Md1Interchange.ToMd1(fwd);
        var emr = EmrInterchange.ToEmr(fwd);

        var back = ReverseInterchange.FromReMesh(md1);
        ReverseInterchange.ApplySkeleton(back, emr);
        var dcm = ReverseInterchange.ToDcm(back, DcmVariant.Dc1, Base);
        return DcmModel.Read(dcm, 0, Base, DcmVariant.Dc1);
    }

    [Fact]
    public void Synthetic_dcm_survives_forward_then_reverse_round_trip()
    {
        var original = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var rt = RoundTrip(original);

        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(original.Parts.Count, rt.Parts.Count);
        Assert.Equal(original.Header.TriangleCount, rt.Header.TriangleCount);
        Assert.Equal(original.Header.QuadCount, rt.Header.QuadCount);

        // Skeleton survives: same forward-kinematic world position per part.
        var a = DinoCrisis.Model.ObjExporter.ComputeWorldPositions(original);
        var b = DinoCrisis.Model.ObjExporter.ComputeWorldPositions(rt);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ToDcm_scale_factor_scales_positions_and_rest_poses()
    {
        var original = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var im = DcmInterchange.ToInterchange(original); // carries rest + parent from the DCM

        var full = DcmModel.Read(ReverseInterchange.ToDcm(im, DcmVariant.Dc1, Base, 1.0), 0, Base, DcmVariant.Dc1);
        var half = DcmModel.Read(ReverseInterchange.ToDcm(im, DcmVariant.Dc1, Base, 0.5), 0, Base, DcmVariant.Dc1);

        Assert.True(DcmModel.Validate(half));
        Assert.Equal(full.Vertices.Count, half.Vertices.Count);
        for (int i = 0; i < full.Vertices.Count; i++)
        {
            Assert.Equal((short)System.Math.Round(full.Vertices[i].X * 0.5), half.Vertices[i].X);
            Assert.Equal((short)System.Math.Round(full.Vertices[i].Y * 0.5), half.Vertices[i].Y);
        }
        // rest pose scales too (part 1 is the non-root bone)
        Assert.Equal((short)System.Math.Round(full.Parts[1].X * 0.5), half.Parts[1].X);
        Assert.Equal((short)System.Math.Round(full.Parts[1].Y * 0.5), half.Parts[1].Y);
    }

    // ---- per-vertex bone index (DC2 SVECTOR.pad) reconstruction ----
    // DC2 skins by a per-vertex bone index stored in the 4th SVECTOR word; RE's MD1 rigs by per-part
    // vertex objects and has no such field. FromReMesh must therefore stamp each vertex with its owning
    // part index (= its bone), else every vertex reads bone 0 -> correct at rest but stretched under
    // animation in-engine (the DC2 injection regression, docs/dc2/DC2-MODEL-INJECTION-RCA.md §4b).

    [Fact]
    public void FromReMesh_tags_each_vertex_with_its_owning_part_bone_index()
    {
        var original = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var md1 = Md1Interchange.ToMd1(DcmInterchange.ToInterchange(original));

        var back = ReverseInterchange.FromReMesh(md1);

        Assert.True(back.Parts.Count >= 2); // fixture has a root + a child bone
        for (int i = 0; i < back.Parts.Count; i++)
            Assert.All(back.Parts[i].Positions, p => Assert.Equal((short)i, p.Pad));
    }

    [Fact]
    public void Round_trip_emits_per_vertex_bone_index_matching_the_part_partition()
    {
        var original = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc2), 0, SyntheticBlob.Base, DcmVariant.Dc2);
        var im = DcmInterchange.ToInterchange(original);

        var back = ReverseInterchange.FromReMesh(Md1Interchange.ToMd1(im));
        ReverseInterchange.ApplySkeleton(back, EmrInterchange.ToEmr(im)); // rest poses + parents for ToDcm
        var dcm = DcmModel.Read(ReverseInterchange.ToDcm(back, DcmVariant.Dc2, Base), 0, Base, DcmVariant.Dc2);

        // ToDcm lays the vertex pool out part-by-part, so each part's block carries that part's bone index.
        int k = 0;
        for (int i = 0; i < back.Parts.Count; i++)
            for (int j = 0; j < back.Parts[i].Positions.Count; j++, k++)
                Assert.Equal((short)i, dcm.Vertices[k].Pad);
        Assert.Equal(dcm.Vertices.Count, k);
        Assert.Contains(dcm.Vertices, v => v.Pad != 0); // not the degenerate all-bone-0 binding
    }

    // ---- gated golden: real DC1 Regina ----

    [Dc1Fact("core00.dat")]
    public void Real_regina_survives_round_trip_to_valid_dcm()
    {
        var bytes = File.ReadAllBytes(FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat")));
        var original = DcmLocator.FromDc1Package(bytes).FirstOrDefault(m => m.Header.PartCount == 15);
        Assert.NotNull(original);

        var rt = RoundTrip(original!);
        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(15, rt.Parts.Count);
        Assert.Equal(original!.Header.TriangleCount, rt.Header.TriangleCount); // 314
        Assert.Equal(original.Header.QuadCount, rt.Header.QuadCount);          // 141

        // The rig round-trips exactly (relative poses + parent tree → child lists → back).
        Assert.Equal(DinoCrisis.Model.ObjExporter.ComputeWorldPositions(original), DinoCrisis.Model.ObjExporter.ComputeWorldPositions(rt));
        Assert.Equal(original.Parts.Select(p => p.Parent), rt.Parts.Select(p => p.Parent));
    }
}
