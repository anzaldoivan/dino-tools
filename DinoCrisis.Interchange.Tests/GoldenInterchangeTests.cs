using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Gated golden: convert a REAL DCM model (DC1 Theri st605, §7 witness 22/334/57) through
/// DCM → interchange → MD1 and assert the rig + polygon budget survive. Skipped when game data is
/// absent (no copyrighted bytes committed), exactly like the Model project's GoldenWitnessTests.</summary>
public sealed class GoldenInterchangeTests
{
    [Dc1Theory("st605.dat", "st106.dat")]
    [InlineData("st605.dat", 22, 334, 57)]   // Therizinosaurus
    [InlineData("st106.dat", 15, 282, 179)]  // Humanoid (NPC, 15-bone human rig)
    public void Real_dcm_converts_to_md1_preserving_rig_and_poly_budget(
        string file, int parts, int triTotal, int quadTotal)
    {
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(FixtureGate.Dc1File(file)));
        var model = models.FirstOrDefault(m =>
            m.Header.PartCount == parts && m.Header.TriangleCount == triTotal && m.Header.QuadCount == quadTotal);
        Assert.NotNull(model);

        var mesh = DcmInterchange.ToInterchange(model!);
        Assert.Equal(parts, mesh.Parts.Count);
        Assert.Equal(triTotal, mesh.Parts.Sum(p => p.Triangles.Count));
        Assert.Equal(quadTotal, mesh.Parts.Sum(p => p.Quads.Count));
        Assert.All(mesh.Parts, p => Assert.Equal(p.Positions.Count, p.Normals.Count)); // pools parallel

        var md1 = Md1Interchange.ToMd1(mesh);
        Assert.Equal(parts, md1.NumParts);

        // Sum the per-part MD1 poly counts (tri object + quad object per part) and match the DCM totals.
        int md1Tris = 0, md1Quads = 0;
        for (int i = 0; i < md1.NumParts; i++)
        {
            md1Tris += md1.Objects[i * 2].pri_count;
            md1Quads += md1.Objects[i * 2 + 1].pri_count;
        }
        Assert.Equal(triTotal, md1Tris);
        Assert.Equal(quadTotal, md1Quads);

        // Every MD1 polygon index must be in range of its part's own vertex pool (a structurally
        // valid mesh chunk). Spot-check via the biohazard reader's own accessors.
        for (int i = 0; i < md1.NumParts; i++)
        {
            var triObj = md1.Objects[i * 2];
            foreach (var t in md1.GetTriangles(triObj))
            {
                Assert.True(t.v0 < triObj.vtx_count);
                Assert.True(t.v1 < triObj.vtx_count);
                Assert.True(t.v2 < triObj.vtx_count);
            }
        }
    }

    [Dc1Theory("st605.dat", "st106.dat")]
    [InlineData("st605.dat", 22, 334, 57)]   // Therizinosaurus
    [InlineData("st106.dat", 15, 282, 179)]  // Humanoid (NPC, 15-bone human rig)
    public void Real_dcm_skeleton_emits_emr_whose_FK_matches_the_dcm_FK(
        string file, int parts, int triTotal, int quadTotal)
    {
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(FixtureGate.Dc1File(file)));
        var model = models.FirstOrDefault(m =>
            m.Header.PartCount == parts && m.Header.TriangleCount == triTotal && m.Header.QuadCount == quadTotal);
        Assert.NotNull(model);

        var mesh = DcmInterchange.ToInterchange(model!);
        var emr = EmrInterchange.ToEmr(mesh);
        Assert.Equal(parts, emr.NumParts);

        // Cross-oracle: the EMR's forward-kinematic world position of each part (its own armature
        // walk) must equal the DCM's own FK assembly (ObjExporter.ComputeWorldPositions). This proves
        // the parent tree + relative poses survived the conversion exactly.
        var dcmWorld = DinoCrisis.Model.ObjExporter.ComputeWorldPositions(model!);
        for (int i = 0; i < parts; i++)
        {
            var fp = emr.GetFinalPosition(i);
            Assert.Equal(dcmWorld[i].X, fp.x);
            Assert.Equal(dcmWorld[i].Y, fp.y);
            Assert.Equal(dcmWorld[i].Z, fp.z);
        }
    }

    [Dc1Fact("st106.dat")]
    public void Real_dcm_assembles_into_re2_and_re3_donors_preserving_part_count()
    {
        // Humanoid (15-bone human rig): 15 parts.
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(FixtureGate.Dc1File("st106.dat")));
        var model = models.FirstOrDefault(m => m.Header.PartCount == 15 && m.Header.TriangleCount == 282 && m.Header.QuadCount == 179);
        Assert.NotNull(model);
        var mesh = DcmInterchange.ToInterchange(model!);

        // RE2 donor → MD1, 15 parts; fresh rig replaces the armature with the raptor's own skeleton.
        var re2 = EmdAssembler.Assemble(SyntheticEmd.BuildRe2(), mesh, EmdAssembler.RigMode.FreshRig);
        using (var ms = new MemoryStream(re2, writable: false))
        {
            var emd = new EmdFile(BioVersion.Biohazard2, ms);
            Assert.Equal(15, emd.Md1.NumParts);
            Assert.Equal(15, emd.GetEmr(0).NumParts);
        }

        // RE3 donor → MD2 (cross-engine mesh conversion), 15 parts.
        var re3 = EmdAssembler.Assemble(SyntheticEmd.BuildRe3(), mesh, EmdAssembler.RigMode.KeepDonorRig);
        using (var ms = new MemoryStream(re3, writable: false))
        {
            var emd = new EmdFile(BioVersion.Biohazard3, ms);
            var md2 = emd.GetMesh(0);
            Assert.Equal(BioVersion.Biohazard3, md2.Version);
            Assert.Equal(15, md2.NumParts);
        }
    }
}
