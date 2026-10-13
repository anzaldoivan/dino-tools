using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Fail-first coverage for the verified RE3 indexed multi-CLUT contract.</summary>
public sealed class Dc1JillPaletteRowTests
{
    private const uint Base = 0x80000000;

    [Theory]
    [InlineData((ushort)0x0000, 0, 0, 0, 0)]
    [InlineData((ushort)0x8000, 0, 0, 0, 255)]
    [InlineData((ushort)0x001F, 255, 0, 0, 255)]
    [InlineData((ushort)0x03E0, 0, 255, 0, 255)]
    [InlineData((ushort)0x7C00, 0, 0, 255, 255)]
    public void Bgr555_transparency_and_nonzero_black_follow_decoder_semantics(
        ushort value, int r, int g, int b, int a)
    {
        Assert.Equal((r, g, b, a), PsxImage.FromPsv(value));
    }

    [Fact]
    public void FromTim_preserves_8bpp_geometry_indices_palette_rows_and_page_ownership()
    {
        var tim = new IntelOrca.Biohazard.TimFile(512, 256, 8);
        tim.ResizeCluts(4);
        var distinctRows = Enumerable.Range(0, 2)
            .Select(row => Enumerable.Range(0, 256)
                .Select(index => (ushort)(row * 0x20 + index + (index == 0 ? 0 : 0x8000)))
                .ToArray())
            .ToArray();
        var rows = new[] { distinctRows[0], distinctRows[1], distinctRows[0], distinctRows[1] };
        for (int row = 0; row < rows.Length; row++)
            tim.SetPalette(row, rows[row]);
        tim.SetRawPixel(0, 0, 7);
        tim.SetRawPixel(128, 0, 9);
        var rawTim = tim.GetBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(rawTim.AsSpan(14, 2), 480);
        tim = new IntelOrca.Biohazard.TimFile(rawTim);

        var skin = DcTextureEncoder.FromTim(tim);

        Assert.Equal((512, 256), (skin.Width, skin.Height));
        Assert.Equal(9, tim.PixelFormat);
        Assert.Equal(4, tim.PaletteCount);
        Assert.Equal((0, 480), (tim.PaletteOriginX, tim.PaletteOriginY));
        Assert.Equal(512 * 256, skin.Indices.Length);
        Assert.Equal(7, skin.Indices[0]);
        Assert.Equal(9, skin.Indices[128]);
        Assert.Equal(4, skin.PaletteRows.Count);
        Assert.Equal(rows[0], skin.PaletteRows[0]);
        Assert.Equal(rows[1], skin.PaletteRows[1]);
        Assert.Equal(rows[0], skin.PaletteRows[2]);
        Assert.Equal(rows[1], skin.PaletteRows[3]);
        Assert.Equal(256, rows[0].Zip(rows[1]).Count(pair => pair.First != pair.Second));
        Assert.Equal((ushort)0x80, skin.Pages.Single(page => page.X == 0).TPage);
        Assert.Equal((ushort)0x81, skin.Pages.Single(page => page.X == 128).TPage);
        Assert.Equal(0, skin.Pages.Single(page => page.X == 0).PaletteRow);
        Assert.Equal(1, skin.Pages.Single(page => page.X == 128).PaletteRow);
        Assert.Throws<InvalidOperationException>(() => _ = skin.Clut);
        Assert.Throws<ArgumentException>(() => DcTextureEncoder.WriteDc2Tex(skin));
        Assert.Throws<ArgumentException>(() => DcPackageAssembler.BuildDc1(new InterchangeMesh(), skin));
    }

    [Fact]
    public void FromTim_rejects_4bpp_instead_of_treating_nibbles_as_8bpp_indices()
    {
        var tim = new IntelOrca.Biohazard.TimFile(Build4BppTimBytes(128, 256));
        Assert.Equal(8, tim.PixelFormat);
        Assert.Throws<NotSupportedException>(() => DcTextureEncoder.FromTim(tim));
    }

