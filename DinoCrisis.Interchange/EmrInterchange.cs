using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

/// <summary>
/// Emits an RE EMR (armature / skeleton) chunk from the neutral <see cref="InterchangeMesh"/> — gap
/// "G3". The interchange already holds exactly what an EMR needs: per-part rest pose <b>relative to
/// the parent</b> (DCM stores bone xyz this way; RE EMR stores <c>RelativePosition</c> the same way)
/// and a parent tree. The EMR instead encodes the hierarchy as per-part <b>child lists</b>, so this
/// converter inverts the parent array into children and feeds <c>Emr.Builder</c>. No keyframes are
/// written (skeleton/rest pose only) — animation is the out-of-scope motion-retarget gate.
/// </summary>
public static class EmrInterchange
{
    /// <summary>Build a keyframe-less EMR (skeleton + rest pose) from the interchange mesh.
    /// <paramref name="version"/> selects the RE dialect (default RE2/Biohazard2, matching the EMD
    /// donor path). The root part (the one with a negative parent) must be part 0, as RE's armature
    /// walk begins at index 0.</summary>
    public static Emr ToEmr(InterchangeMesh mesh, BioVersion version = BioVersion.Biohazard2)
    {
        if (mesh.Parts.Count == 0)
            throw new System.ArgumentException("Mesh has no parts.", nameof(mesh));
        if (mesh.Parts[0].Parent >= 0)
            throw new System.ArgumentException("Root part (negative parent) must be part 0.", nameof(mesh));
        if (mesh.Parts.Count > 256)
            throw new System.ArgumentException("EMR child indices are bytes; >256 parts unsupported.", nameof(mesh));

        var builder = new Emr.Builder(version)
        {
            NumParts = (ushort)mesh.Parts.Count,
            KeyFrameSize = 0,
        };

        for (int i = 0; i < mesh.Parts.Count; i++)
        {
            var p = mesh.Parts[i];
            builder.RelativePositions.Add(new Emr.Vector { x = p.RestX, y = p.RestY, z = p.RestZ });
        }

        // Invert the parent array into per-part child-index lists (the EMR armature encoding). Order
        // children by ascending index for determinism.
        for (int i = 0; i < mesh.Parts.Count; i++)
        {
            var children = new List<byte>();
            for (int j = 0; j < mesh.Parts.Count; j++)
                if (mesh.Parts[j].Parent == i)
                    children.Add((byte)j);
            builder.Armatures.Add(children.ToArray());
        }

        return builder.ToEmr();
    }
}
