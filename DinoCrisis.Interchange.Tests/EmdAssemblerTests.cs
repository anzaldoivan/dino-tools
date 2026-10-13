using System.Buffers.Binary;
using System.IO;
using DinoCrisis.Model;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 1: the donor-template EMD assembly. Built on a clean-room SYNTHETIC RE2 EMD
/// (8 chunks, the Biohazard2 layout) so no copyrighted RE bytes are needed — proves the mesh chunk
/// is swapped and the other chunks (animation/armature) are preserved, i.e. a part-matched donor's
/// animation rides along.</summary>
public class EmdAssemblerTests
{
    /// <summary>A minimal valid Biohazard2 EMD: 8 chunks (Morph, Anim, Armature, Anim, Armature,
    /// Anim, Armature, Mesh per EmdFile.g_chunkKinds2), OffsetDirectoryV2 framing. Chunks 0–6 carry a
    /// recognizable marker byte; chunk 7 (mesh) is a real MD1 so the donor itself parses.</summary>
    private static byte[] BuildSyntheticRe2Emd(byte[] meshChunk, byte marker)
    {
        var chunks = new byte[8][];
        for (int i = 0; i < 7; i++) chunks[i] = new byte[] { marker, (byte)i, 0, 0 };
        chunks[7] = meshChunk;

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0); // directoryOffset placeholder
        bw.Write(8); // numOffsets

        var offsets = new int[8];
        for (int i = 0; i < 8; i++)
        {
            offsets[i] = (int)ms.Position;
            bw.Write(chunks[i]);
        }
        int dirOff = (int)ms.Position;
        foreach (var o in offsets) bw.Write(o);

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), dirOff);
        return bytes;
    }

    private static Md1 SyntheticDcmAsMd1()
    {
        var model = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        return Md1Interchange.ToMd1(DcmInterchange.ToInterchange(model));
    }

    [Fact]
    public void SwapRe2Mesh_replaces_mesh_and_preserves_other_chunks()
    {
        // Donor with a 4-byte placeholder mesh, marked 0xAA on its non-mesh chunks.
        var donor = BuildSyntheticRe2Emd(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, marker: 0xAA);
        var newMesh = SyntheticDcmAsMd1();

        var result = EmdAssembler.SwapRe2Mesh(donor, newMesh);

        // Reload and confirm the mesh chunk is now our DCM-derived MD1 (2 parts, 4 objects).
        using var ms = new MemoryStream(result, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);
        var roundTripped = emd.Md1;
        Assert.Equal(2, roundTripped.NumParts);
        Assert.Equal(4, roundTripped.NumObjects);

        // The donor's non-mesh chunks survive untouched (chunk 0 still starts 0xAA, 0x00).
        var chunk0 = emd.GetChunk(ModelFile.ChunkKind.Morph, 0);
        Assert.Equal((byte)0xAA, chunk0.Span[0]);
        Assert.Equal((byte)0x00, chunk0.Span[1]);
    }

    [Fact]
    public void SwapRe2MeshAndSkeleton_sets_mesh_and_every_armature_chunk()
    {
        var donor = BuildSyntheticRe2Emd(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, marker: 0xAA);
        var model = DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1);
        var interchange = DcmInterchange.ToInterchange(model);
        var mesh = Md1Interchange.ToMd1(interchange);
        var skeleton = EmrInterchange.ToEmr(interchange);

        var result = EmdAssembler.SwapRe2MeshAndSkeleton(donor, mesh, skeleton);

        using var ms = new MemoryStream(result, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);

        // Mesh swapped.
        Assert.Equal(2, emd.Md1.NumParts);

        // Every armature bank now reads back as our 2-part skeleton with the correct FK.
        int armatureCount = 0;
        for (int i = 0; i < emd.NumChunks; i++)
            if (emd.GetChunkKind(i) == ModelFile.ChunkKind.Armature) armatureCount++;
        Assert.True(armatureCount >= 1);
        for (int n = 0; n < armatureCount; n++)
        {
            var emr = emd.GetEmr(n);
            Assert.Equal(2, emr.NumParts);
            var fp = emr.GetFinalPosition(1); // (0,-100,0) + (10,20,30)
            Assert.Equal((short)10, fp.x);
            Assert.Equal((short)-80, fp.y);
            Assert.Equal((short)30, fp.z);
        }
    }

    [Fact]
    public void SwapRe2Mesh_output_is_a_parseable_emd()
    {
        var donor = BuildSyntheticRe2Emd(new byte[] { 0, 0, 0, 0 }, marker: 0x11);
        var result = EmdAssembler.SwapRe2Mesh(donor, SyntheticDcmAsMd1());

        // ModelFile.FromFile uses (directoryOffset + numOffsets*4 == fileLength) + numOffsets==8 to
        // recognize a Biohazard2 EMD; assert our output satisfies that contract.
        int dirOff = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(0, 4));
        int numOffsets = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4, 4));
        Assert.Equal(8, numOffsets);
        Assert.Equal(result.Length, dirOff + numOffsets * 4);
    }
}
