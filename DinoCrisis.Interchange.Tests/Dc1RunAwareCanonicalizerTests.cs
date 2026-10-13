using DinoCrisis.Interchange;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Dc1RunAwareCanonicalizerTests
{
    private const uint Base = 0x80100000;
    [Fact]
    public void Repack_inserts_minimum_per_part_padding_and_preserves_visible_records()
    {
        var source = ReadSynthetic();
        var target = ReadSynthetic();

        var result = Dc1RunAwareCanonicalizer.Repack(
            source, target, [1, 5], maximumRecords: 32,
            maximumDecodedEndAddress: Base + 0x10000);

        Assert.Equal(new[] { 0, 3, 4, 5, 6, 7 }, result.OldToNewIndices);
        Assert.Equal(new[] { 1, 2, 8 }, result.PaddingIndices);
        Assert.Equal(9, result.Model.Vertices.Count);
        Assert.Equal(9, result.Model.Normals.Count);
        for (int old = 0; old < source.Vertices.Count; old++)
        {
            Assert.Equal(source.Vertices[old], result.Model.Vertices[result.OldToNewIndices[old]]);
            Assert.Equal(source.Normals[old], result.Model.Normals[result.OldToNewIndices[old]]);
        }
        Assert.All(result.PaddingIndices, i => Assert.Equal(default, result.Model.Vertices[i]));
        Assert.All(result.PaddingIndices, i => Assert.Equal(default, result.Model.Normals[i]));
        Assert.Equal(new byte[] { 1, 2 }, result.Model.Parts.Select(p => p.Child));
        Assert.True(Dc1TransformRunValidator.Validate(result.Model, result.PaddingIndices).IsLegal);
    }

    [Fact]
    public void Repack_is_deterministic_strictly_parseable_and_preserves_primitives_and_template_prefix()
    {
        var source = ReadSynthetic();
        source.Triangles[0].Uv0 = new Uv(7, 9);
        source.Triangles[0].Clut = 0x1234;
        source.Triangles[0].TPage = 0x5678;
        var target = ReadSynthetic();
        byte[] originalPrefix = (byte[])target.Raw.Clone();

        var a = Dc1RunAwareCanonicalizer.Repack(source, target, [3, 3], 32,
            Base + 0x10000);
        var b = Dc1RunAwareCanonicalizer.Repack(source, target, [3, 3], 32,
            Base + 0x10000);

        Assert.Equal(a.DecodedBytes, b.DecodedBytes);
        Assert.Empty(a.PaddingIndices);
        Assert.Equal(source.Triangles[0].Uv0, a.Model.Triangles[0].Uv0);
        Assert.Equal(source.Triangles[0].Clut, a.Model.Triangles[0].Clut);
        Assert.Equal(source.Triangles[0].TPage, a.Model.Triangles[0].TPage);
        int mapEnd = target.ModelHead + target.Variant.MapOffset + target.Parts.Count * DcmVariant.PartStride;
        Assert.Equal(originalPrefix.AsSpan(mapEnd), a.DecodedBytes.AsSpan(mapEnd, originalPrefix.Length - mapEnd));
        var reread = DcmModel.Read(a.DecodedBytes, target.ModelHead, target.LoadBase, DcmVariant.Dc1);
        Assert.Equal(a.Model.Vertices, reread.Vertices);
        Assert.Equal(a.Model.Normals, reread.Normals);
    }

    [Fact]
    public void Repack_rejects_malformed_ownership_and_capacity()
    {
        var model = ReadSynthetic();
        Assert.Throws<InvalidDataException>(() => Dc1RunAwareCanonicalizer.Repack(
            model, model, [3, 2], 32, Base + 0x10000));
        Assert.Throws<InvalidDataException>(() => Dc1RunAwareCanonicalizer.Repack(
            model, model, [1, 5], 8, Base + 0x10000));
    }

    [Fact]
    public void RebuildPackage_preserves_unrelated_payloads_and_metadata()
    {
        byte[] modelBlob = BuildBlob();
        byte[] package = new GianPackageBuilder()
            .Add(GianEntryType.Unknown, Lzss.Compress(modelBlob), 1, 2, 3, 4)
            .Add(GianEntryType.Data, [9, 8, 7], 5, 6, 7, 8)
            .Build();
        var result = Dc1RunAwareCanonicalizer.Repack(
            ReadSynthetic(), ReadSynthetic(), [3, 3], 32, Base + 0x10000);

        byte[] rebuilt = Dc1RunAwareCanonicalizer.RebuildPackage(package, 0, result.DecodedBytes);
        var before = GianPackage.TryParse(package)!;
        var after = GianPackage.TryParse(rebuilt)!;

        Assert.Equal(before.Entries[1].Type, after.Entries[1].Type);
        Assert.Equal((before.Entries[1].GfxX, before.Entries[1].GfxY, before.Entries[1].GfxW, before.Entries[1].GfxH),
            (after.Entries[1].GfxX, after.Entries[1].GfxY, after.Entries[1].GfxW, after.Entries[1].GfxH));
        Assert.Equal(before.DecodeEntry(package, before.Entries[1]), after.DecodeEntry(rebuilt, after.Entries[1]));
    }

    private static DcmModel ReadSynthetic()
    {
        byte[] blob = BuildBlob();
        return DcmModel.Read(blob, 0, Base, DcmVariant.Dc1);
    }

    private static byte[] BuildBlob()
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 6; i++)
            b.AddVertex(new SVector(i, (short)(i + 1), (short)(i + 2), 0), new SVector(0, 4096, 0, 0));
        b.AddPart(new DcmPart { Parent = -1, Child = 1 },
            [new DcmTriangle { V0 = 0, V1 = 1, V2 = 2, Uv0 = new Uv(1, 2), Uv1 = new Uv(3, 4), Uv2 = new Uv(5, 6), Clut = 7, TPage = 8 }], []);
        b.AddPart(new DcmPart { Parent = 0, Child = 1 },
            [new DcmTriangle { V0 = 3, V1 = 4, V2 = 5, Uv0 = new Uv(9, 10), Uv1 = new Uv(11, 12), Uv2 = new Uv(13, 14), Clut = 15, TPage = 16 }], []);
        return b.Serialize(Base);
    }
}
