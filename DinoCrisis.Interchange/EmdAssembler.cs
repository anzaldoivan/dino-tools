using System.IO;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

/// <summary>
/// Produces a complete RE EMD by the <b>donor-template</b> strategy: an EMD cannot be authored from
/// an empty chunk directory through the public <c>ModelFile</c> API (chunks are only populated by
/// <c>Read</c>), so we take an existing EMD of the right version and replace its <b>mesh</b> chunk
/// with the DCM-derived MD1. Crucially this leaves the donor's Armature (EMR) and Animation (EDD)
/// chunks intact — so a part-count-matched donor yields an <i>animated</i> result without solving
/// cross-rig motion retargeting (the human-CE gate, kept out of scope here). Replacing the skeleton
/// (EMR) and texture (TIM) chunks are later increments (G3, G4).
/// </summary>
public static class EmdAssembler
{
    /// <summary>Whether to keep the donor's skeleton (and so its borrowed animations) or replace it
    /// with the DCM creature's own skeleton.</summary>
    public enum RigMode
    {
        /// <summary>Keep the donor's Armature/Animation chunks — borrowed-rig. Only coherent when the
        /// donor's part count matches the DCM creature; then it animates with the donor's motions.</summary>
        KeepDonorRig,

        /// <summary>Replace every Armature chunk with the DCM creature's own skeleton — fresh-rig. The
        /// donor's animations are left in place, so the result is a correct rest-pose model that only
        /// animates once matching motion is supplied (the out-of-scope motion-retarget gate).</summary>
        FreshRig,
    }

    /// <summary>
    /// Version-aware assembly: detect the donor's RE engine (<see cref="EmdDonor.Detect"/>), convert
    /// the interchange mesh to that engine's mesh format (MD1 for RE2, MD2 for RE3, TMD for RE1 — via
    /// the MIT <c>MeshConverter</c> with <b>remap disabled</b>, because the converter's built-in part
    /// remaps are for RE's fixed 15-part humanoid player rig and would scramble an arbitrary creature),
    /// set the mesh chunk, and — for <see cref="RigMode.FreshRig"/> — emit and set the DCM skeleton
    /// (EMR) in the donor's dialect. Returns the new EMD bytes.
    /// </summary>
    public static byte[] Assemble(byte[] donorEmd, InterchangeMesh mesh, RigMode rigMode)
    {
        var version = EmdDonor.Detect(donorEmd);

        EmdFile emd;
        using (var ms = new MemoryStream(donorEmd, writable: false))
        {
            try
            {
                emd = new EmdFile(version, ms);
            }
            catch (System.Exception ex)
            {
                throw new InvalidDataException($"Donor is not a parseable {version} EMD: {ex.Message}", ex);
            }
        }

        // Build the RE2 MD1 first, then convert to the donor's mesh version if different. ConvertMesh
        // is a no-op when versions match; remap:false keeps our part order 1:1 (we carry our own rig).
        var md1 = Md1Interchange.ToMd1(mesh);
        IModelMesh targetMesh = version == BioVersion.Biohazard2
            ? md1
            : new MeshConverter().ConvertMesh(md1, version, remap: false);
        emd.SetMesh(0, targetMesh);

        if (rigMode == RigMode.FreshRig)
        {
            var skeleton = EmrInterchange.ToEmr(mesh, version);
            int armatureCount = CountChunks(emd, ModelFile.ChunkKind.Armature);
            if (armatureCount == 0)
                throw new InvalidDataException("Donor has no Armature chunk to replace for FreshRig.");
            for (int n = 0; n < armatureCount; n++)
                emd.SetEmr(n, skeleton);
        }

        using var outMs = new MemoryStream();
        emd.Save(outMs);
        return outMs.ToArray();
    }

    private static int CountChunks(ModelFile file, ModelFile.ChunkKind kind)
    {
        int count = 0;
        for (int i = 0; i < file.NumChunks; i++)
            if (file.GetChunkKind(i) == kind) count++;
        return count;
    }

    /// <summary>Swap the mesh of an RE2 (Biohazard2) EMD donor for <paramref name="mesh"/>, returning
    /// the new EMD bytes. The donor must be a Biohazard2 EMD; its other chunks are preserved.</summary>
    public static byte[] SwapRe2Mesh(byte[] donorEmd, Md1 mesh)
    {
        using var ms = new MemoryStream(donorEmd, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);
        emd.Md1 = mesh;
        using var outMs = new MemoryStream();
        emd.Save(outMs);
        return outMs.ToArray();
    }

    /// <summary>Swap BOTH the mesh and the skeleton (every Armature chunk) of an RE2 EMD donor — the
    /// <b>fresh-rig</b> path. Note: the donor's Animation (EDD) chunks are left in place, so the
    /// result only animates coherently if the new skeleton's part count matches what those animations
    /// expect; otherwise it is a correct rest-pose model awaiting new animation (the motion gate).
    /// For a part-matched donor, prefer <see cref="SwapRe2Mesh"/> alone to keep the borrowed rig.</summary>
    public static byte[] SwapRe2MeshAndSkeleton(byte[] donorEmd, Md1 mesh, Emr skeleton)
    {
        using var ms = new MemoryStream(donorEmd, writable: false);
        var emd = new EmdFile(BioVersion.Biohazard2, ms);
        emd.Md1 = mesh;

        // Set every Armature chunk to the new skeleton (RE2 EMDs carry several armature/animation banks).
        int armatureCount = 0;
        for (int i = 0; i < emd.NumChunks; i++)
            if (emd.GetChunkKind(i) == ModelFile.ChunkKind.Armature)
                armatureCount++;
        for (int n = 0; n < armatureCount; n++)
            emd.SetEmr(n, skeleton);

        using var outMs = new MemoryStream();
        emd.Save(outMs);
        return outMs.ToArray();
    }
}