    [Fact]
    public void Target_native_conversion_keeps_page_specific_cluts_and_decodes_page_one_rgba()
    {
        var target = BuildTarget(out var targetPackage);
        var source = BuildSource();
        var donor = BuildTwoRowDonor();

        var result = Dc1TargetNativeAtlasConverter.Convert(
            targetPackage, targetModelEntryIndex: 0, target, source, donor);
        var repeated = Dc1TargetNativeAtlasConverter.Convert(
            targetPackage, targetModelEntryIndex: 0, target, source, donor);
        Assert.Equal(result.PackageBytes, repeated.PackageBytes);

        Assert.Equal((ushort)0x7DB0, result.Model.Triangles[0].Clut);
        Assert.Equal((ushort)0x7DF0, result.Model.Triangles[1].Clut);
        Assert.Equal(new Uv(255, 5), result.Model.Triangles[1].Uv1);
        Assert.Equal(0, result.CornerSamples[0].PaletteRow);
        Assert.Contains(result.CornerSamples, sample => sample.SourceTPage == 0x81 && sample.PaletteRow == 1);

        var package = GianPackage.TryParse(result.PackageBytes)!;
        var texture = package.Entries[result.TextureEntryIndex];
        Assert.Equal(GianEntryType.Lzss2, texture.Type);
        Assert.Equal((ushort)896, texture.GfxX);
        Assert.Equal((ushort)0, texture.GfxY);
        Assert.Equal((ushort)128, texture.GfxW);
        Assert.Equal((ushort)464, texture.GfxH);
        var storedTexture = package.DecodeEntry(result.PackageBytes, texture);
        Assert.Equal(256 * 480, storedTexture.Length);
        Assert.Equal(result.TargetLinearTexture,
            DcmTextureDecoder.UnswizzleGfx(storedTexture, 256, 464));
        var palette = package.DecodeEntry(result.PackageBytes, package.Entries[result.PaletteEntryIndex]);
        Assert.Equal(donor.PaletteRows[0], ReadPaletteRow(palette, 0));
        Assert.Equal(donor.PaletteRows[1], ReadPaletteRow(palette, 1));

        // The existing representative decoder must refuse this mixed-CLUT atlas rather than flattening
        // it to the first primitive's palette row.
        Assert.Null(DcmTextureDecoder.TryDecode(package, result.PackageBytes, result.Model));

        var pageAware = DcmTextureDecoder.TryDecodePageAware(
            package, result.PackageBytes, result.Model,
            new Dictionary<int, ushort> { [0] = 0x7DB0, [128] = 0x7DF0 });
        Assert.NotNull(pageAware);
        Assert.Equal((256, 464), (pageAware!.Image.Width, pageAware.Image.Height));
        Assert.Equal((255, 0, 0, 255), Pixel(pageAware.Image, 0, 4));
        Assert.Equal((0, 255, 0, 255), Pixel(pageAware.Image, 128, 4));

        var png = PngEncoder.Encode(pageAware.Image);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(8, png[24]); // IHDR bit depth.
        Assert.Equal(6, png[25]); // IHDR colour type: RGBA8.
    }

    [Fact]
    public void Target_clut_rows_are_derived_and_bounded_by_the_two_row_entry()
    {
        Assert.Equal((ushort)0x7DB0, Dc1TargetNativeAtlasConverter.TargetClutForPaletteRow(0));
        Assert.Equal((ushort)0x7DF0, Dc1TargetNativeAtlasConverter.TargetClutForPaletteRow(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc1TargetNativeAtlasConverter.TargetClutForPaletteRow(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc1TargetNativeAtlasConverter.TargetClutForPaletteRow(2));
    }

    [Fact]
    public void Conversion_rejects_more_referenced_palette_rows_than_target_can_store()
    {
        var target = BuildTarget(out var targetPackage);
        var source = BuildSourceWithThreePages();
        var donor = BuildTwoRowDonor(
            pages: new[]
            {
                new Dc1DonorPage(0x80, 0, 0, PaletteRow: 0),
                new Dc1DonorPage(0x81, 128, 0, PaletteRow: 1),
                new Dc1DonorPage(0x82, 256, 0, PaletteRow: 2),
            });

        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source, donor));
    }

    [Fact]
    public void Conversion_rejects_ambiguous_duplicate_donor_page_codes()
    {
        var target = BuildTarget(out var targetPackage);
        var source = BuildSource();
        var donor = BuildTwoRowDonor(
            pages: new[]
            {
                new Dc1DonorPage(0x80, 0, 0, PaletteRow: 0),
                new Dc1DonorPage(0x80, 0, 0, PaletteRow: 1),
            });

        Assert.Throws<InvalidDataException>(() =>
            Dc1TargetNativeAtlasConverter.Convert(targetPackage, 0, target, source, donor));
    }

