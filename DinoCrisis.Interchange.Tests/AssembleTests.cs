using System.IO;
using DinoCrisis.Model;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 3: version-aware <see cref="EmdAssembler.Assemble"/>. Synthetic 2-part DCM model.</summary>
public class AssembleTests
{
    private static InterchangeMesh SyntheticMesh() =>
        DcmInterchange.ToInterchange(
            DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1));

    [Fact]
    public void Assemble_re2_keep_donor_rig_swaps_only_the_mesh()
    {
        var donor = SyntheticEmd.BuildRe2(marker: 0xAA);
        var result = EmdAssembler.Assemble(donor, SyntheticMesh(), EmdAssembler.RigMode.KeepDonorRig);

        using var ms = new MemoryStream(result, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);

        Assert.Equal(2, emd.Md1.NumParts);                       // mesh swapped to ours (RE2 MD1)
        var armature0 = emd.GetChunk(ModelFile.ChunkKind.Armature, 0);
        Assert.Equal((byte)0xAA, armature0.Span[0]);             // donor rig preserved
    }

    [Fact]
    public void Assemble_re2_fresh_rig_replaces_every_armature()
    {
        var donor = SyntheticEmd.BuildRe2(marker: 0xAA);
        var result = EmdAssembler.Assemble(donor, SyntheticMesh(), EmdAssembler.RigMode.FreshRig);

        using var ms = new MemoryStream(result, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);

        int armatureCount = 0;
        for (int i = 0; i < emd.NumChunks; i++)
            if (emd.GetChunkKind(i) == ModelFile.ChunkKind.Armature) armatureCount++;
        for (int n = 0; n < armatureCount; n++)
        {
            var emr = emd.GetEmr(n);
            Assert.Equal(2, emr.NumParts);
            var fp = emr.GetFinalPosition(1); // (0,-100,0)+(10,20,30)
            Assert.Equal((short)10, fp.x);
            Assert.Equal((short)-80, fp.y);
            Assert.Equal((short)30, fp.z);
        }
    }

    [Fact]
    public void Assemble_re3_converts_mesh_to_md2()
    {
        var donor = SyntheticEmd.BuildRe3(marker: 0xBB);
        var result = EmdAssembler.Assemble(donor, SyntheticMesh(), EmdAssembler.RigMode.KeepDonorRig);

        using var ms = new MemoryStream(result, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard3, ms);

        var mesh = emd.GetMesh(0);
        Assert.Equal(BioVersion.Biohazard3, mesh.Version); // it's an MD2 now
        Assert.Equal(2, mesh.NumParts);
    }

    [Fact]
    public void Re1_mesh_path_converts_md1_to_tmd_without_humanoid_remap()
    {
        // RE1 EMD assembly uses the V1 layout we don't synthesize here, but the load-bearing new part
        // — producing the RE1 TMD mesh from our MD1 with remap disabled — is exercised directly.
        var md1 = Md1Interchange.ToMd1(SyntheticMesh());
        var tmd = new MeshConverter().ConvertMesh(md1, BioVersion.Biohazard1, remap: false);
        Assert.Equal(BioVersion.Biohazard1, tmd.Version);
        Assert.Equal(2, tmd.NumParts); // parts stay 1:1 (no 15-part humanoid scramble)
    }

    [Fact]
    public void Assemble_rejects_unparseable_donor()
    {
        // 8-offset header but the directory doesn't close ⇒ not a valid EMD.
        var junk = new byte[40];
        junk[4] = 8; // numOffsets = 8, but length/offsets are nonsense
        Assert.ThrowsAny<System.Exception>(
            () => EmdAssembler.Assemble(junk, SyntheticMesh(), EmdAssembler.RigMode.KeepDonorRig));
    }
}
