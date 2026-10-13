using DinoCrisis.Model;
using DinoCrisis.Model.Tests; // linked SyntheticBlob
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 2 (G3): EMR / skeleton emit. The synthetic DCM has part0 (root, rest 0,-100,0)
/// and part1 (child of 0, rest 10,20,30).</summary>
public class EmrInterchangeTests
{
    private static InterchangeMesh SyntheticMesh() =>
        DcmInterchange.ToInterchange(
            DcmModel.Read(SyntheticBlob.Build(DcmVariant.Dc1), 0, SyntheticBlob.Base, DcmVariant.Dc1));

    [Fact]
    public void ToEmr_roundtrips_parts_relative_positions_and_child_tree()
    {
        var emr = EmrInterchange.ToEmr(SyntheticMesh());

        Assert.Equal(2, emr.NumParts);

        var root = emr.GetRelativePosition(0);
        Assert.Equal((short)0, root.x);
        Assert.Equal((short)-100, root.y);
        Assert.Equal((short)0, root.z);

        var child = emr.GetRelativePosition(1);
        Assert.Equal((short)10, child.x);
        Assert.Equal((short)20, child.y);
        Assert.Equal((short)30, child.z);

        // Part 0 is the parent of part 1; part 1 is a leaf.
        Assert.Contains((byte)1, emr.GetArmatureParts(0).ToArray());
        Assert.Empty(emr.GetArmatureParts(1).ToArray());
    }

    [Fact]
    public void ToEmr_final_position_accumulates_down_the_chain()
    {
        var emr = EmrInterchange.ToEmr(SyntheticMesh());

        // FK: root (0,-100,0) + child rel (10,20,30) = (10,-80,30).
        var fp = emr.GetFinalPosition(1);
        Assert.Equal((short)10, fp.x);
        Assert.Equal((short)-80, fp.y);
        Assert.Equal((short)30, fp.z);
    }

    [Fact]
    public void ToEmr_rejects_skeleton_whose_root_is_not_part0()
    {
        var mesh = SyntheticMesh();
        mesh.Parts[0].Parent = 0; // root no longer has a negative parent
        Assert.Throws<System.ArgumentException>(() => EmrInterchange.ToEmr(mesh));
    }

    [Fact]
    public void ToEmr_writes_one_armature_entry_per_part()
    {
        // RelativePositions.Count must equal Armatures.Count or Emr.Builder throws — a 2-part mesh
        // must therefore yield exactly 2 armature slots (the leaf's is empty).
        var emr = EmrInterchange.ToEmr(SyntheticMesh());
        Assert.Equal(2, emr.NumArmatures);
    }
}