    private static ushort[] ReadPaletteRow(byte[] bytes, int row)
    {
        var result = new ushort[256];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(row * 512 + i * 2, 2));
        return result;
    }

    private static byte[] Build4BppTimBytes(int width, int height)
    {
        int imageBytes = checked(width / 4 * height);
        var bytes = new byte[checked(8 + 12 + 16 * 2 + 12 + imageBytes)];
        int offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 16); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 8); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 12 + 16 * 2); offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 0); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 0); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 16); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 1); offset += 2;
        offset += 16 * 2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)(12 + imageBytes)); offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 0); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 0); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)(width / 4)); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)height);
        return bytes;
    }

    private static (int R, int G, int B, int A) Pixel(PsxImage image, int x, int y)
    {
        int offset = (y * image.Width + x) * 4;
        return (image.Pixels[offset], image.Pixels[offset + 1],
            image.Pixels[offset + 2], image.Pixels[offset + 3]);
    }

    private static DcmModel BuildTarget(out byte[] packageBytes)
    {
        var modelBlob = BuildModel(0x008E, 0x7DB0).Raw;
        var linear = Enumerable.Repeat((byte)0, 256 * 480).ToArray();
        var palette = new byte[1024];
        var package = new GianPackageBuilder()
            .Add(GianEntryType.Lzss2, Lzss.Compress(modelBlob), 0, 0, 0, 0)
            .Add(GianEntryType.Lzss2,
                Lzss.Compress(DcmTextureDecoder.SwizzleGfx(linear, 256, 464)),
                896, 0, 128, 464)
            .AddPalette(palette, 768, 502, 256, 2)
            .Build();
        packageBytes = package;
        var parsed = GianPackage.TryParse(package)!;
        return DcmModel.Read(parsed.DecodeEntry(package, parsed.Entries[0]), 0, Base, DcmVariant.Dc1);
    }

    private static DcmModel BuildSource()
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 6; i++)
            b.AddVertex(new SVector((short)(i * 10), (short)(i * 20), (short)(i * 30), 0),
                new SVector(0, 0, 4096, 0));
        b.AddPart(new DcmPart { Parent = -1, Child = 2 },
            new[]
            {
                new DcmTriangle { V0 = 0, V1 = 1, V2 = 2, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = 0x80, Clut = 0x7800 },
                new DcmTriangle { V0 = 3, V1 = 4, V2 = 5, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = 0x81, Clut = 0x7800 },
            }, Array.Empty<DcmQuad>());
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }

    private static DcmModel BuildSourceWithThreePages()
    {
        var b = new DcmModelBuilder(DcmVariant.Dc1);
        for (short i = 0; i < 9; i++)
            b.AddVertex(new SVector((short)(i * 10), (short)(i * 20), (short)(i * 30), 0),
                new SVector(0, 0, 4096, 0));
        b.AddPart(new DcmPart { Parent = -1, Child = 2 },
            new[]
            {
                new DcmTriangle { V0 = 0, V1 = 1, V2 = 2, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = 0x80, Clut = 0x7800 },
                new DcmTriangle { V0 = 3, V1 = 4, V2 = 5, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = 0x81, Clut = 0x7800 },
                new DcmTriangle { V0 = 6, V1 = 7, V2 = 8, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = 0x82, Clut = 0x7800 },
            }, Array.Empty<DcmQuad>());
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }

    private static Dc1DonorTexture BuildTwoRowDonor(IReadOnlyList<Dc1DonorPage>? pages = null)
    {
        var indices = new byte[256 * 256];
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 128; x++) indices[y * 256 + x] = 1;
            for (int x = 128; x < 256; x++) indices[y * 256 + x] = 2;
        }
        var rows = Enumerable.Range(0, 4)
            .Select(row => Enumerable.Range(0, 256)
                .Select(index => row switch
                {
                    0 => (ushort)(index == 1 ? 0x001F : 0),
                    1 => (ushort)(index == 2 ? 0x03E0 : 0),
                    _ => (ushort)0,
                }).ToArray())
            .ToArray();
        return new Dc1DonorTexture(
            BioVersion.Biohazard3, 256, 256, 2, indices, rows,
            pages ?? new[]
            {
                new Dc1DonorPage(0x80, 0, 0, PaletteRow: 0),
                new Dc1DonorPage(0x81, 128, 0, PaletteRow: 1),
            });
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
                new DcmTriangle { V0 = 0, V1 = 1, V2 = 2, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = tpage, Clut = clut },
                new DcmTriangle { V0 = 3, V1 = 4, V2 = 5, Uv0 = new Uv(0, 4), Uv1 = new Uv(127, 5), Uv2 = new Uv(64, 6), TPage = tpage, Clut = clut },
            }, Array.Empty<DcmQuad>());
        return DcmModel.Read(b.Serialize(Base), 0, Base, DcmVariant.Dc1);
    }
}
