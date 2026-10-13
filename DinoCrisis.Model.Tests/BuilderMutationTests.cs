using System;
using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Increment 2 of the DcmModelBuilder (docs/dc1/DCM-MODEL-BUILDER-PLAN.md): AddPart / RemovePart mutate
/// the part list while keeping the flat triangle/quad partition and the parent tree consistent, so the
/// result serializes to a valid model. Oracle: mutate → Serialize → re-read → part count ±1, totals
/// updated, Validate holds.
/// </summary>
public sealed class BuilderMutationTests
{
    private static DcmModelBuilder Synth(DcmGame game)
    {
        var v = DcmVariant.For(game);
        return DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!.ToBuilder();
    }

    private static DcmModel Reread(DcmModelBuilder b)
        => DcmModel.Read(b.Serialize(b.LoadBase), 0, b.LoadBase, b.Variant);

    private static DcmTriangle Tri(ushort a, ushort c, ushort d) =>
        new() { V0 = a, V1 = c, V2 = d, Uv0 = new Uv(0, 0), Uv1 = new Uv(1, 1), Uv2 = new Uv(2, 2) };

    [Fact]
    public void AddPart_AppendsPartAndPolys_RoundTrips()
    {
        var b = Synth(DcmGame.Dc1);
        int parts0 = b.Parts.Count, tris0 = b.Triangles.Count;
        b.AddPart(new DcmPart { X = 5, Y = 6, Z = 7, Parent = 0 }, new[] { Tri(0, 1, 2) }, Array.Empty<DcmQuad>());

        Assert.Equal(parts0 + 1, b.Parts.Count);
        var rt = Reread(b);
        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(parts0 + 1, rt.Parts.Count);
        Assert.Equal(tris0 + 1, rt.Header.TriangleCount);
        Assert.Equal(1, rt.Parts[^1].TriangleCount);
        Assert.Equal((short)5, rt.Parts[^1].X);   // the new bone's rest position survived
    }

    [Fact]
    public void RemovePart_RemovesPartAndPolys_RoundTrips()
    {
        var b = Synth(DcmGame.Dc1);
        int tris0 = b.Triangles.Count, removed = b.Parts[1].TriangleCount;
        b.RemovePart(1);

        Assert.Single(b.Parts);
        var rt = Reread(b);
        Assert.True(DcmModel.Validate(rt));
        Assert.Single(rt.Parts);
        Assert.Equal(tris0 - removed, rt.Header.TriangleCount);
    }

    [Fact]
    public void RemovePart_GraftsOrphansOntoTheRemovedPartsParent()
    {
        var b = Synth(DcmGame.Dc1);           // part0 (root), part1 (parent 0)
        b.AddPart(new DcmPart { Parent = 1 }, Array.Empty<DcmTriangle>(), Array.Empty<DcmQuad>()); // part2 child of part1
        b.RemovePart(1);                       // part2 should graft onto part1's parent (0) and shift to index 1

        Assert.Equal(2, b.Parts.Count);
        Assert.Equal((sbyte)0, b.Parts[1].Parent);
        Assert.True(DcmModel.Validate(Reread(b)));
    }

    [Fact]
    public void Guards_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => Synth(DcmGame.Dc1).RemovePart(0)); // root
        Assert.Throws<ArgumentException>(() =>
            Synth(DcmGame.Dc1).AddPart(new DcmPart { Parent = 99 }, Array.Empty<DcmTriangle>(), Array.Empty<DcmQuad>()));
        Assert.Throws<ArgumentException>(() =>     // triangle references a vertex outside the pool
            Synth(DcmGame.Dc1).AddPart(new DcmPart { Parent = 0 }, new[] { Tri(999, 0, 1) }, Array.Empty<DcmQuad>()));
    }

    // ---- gated golden: add/remove on a real model ----

    [Dc1Fact("st605.dat")]
    public void Golden_Dc1_AddThenRemovePart_StaysValid() =>
        AssertGoldenAddThenRemovePartStaysValid("st605.dat", 22, DcmGame.Dc1);

    [Dc2Fact("E00.DAT")]
    public void Golden_Dc2_AddThenRemovePart_StaysValid() =>
        AssertGoldenAddThenRemovePartStaysValid("E00.DAT", 21, DcmGame.Dc2);

    private static void AssertGoldenAddThenRemovePartStaysValid(string file, int parts, DcmGame game)
    {
        DcmModel model;
        if (game == DcmGame.Dc2)
        {
            string p = FixtureGate.Dc2File(file);
            model = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(TestData.Pristine(p)));
        }
        else
        {
            model = DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File(file))))
                .First(m => m.Header.PartCount == parts);
        }

        // add a part that reuses an existing triangle (valid vertex indices), parented to the root
        var b = model.ToBuilder();
        var reused = b.Triangles[0];
        b.AddPart(new DcmPart { Parent = 0 }, new[] { reused }, Array.Empty<DcmQuad>());
        var added = Reread(b);
        Assert.True(DcmModel.Validate(added));
        Assert.Equal(parts + 1, added.Parts.Count);
        Assert.Equal(model.Header.TriangleCount + 1, added.Header.TriangleCount);

        // remove the last part (always a leaf) → back to the original counts, still valid
        b.RemovePart(b.Parts.Count - 1);
        var removed = Reread(b);
        Assert.True(DcmModel.Validate(removed));
        Assert.Equal(parts, removed.Parts.Count);
        Assert.Equal(model.Header.TriangleCount, removed.Header.TriangleCount);
    }
}
