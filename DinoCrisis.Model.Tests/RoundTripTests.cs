using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class RoundTripTests
{
    private const uint SynthBase = SyntheticBlob.Base;

    private static byte[] BuildSynthetic(DcmVariant v) => SyntheticBlob.Build(v);

    [Theory]
    [InlineData(DcmGame.Dc1)]
    [InlineData(DcmGame.Dc2)]
    public void Synthetic_ParsesValidAndRoundTripsByteExact(DcmGame game)
    {
        var v = DcmVariant.For(game);
        byte[] blob = BuildSynthetic(v);

        var model = DcmModel.TryReadAt(blob, modelHead: 0, loadBase: SynthBase, v);
        Assert.NotNull(model);                       // passes every §7 invariant
        Assert.Equal(2, model!.Parts.Count);
        Assert.Equal(2, model.Triangles.Count);
        Assert.Single(model.Quads);
        Assert.Equal(game == DcmGame.Dc2 ? 6 : 4, model.VertexCount);

        Assert.Equal(blob, model.ToBuilder().Write()); // byte-exact no-op round trip
    }

    [Theory]
    [InlineData(DcmGame.Dc1)]
    [InlineData(DcmGame.Dc2)]
    public void Synthetic_EditedVertexSerializesAndReReads(DcmGame game)
    {
        var v = DcmVariant.For(game);
        var model = DcmModel.TryReadAt(BuildSynthetic(v), 0, SynthBase, v)!;

        var builder = model.ToBuilder();
        builder.Vertices[0] = new SVector(99, 98, 97, 5);
        byte[] edited = builder.Write();

        var reread = DcmModel.TryReadAt(edited, 0, SynthBase, v)!;
        Assert.Equal(new SVector(99, 98, 97, 5), reread.Vertices[0]);
        Assert.Equal(model.Vertices[1], reread.Vertices[1]); // neighbours untouched
        Assert.Equal(model.Parts.Count, reread.Parts.Count); // structure intact
    }

    // ---- golden round trip (gated): real models re-emit byte-exactly ----

    [Dc1Theory("st605.dat", "st106.dat", "core00.dat")]
    [InlineData("st605.dat", 22)]
    [InlineData("st106.dat", 15)]
    [InlineData("core00.dat", 15)]
    public void Dc1_RealModel_RoundTripsByteExact(string file, int parts)
    {
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File(file))));
        var model = models.First(m => m.Header.PartCount == parts);
        Assert.Equal(model.Raw, model.ToBuilder().Write());
    }

    [Dc2Theory("E00.DAT", "E90.DAT")]
    [InlineData("E00.DAT")]
    [InlineData("E90.DAT")]
    public void Dc2_RealModel_RoundTripsByteExact(string file)
    {
        string path = FixtureGate.Dc2File(file);
        var model = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(TestData.Pristine(path)));
        Assert.Equal(model.Raw, model.ToBuilder().Write());
    }
}
