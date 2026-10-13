using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Dc1TargetNativeAtlasConverterTests
{
    private const uint Base = 0x80000000;
    private const ushort TargetTPage = 0x008E;
    private const ushort TargetClut = 0x7DB0;

    [Fact]
    public void Swizzle_unswizzle_and_lzss_round_trip_partial_visible_height()
    {
        const int rowBytes = 256;
        const int visibleHeight = 464;
        int storedHeight = (visibleHeight + 31) / 32 * 32;
        var linear = Enumerable.Range(0, rowBytes * storedHeight)
            .Select(i => (byte)((i * 37 + 11) & 0xFF)).ToArray();

        var swizzled = DcmTextureDecoder.SwizzleGfx(linear, rowBytes, visibleHeight);
        var compressed = Lzss.Compress(swizzled);
        var roundTrip = DcmTextureDecoder.UnswizzleGfx(
            Lzss.Decompress(compressed), rowBytes, visibleHeight);

        Assert.Equal(linear, roundTrip);
        Assert.Equal(swizzled, DcmTextureDecoder.SwizzleGfx(roundTrip, rowBytes, visibleHeight));
    }

    [Fact]
    public void Conversion_folds_two_donor_pages_rewrites_codes_and_preserves_target_owned_regions()
    {
        var target = BuildTarget(out var targetPackage, out var originalLinear, out var originalPalette);
        var source = BuildSource();
        var donor = BuildDonor();

        var result = Dc1TargetNativeAtlasConverter.Convert(
            targetPackage, targetModelEntryIndex: 0, target, source, donor);

        Assert.Equal(6, result.Model.Vertices.Count);
        Assert.Equal(2, result.Model.Triangles.Count);
        Assert.Empty(result.Model.Quads);
        Assert.Equal(source.Parts.Select(p => (p.TriangleCount, p.QuadCount)),
            result.Model.Parts.Select(p => (p.TriangleCount, p.QuadCount)));
        Assert.Equal((ushort)0x008E, result.Model.Triangles[0].TPage);
        Assert.Equal((ushort)0x008E, result.Model.Triangles[1].TPage);
        Assert.Equal((ushort)0x7DB0, result.Model.Triangles[0].Clut);
        Assert.Equal((ushort)0x7DB0, result.Model.Triangles[1].Clut);
        Assert.Equal(new Uv(127, 5), result.Model.Triangles[0].Uv1);
        Assert.Equal(new Uv(255, 5), result.Model.Triangles[1].Uv1);

        var package = GianPackage.TryParse(result.PackageBytes);
        Assert.NotNull(package);
        Assert.Equal(GianEntryType.Lzss2, package!.Entries[1].Type);
        Assert.Equal((ushort)896, package.Entries[1].GfxX);
        Assert.Equal((ushort)0, package.Entries[1].GfxY);
        Assert.Equal((ushort)128, package.Entries[1].GfxW);
        Assert.Equal((ushort)464, package.Entries[1].GfxH);
        Assert.Equal(GianEntryType.Palette, package.Entries[2].Type);
        Assert.Equal((ushort)768, package.Entries[2].GfxX);
        Assert.Equal((ushort)502, package.Entries[2].GfxY);
        Assert.Equal((ushort)256, package.Entries[2].GfxW);
        Assert.Equal((ushort)2, package.Entries[2].GfxH);

        Assert.Equal(originalLinear.AsSpan(256 * 256), result.TargetLinearTexture.AsSpan(256 * 256));
        var palette = package.DecodeEntry(result.PackageBytes, package.Entries[2]);
        Assert.Equal(originalPalette.AsSpan(512), palette.AsSpan(512));
        Assert.Equal(new byte[] { 9, 8, 7 },
            package.DecodeEntry(result.PackageBytes, package.Entries[3]));

        var decoded = DcmTextureDecoder.TryDecode(package, result.PackageBytes, result.Model);
        Assert.NotNull(decoded);
        AssertCornerColours(source, result.Model, decoded!.Image, donor);

        var again = Dc1TargetNativeAtlasConverter.Convert(
            targetPackage, targetModelEntryIndex: 0, target, source, donor);
        Assert.Equal(result.PackageBytes, again.PackageBytes);
    }

    [Fact]
    public void Conversion_rejects_unsupported_donor_depth_palette_pages_and_version()
    {
        var target = BuildTarget(out var targetPackage, out _, out _);
        var source = BuildSource();

        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source,
                BuildDonor(texelsPerWord: 1)));
        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source,
                BuildDonor(palette: new ushort[512])));
        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source,
                BuildDonor(pages: new[]
                {
                    new Dc1DonorPage(0x80, 0, 0),
                    new Dc1DonorPage(0x81, 128, 0),
                    new Dc1DonorPage(0x82, 256, 0),
                })));
        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source,
                BuildDonor(version: (BioVersion)99)));
    }

    [Fact]
    public void Conversion_rejects_a_corner_outside_one_target_window()
    {
        var target = BuildTarget(out var targetPackage, out _, out _);
        var source = BuildSource(secondPageU: 128);

        var ex = Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(
                targetPackage, 0, target, source, BuildDonor()));
        Assert.Contains("256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversion_rejects_raw_texture_storage_even_when_pixels_are_decodable()
    {
        var target = BuildTarget(out var package, out _, out _, textureType: GianEntryType.Texture);

        var ex = Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(
                package, 0, target, BuildSource(), BuildDonor()));
        Assert.Contains("Lzss2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conversion_preserves_mixed_primitive_order_and_part_ownership()
    {
        var target = BuildTarget(out var targetPackage, out _, out _);
        var source = BuildSourceWithQuad();
        var result = Dc1TargetNativeAtlasConverter.Convert(
            targetPackage, 0, target, source, BuildDonor());

        Assert.Equal(2, result.Model.Triangles.Count);
        Assert.Single(result.Model.Quads);
        Assert.Equal(source.Parts.Select(p => (p.Parent, p.Child)),
            result.Model.Parts.Select(p => (p.Parent, p.Child)));
        Assert.Equal(source.Triangles.Select(t => (t.V0, t.V1, t.V2)),
            result.Model.Triangles.Select(t => (t.V0, t.V1, t.V2)));
        Assert.Equal(source.Quads.Select(q => (q.V0, q.V1, q.V2, q.V3)),
            result.Model.Quads.Select(q => (q.V0, q.V1, q.V2, q.V3)));
        Assert.All(result.Model.Triangles, t => Assert.Equal((ushort)0x008E, t.TPage));
        Assert.All(result.Model.Quads, q => Assert.Equal((ushort)0x008E, q.TPage));
        Assert.Equal(10, result.CornerSamples.Count);
    }

    private static DcmModel BuildTarget(
        out byte[] packageBytes,
        out byte[] linear,
        out byte[] palette,
        GianEntryType textureType = GianEntryType.Lzss2)
    {
        var modelBlob = BuildModel(TargetTPage, TargetClut).Raw;
        var builder = new GianPackageBuilder()
            .Add(GianEntryType.Lzss2, Lzss.Compress(modelBlob), 0, 0, 0, 0)
            .Add(textureType,
                textureType == GianEntryType.Lzss2
                    ? Lzss.Compress(DcmTextureDecoder.SwizzleGfx(
                        linear = TargetLinear(), 256, 464))
                    : linear = TargetLinear(),
                896, 0, 128, 464)
            .AddPalette(palette = TargetPalette(), 768, 502, 256, 2)
            .Add(GianEntryType.Data, new byte[] { 9, 8, 7 }, 0, 0, 0, 0);
        packageBytes = builder.Build();
        var package = GianPackage.TryParse(packageBytes)!;
        var decoded = package.DecodeEntry(packageBytes, package.Entries[0]);
        return DcmModel.Read(decoded, 0, Base, DcmVariant.Dc1);
    }

    private static byte[] TargetLinear()
        => Enumerable.Range(0, 256 * 480).Select(i => (byte)((i * 13 + 7) & 0xFF)).ToArray();

    private static byte[] TargetPalette()
    {
        var bytes = new byte[1024];
        for (int i = 0; i < 512; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(i * 2, 2), (ushort)((i * 17 + 1) & 0x7FFF));
        return bytes;
    }

    private static DcmModel BuildSource(int secondPageU = 127)
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 6; i++)
        {
            b.AddVertex(new SVector((short)(i * 10), (short)(i * 20), (short)(i * 30), 0),
                new SVector(0, 0, 4096, 0));
        }
        b.AddPart(new DcmPart { Parent = -1, Child = 2 },
            new[]
            {
                new DcmTriangle
                {
                    V0 = 0, V1 = 1, V2 = 2,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6),
                    TPage = 0x80, Clut = 0x7800,
                },
                new DcmTriangle
                {
                    V0 = 3, V1 = 4, V2 = 5,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv((byte)secondPageU, 5), Uv2 = new Uv(64, 6),
                    TPage = 0x81, Clut = 0x7800,
                },
            }, Array.Empty<DcmQuad>());
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }

    private static DcmModel BuildSourceWithQuad()
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 10; i++)
        {
            b.AddVertex(new SVector((short)(i * 10), (short)(i * 20), (short)(i * 30), 0),
                new SVector(0, 0, 4096, 0));
        }
        b.AddPart(new DcmPart { Parent = -1, Child = 2 },
            new[]
            {
                new DcmTriangle
                {
                    V0 = 0, V1 = 1, V2 = 2,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6),
                    TPage = 0x80, Clut = 0x7800,
                },
                new DcmTriangle
                {
                    V0 = 3, V1 = 4, V2 = 5,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6),
                    TPage = 0x81, Clut = 0x7800,
                },
            },
            new[]
            {
                new DcmQuad
                {
                    V0 = 6, V1 = 7, V2 = 8, V3 = 9,
                    Uv0 = new Uv(0, 8), Uv1 = new Uv(127, 9),
                    Uv2 = new Uv(127, 10), Uv3 = new Uv(0, 11),
                    TPage = 0x80, Clut = 0x7800,
                },
            });
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }

    private static Dc1DonorTexture BuildDonor(
        BioVersion version = BioVersion.Biohazard3,
        int texelsPerWord = 2,
        ushort[]? palette = null,
        IReadOnlyList<Dc1DonorPage>? pages = null)
    {
        var indices = Enumerable.Range(0, 256 * 256)
            .Select(i => (byte)((i / 256 + i % 256) & 0xFF)).ToArray();
        return new Dc1DonorTexture(
            version, 256, 256, texelsPerWord, indices,
            palette ?? Enumerable.Range(0, 256)
                .Select(i => (ushort)(i == 0 ? 0 : (i | (i << 5) | (i << 10))))
                .ToArray(),
            pages ?? new[]
            {
                new Dc1DonorPage(0x80, 0, 0),
                new Dc1DonorPage(0x81, 128, 0),
            });
    }

    private static void AssertCornerColours(
        DcmModel source, DcmModel result, PsxImage target, Dc1DonorTexture donor)
    {
        var sourceTriangles = source.Triangles;
        var resultTriangles = result.Triangles;
        Assert.Equal(sourceTriangles.Count, resultTriangles.Count);
        for (int i = 0; i < sourceTriangles.Count; i++)
        {
            var s = sourceTriangles[i];
            var r = resultTriangles[i];
            Assert.Equal((ushort)0x008E, r.TPage);
            Assert.Equal((ushort)0x7DB0, r.Clut);
            foreach (var pair in new[]
            {
                (s.Uv0, r.Uv0), (s.Uv1, r.Uv1), (s.Uv2, r.Uv2),
            })
            {
                var page = donor.Pages.Single(p => p.Code == s.TPage);
                int sx = page.X + pair.Item1.U;
                int sy = page.Y + pair.Item1.V;
                ushort colour = donor.Palette[donor.Indices[sy * donor.Width + sx]];
                var expected = PsxImage.FromPsv(colour);
                int o = (pair.Item2.V * target.Width + pair.Item2.U) * 4;
                Assert.Equal((expected.R, expected.G, expected.B, expected.A),
                    (target.Pixels[o], target.Pixels[o + 1], target.Pixels[o + 2], target.Pixels[o + 3]));
            }
        }
    }

    private static DcmModel BuildModel(ushort tpage, ushort clut)
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 6; i++)
            b.AddVertex(new SVector((short)(i * 10), (short)(i * 20), (short)(i * 30), 0),
                new SVector(0, 0, 4096, 0));
        b.AddPart(new DcmPart { Parent = -1, Child = 2 },
            new[]
            {
                new DcmTriangle
                {
                    V0 = 0, V1 = 1, V2 = 2,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6),
                    TPage = tpage, Clut = clut,
                },
                new DcmTriangle
                {
                    V0 = 3, V1 = 4, V2 = 5,
                    Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6),
                    TPage = tpage, Clut = clut,
                },
            }, Array.Empty<DcmQuad>());
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }
}
