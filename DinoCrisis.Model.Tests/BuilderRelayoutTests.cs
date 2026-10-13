using System;
using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Increment 1 of the DcmModelBuilder (docs/dc1/DCM-MODEL-BUILDER-PLAN.md): the re-layout serializer
/// builds a fresh, valid, parseable DCM blob from the builder's lists — recomputing pointers, the
/// per-part partition, the pool offsets, and the header totals — so a later increment can add/remove
/// parts. Oracle: serialize → re-read → structurally equal + passes Validate, and re-serialization is
/// idempotent. Synthetic runs anywhere; the golden re-derives it from every §7 model.
/// </summary>
public sealed class BuilderRelayoutTests
{
    /// <summary>Serialize → re-read → assert a faithful, valid round-trip (counts, pools, idempotence).</summary>
    private static void AssertRoundTrips(DcmModel model)
    {
        uint baseVa = model.LoadBase;
        byte[] b1 = model.ToBuilder().Serialize(baseVa);

        var rt = DcmModel.Read(b1, 0, baseVa, model.Variant);
        Assert.True(DcmModel.Validate(rt), "serialized model must satisfy the §7 invariants");

        Assert.Equal(model.Parts.Count, rt.Parts.Count);
        Assert.Equal(model.Vertices.Count, rt.Vertices.Count);
        Assert.Equal(model.Normals.Count, rt.Normals.Count);
        Assert.Equal(model.Triangles.Count, rt.Triangles.Count);
        Assert.Equal(model.Quads.Count, rt.Quads.Count);
        Assert.Equal(model.Header.TriangleCount, rt.Header.TriangleCount);
        Assert.Equal(model.Header.QuadCount, rt.Header.QuadCount);

        // pools survive verbatim (SVector is a value type → sequence equality)
        Assert.Equal(model.Vertices, rt.Vertices);
        Assert.Equal(model.Normals, rt.Normals);
        // header tpage/clut preserved for DC2
        Assert.Equal(model.Header.TPage, rt.Header.TPage);
        Assert.Equal(model.Header.Clut, rt.Header.Clut);

        // re-layout is idempotent: serializing the re-read model reproduces the same bytes
        byte[] b2 = rt.ToBuilder().Serialize(baseVa);
        Assert.Equal(b1, b2);
    }

    [Theory]
    [InlineData(DcmGame.Dc1)]
    [InlineData(DcmGame.Dc2)]
    public void Synthetic_SerializeRoundTrips(DcmGame game)
    {
        var v = DcmVariant.For(game);
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        AssertRoundTrips(model);
    }

    [Fact]
    public void Serialize_ThrowsOnInconsistentPartition()
    {
        var v = DcmVariant.Dc1;
        var b = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!.ToBuilder();
        // add a triangle to the flat list without bumping any part's TriangleCount → partition broken
        b.Triangles.Add(b.Triangles[0]);
        Assert.Throws<InvalidOperationException>(() => b.Serialize(b.LoadBase));
    }

    [Fact]
    public void Synthetic_SerializeAtArbitraryBase_StillValid()
    {
        var v = DcmVariant.Dc2;
        var model = DcmModel.TryReadAt(SyntheticBlob.Build(v), 0, SyntheticBlob.Base, v)!;
        byte[] blob = model.ToBuilder().Serialize(0x40000000u); // a different base than the source
        var rt = DcmModel.Read(blob, 0, 0x40000000u, v);
        Assert.True(DcmModel.Validate(rt));
        Assert.Equal(model.Triangles.Count, rt.Triangles.Count);
    }

    // ---- gated golden: every §7 model re-lays-out faithfully ----

    [Dc1Theory("st605.dat", "st106.dat", "core00.dat")]
    [InlineData("st605.dat", 22)]
    [InlineData("st106.dat", 15)]
    [InlineData("core00.dat", 15)]
    public void Golden_Dc1_SerializeRoundTrips(string file, int parts)
    {
        var model = DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File(file))))
            .FirstOrDefault(m => m.Header.PartCount == parts);
        Assert.NotNull(model);
        AssertRoundTrips(model!);
    }

    [Dc2Theory("E00.DAT", "E90.DAT")]
    [InlineData("E00.DAT")]
    [InlineData("E90.DAT")]
    public void Golden_Dc2_SerializeRoundTrips(string file)
    {
        string path = FixtureGate.Dc2File(file);
        AssertRoundTrips(DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(TestData.Pristine(path))));
    }
}
