using System.Buffers.Binary;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;

namespace DinoCrisis.Interchange;

/// <summary>
/// One explicitly parsed donor texture page.  The coordinates are the donor's absolute indexed
/// texel origin; they are not inferred from a file name or from a target TPAGE.
/// </summary>
public readonly record struct Dc1DonorPage(
    ushort Code,
    int X,
    int Y,
    int Width = TextureAtlas.PageWidth,
    int Height = TextureAtlas.PageHeight,
    int PaletteRow = 0);

/// <summary>
/// The source-side texture contract required by the private DC1 cross-rig atlas converter.
/// Palette values are PSX 16-bit colours and <see cref="Indices"/> contains one indexed texel per
/// source pixel.  The donor game/version is supplied by parsed source metadata.
/// </summary>
public sealed class Dc1DonorTexture
{
    public Dc1DonorTexture(
        BioVersion donorVersion,
        int width,
        int height,
        int texelsPerWord,
        byte[] indices,
        ushort[] palette,
        IReadOnlyList<Dc1DonorPage> pages)
        : this(donorVersion, width, height, texelsPerWord, indices,
            palette is null ? throw new ArgumentNullException(nameof(palette)) : new[] { palette }, pages)
    {
    }

    public Dc1DonorTexture(
        BioVersion donorVersion,
        int width,
        int height,
        int texelsPerWord,
        byte[] indices,
        IReadOnlyList<ushort[]> paletteRows,
        IReadOnlyList<Dc1DonorPage> pages)
    {
        DonorVersion = donorVersion;
        Width = width;
        Height = height;
        TexelsPerWord = texelsPerWord;
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        PaletteRows = paletteRows ?? throw new ArgumentNullException(nameof(paletteRows));
        Pages = pages ?? throw new ArgumentNullException(nameof(pages));
    }

    public BioVersion DonorVersion { get; }
    public int Width { get; }
    public int Height { get; }
    public int TexelsPerWord { get; }
    public byte[] Indices { get; }
    public IReadOnlyList<ushort[]> PaletteRows { get; }
    public IReadOnlyList<Dc1DonorPage> Pages { get; }

    /// <summary>The only palette row for legacy single-CLUT callers; mixed rows refuse explicitly.</summary>
    public ushort[] Palette => PaletteRows.Count == 1
        ? PaletteRows[0]
        : throw new InvalidOperationException(
            "donor texture has multiple palette rows; select the row through page metadata");
}

/// <summary>A source corner and its deterministic target-window mapping.</summary>
public sealed record Dc1AtlasCornerSample(
    ushort SourceTPage,
    Uv SourceUv,
    Uv TargetUv,
    int PaletteRow,
    byte PaletteIndex,
    ushort PaletteValue);

/// <summary>Result of the explicit private target-native DC1 texture conversion.</summary>
public sealed record Dc1TargetNativeAtlasConversionResult(
    byte[] PackageBytes,
    byte[] DecodedModelBytes,
    DcmModel Model,
    byte[] TargetLinearTexture,
    byte[] TargetPaletteBytes,
    int TextureEntryIndex,
    int PaletteEntryIndex,
    IReadOnlyList<Dc1AtlasCornerSample> CornerSamples);

/// <summary>
/// Converts a parsed donor's indexed page rectangles into the proven Regina DC1 GIAN layout.
/// This is deliberately a separate cross-rig surface: the production same-rig importer does not
/// call it, and unresolved RE1/RE2 donor conventions are refused rather than guessed.
/// </summary>
public static class Dc1TargetNativeAtlasConverter
{
    public const int TargetTextureX16 = 896;
    public const int TargetTextureY = 0;
    public const int TargetTextureW16 = 128;
    public const int TargetTextureVisibleHeight = 464;
    public const int TargetPaletteX16 = 768;
    public const int TargetPaletteY = 502;
    public const int TargetPaletteW16 = 256;
    public const int TargetPaletteHeight = 2;

    /// <summary>Derive the target-native CLUT code for one of the two palette-entry rows.</summary>
    public static ushort TargetClutForPaletteRow(int row)
    {
        if ((uint)row >= TargetPaletteHeight)
            throw new ArgumentOutOfRangeException(nameof(row), row,
                "target-native Regina palette entry has exactly two rows");
        return (ushort)((TargetPaletteY + row) << 6 | (TargetPaletteX16 / 16));
    }

    /// <summary>
    /// Convert <paramref name="sourceModel"/>'s indexed corners and rebuild the target package's
    /// texture/palette entries while preserving all other entry payloads and metadata.
    /// </summary>
    public static Dc1TargetNativeAtlasConversionResult Convert(
        byte[] targetPackageBytes,
        int targetModelEntryIndex,
        DcmModel targetModel,
        DcmModel sourceModel,
        Dc1DonorTexture donor)
    {
        ArgumentNullException.ThrowIfNull(targetPackageBytes);
        ArgumentNullException.ThrowIfNull(targetModel);
        ArgumentNullException.ThrowIfNull(sourceModel);
        ArgumentNullException.ThrowIfNull(donor);

        Require(!targetPackageBytes.AsSpan().IsEmpty, "target package is empty");
        Require(targetModel.Variant.Game == DcmGame.Dc1, "target model must be DC1");
        Require(sourceModel.Variant.Game == DcmGame.Dc1, "source model must be DC1");
        Require(DcmModel.Validate(targetModel), "target model failed strict validation");
        // A run-aware cross-rig source deliberately appends its canonical pools after the preserved
        // target template, so the ordinary same-rig Validate() placement invariant does not apply.
        // DcmModel.Read has already checked every pointer span/count; the run-aware caller owns the
        // additional RTPT/padding legality gate.
        Require(sourceModel.Vertices.Count == sourceModel.Normals.Count,
            "source position and normal pools are not parallel");
        Require(sourceModel.Parts.Sum(p => p.TriangleCount) == sourceModel.Triangles.Count &&
                sourceModel.Parts.Sum(p => p.QuadCount) == sourceModel.Quads.Count,
            "source primitive partitions are inconsistent");

        var package = GianPackage.TryParse(targetPackageBytes)
            ?? throw new InvalidDataException("DC1 target-native atlas: target GIAN package is invalid");
        Require(!package.IsDc2, "target package must use the DC1 entry table");
        Require(targetModelEntryIndex >= 0 && targetModelEntryIndex < package.Entries.Count,
            "target model entry index is outside the package");
        Require(package.Entries[targetModelEntryIndex].Type is GianEntryType.Data or GianEntryType.Lzss0 or
                GianEntryType.Lzss1 or GianEntryType.Lzss2 or GianEntryType.Unknown,
            "target model entry has an unsupported type");

        byte[] targetModelBytes = package.DecodeEntry(targetPackageBytes, package.Entries[targetModelEntryIndex]);
        Require(targetModelBytes.AsSpan().SequenceEqual(targetModel.Raw),
            "target model does not describe the selected package entry");

        var targetCodes = EnumerateTextureCodes(targetModel).ToArray();
        Require(targetCodes.Length > 0, "target model has no textured primitives");
        ushort targetTPage = targetCodes[0].TPage;
        ushort targetClut = targetCodes[0].Clut;
        Require(targetCodes.All(c => c.TPage == targetTPage && c.Clut == targetClut),
            "target-native Regina contract requires one TPAGE and one CLUT");

        var targetTexture = FindTargetTexture(package, targetTPage);
        var targetPalette = FindTargetPalette(package, targetClut);
        Require(targetTexture.Type == GianEntryType.Lzss2,
            "target-native Regina texture storage must remain Lzss2");
        Require(targetTexture.GfxX == TargetTextureX16 && targetTexture.GfxY == TargetTextureY &&
                targetTexture.GfxW == TargetTextureW16 && targetTexture.GfxH == TargetTextureVisibleHeight,
            "target-native Regina texture metadata is not the proven {896,0,128,464} contract");
        Require(targetPalette.Type == GianEntryType.Palette &&
                targetPalette.GfxX == TargetPaletteX16 && targetPalette.GfxY == TargetPaletteY &&
                targetPalette.GfxW == TargetPaletteW16 && targetPalette.GfxH == TargetPaletteHeight,
            "target-native Regina palette metadata is not the proven {768,502,256,2} contract");

        ValidateTargetCodes(targetTPage, targetClut);
        byte[] targetLinear = DecodeTargetLinear(package, targetPackageBytes, targetTexture);
        byte[] targetPaletteBytes = package.DecodeEntry(targetPackageBytes, targetPalette);

        ValidateDonor(donor);
        Require(sourceModel.Parts.Count == targetModel.Parts.Count,
            "source and target part counts differ; target ownership cannot be preserved");
        for (int i = 0; i < targetModel.Parts.Count; i++)
        {
            var targetPart = targetModel.Parts[i];
            var sourcePart = sourceModel.Parts[i];
            Require(targetPart.Parent == sourcePart.Parent,
                $"source part {i} does not preserve target hierarchy ownership");
        }

        var referencedCodes = EnumerateTextureCodes(sourceModel).Select(c => c.TPage).Distinct().ToArray();
        Require(referencedCodes.Length > 0, "source model has no textured primitives");
        Require(referencedCodes.Length <= TargetTextureW16 * 2 / TextureAtlas.PageWidth,
            "source pages cannot fit the target 256-pixel texture window");
        var pageByCode = donor.Pages.ToDictionary(p => p.Code);
        var orderedPages = donor.Pages
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.Code)
            .ToArray();
        var destinationSlots = orderedPages
            .Select((page, slot) => (page.Code, Slot: slot))
            .ToDictionary(x => x.Code, x => x.Slot);
        foreach (ushort code in referencedCodes)
            Require(pageByCode.ContainsKey(code), $"source TPAGE 0x{code:X4} has no parsed donor page");

        var sourceRows = orderedPages.Select(page => page.PaletteRow).Distinct().ToArray();
        Require(sourceRows.Length <= TargetPaletteHeight,
            "source uses more distinct palette rows than the target palette entry can represent");
        var targetPaletteRowBySourceRow = sourceRows
            .OrderBy(row => row)
            .Select((row, slot) => (row, slot))
            .ToDictionary(x => x.row, x => x.slot);
        var targetPaletteRowByPageCode = orderedPages.ToDictionary(
            page => page.Code, page => targetPaletteRowBySourceRow[page.PaletteRow]);

        // The target texture is a 256-texel-wide 8bpp window.  Copy complete donor page cells into
        // deterministic 128-pixel columns, leaving every lower target row untouched.
        foreach (var page in orderedPages)
        {
            int slot = destinationSlots[page.Code];
            for (int row = 0; row < TextureAtlas.PageHeight; row++)
            {
                int sourceOffset = checked((page.Y + row) * donor.Width + page.X);
                int destinationOffset = checked(row * (TargetTextureW16 * 2) + slot * TextureAtlas.PageWidth);
                donor.Indices.AsSpan(sourceOffset, TextureAtlas.PageWidth)
                    .CopyTo(targetLinear.AsSpan(destinationOffset, TextureAtlas.PageWidth));
            }
        }

        byte[] convertedPalette = (byte[])targetPaletteBytes.Clone();
        foreach (var sourceRow in sourceRows)
        {
            int targetRow = targetPaletteRowBySourceRow[sourceRow];
            int paletteRowOffset = ResolvePaletteRowOffset(
                targetPalette, TargetClutForPaletteRow(targetRow), convertedPalette.Length);
            var sourcePalette = donor.PaletteRows[sourceRow];
            Require(sourcePalette.Length == 256,
                $"source palette row {sourceRow} must contain exactly 256 entries");
            for (int i = 0; i < sourcePalette.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    convertedPalette.AsSpan(paletteRowOffset + i * 2, 2), sourcePalette[i]);
        }

        var sourceBuilder = sourceModel.ToBuilder();
        var cornerSamples = new List<Dc1AtlasCornerSample>(checked(sourceModel.Triangles.Count * 3 + sourceModel.Quads.Count * 4));
        int triangleCursor = 0, quadCursor = 0;
        for (int partIndex = 0; partIndex < sourceModel.Parts.Count; partIndex++)
        {
            var part = sourceModel.Parts[partIndex];
            for (int i = 0; i < part.TriangleCount; i++)
            {
                var source = sourceModel.Triangles[triangleCursor + i];
                var output = sourceBuilder.Triangles[triangleCursor + i];
                output.TPage = targetTPage;
                output.Clut = TargetClutForPaletteRow(targetPaletteRowByPageCode[source.TPage]);
                output.Uv0 = MapCorner(source.TPage, source.Uv0, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
                output.Uv1 = MapCorner(source.TPage, source.Uv1, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
                output.Uv2 = MapCorner(source.TPage, source.Uv2, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
            }
            triangleCursor += part.TriangleCount;

            for (int i = 0; i < part.QuadCount; i++)
            {
                var source = sourceModel.Quads[quadCursor + i];
                var output = sourceBuilder.Quads[quadCursor + i];
                output.TPage = targetTPage;
                output.Clut = TargetClutForPaletteRow(targetPaletteRowByPageCode[source.TPage]);
                output.Uv0 = MapCorner(source.TPage, source.Uv0, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
                output.Uv1 = MapCorner(source.TPage, source.Uv1, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
                output.Uv2 = MapCorner(source.TPage, source.Uv2, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
                output.Uv3 = MapCorner(source.TPage, source.Uv3, donor, pageByCode, destinationSlots,
                    targetPaletteRowByPageCode, cornerSamples);
            }
            quadCursor += part.QuadCount;
        }

        byte[] decodedModelBytes = sourceBuilder.Write();
        var outputModel = DcmModel.Read(
            decodedModelBytes, sourceModel.ModelHead, sourceModel.LoadBase, DcmVariant.Dc1);
        Require(outputModel.Vertices.Count == outputModel.Normals.Count,
            "converted position and normal pools are not parallel");
        Require(outputModel.Parts.Count == targetModel.Parts.Count &&
                outputModel.Triangles.Count == sourceModel.Triangles.Count &&
                outputModel.Quads.Count == sourceModel.Quads.Count,
            "converted model changed target part count or primitive counts");

        var packageBuilder = new GianPackageBuilder();
        for (int i = 0; i < package.Entries.Count; i++)
        {
            var entry = package.Entries[i];
            byte[] payload = i switch
            {
                var index when index == targetModelEntryIndex => entry.IsCompressed
                    ? Lzss.Compress(decodedModelBytes)
                    : (byte[])decodedModelBytes.Clone(),
                var index when index == targetTexture.IndexOf(package) =>
                    Lzss.Compress(SwizzleTarget(targetLinear, targetTexture)),
                var index when index == targetPalette.IndexOf(package) => convertedPalette,
                _ => targetPackageBytes.AsSpan(entry.PayloadOffset, checked((int)entry.DeclaredSize)).ToArray(),
            };
            packageBuilder.Add(entry.Type, payload, entry.GfxX, entry.GfxY, entry.GfxW, entry.GfxH);
        }

        byte[] outputPackage = packageBuilder.Build();
        var reparsedPackage = GianPackage.TryParse(outputPackage)
            ?? throw new InvalidDataException("target-native atlas: rebuilt GIAN package is invalid");
        byte[] reparsedModelBytes = reparsedPackage.DecodeEntry(
            outputPackage, reparsedPackage.Entries[targetModelEntryIndex]);
        var reparsedModel = DcmModel.Read(
            reparsedModelBytes, sourceModel.ModelHead, sourceModel.LoadBase, DcmVariant.Dc1);
        Require(reparsedModel.Vertices.Count == reparsedModel.Normals.Count,
            "rebuilt package position and normal pools are not parallel");
        Require(reparsedModel.Triangles.Count == outputModel.Triangles.Count &&
                reparsedModel.Quads.Count == outputModel.Quads.Count,
            "rebuilt package changed primitive counts");

        return new(
            outputPackage,
            reparsedModelBytes,
            reparsedModel,
            targetLinear,
            convertedPalette,
            targetTexture.IndexOf(package),
            targetPalette.IndexOf(package),
            cornerSamples.AsReadOnly());
    }

    private static byte[] SwizzleTarget(byte[] linear, GianEntry texture)
    {
        byte[] swizzled = DcmTextureDecoder.SwizzleGfx(linear, texture.GfxW * 2, texture.GfxH);
        Require(swizzled.Length > 0, "target texture cannot be tile-swizzled");
        return swizzled;
    }

    private static IEnumerable<(ushort TPage, ushort Clut)> EnumerateTextureCodes(DcmModel model)
    {
        foreach (var triangle in model.Triangles)
            yield return (triangle.TPage, triangle.Clut);
        foreach (var quad in model.Quads)
            yield return (quad.TPage, quad.Clut);
    }

    private static GianEntry FindTargetTexture(GianPackage package, ushort tpage)
    {
        int pageX16 = (tpage & 0x0F) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        var matches = package.Entries
            .Where(e => e.Type is GianEntryType.Texture or GianEntryType.Lzss1 or GianEntryType.Lzss2)
            .Where(e => e.GfxX <= pageX16 && pageX16 < e.GfxX + e.GfxW &&
                        e.GfxY <= pageY && pageY < e.GfxY + e.GfxH)
            .ToArray();
        Require(matches.Length == 1, "target TPAGE does not identify exactly one texture entry");
        return matches[0];
    }

    private static GianEntry FindTargetPalette(GianPackage package, ushort clut)
    {
        int clutX16 = (clut & 0x3F) << 4;
        int clutY = clut >> 6;
        var matches = package.Entries
            .Where(e => e.Type == GianEntryType.Palette)
            .Where(e => e.GfxX <= clutX16 && clutX16 + 256 <= e.GfxX + e.GfxW &&
                        e.GfxY <= clutY && clutY < e.GfxY + e.GfxH)
            .ToArray();
        Require(matches.Length == 1, "target CLUT does not identify exactly one palette entry");
        return matches[0];
    }

    private static byte[] DecodeTargetLinear(GianPackage package, byte[] fileBytes, GianEntry texture)
    {
        int rowBytes = checked(texture.GfxW * 2);
        byte[] stored = package.DecodeEntry(fileBytes, texture);
        byte[] linear = DcmTextureDecoder.UnswizzleGfx(stored, rowBytes, texture.GfxH);
        int storedHeight = checked((texture.GfxH + 31) / 32 * 32);
        Require(linear.Length == checked(rowBytes * storedHeight),
            "target Lzss2 texture does not decode to its rounded storage height");
        return linear;
    }

    private static int ResolvePaletteRowOffset(GianEntry palette, ushort clut, int dataLength)
    {
        int clutX16 = (clut & 0x3F) << 4;
        int clutY = clut >> 6;
        int rowStride = checked(palette.GfxW * 2);
        int offset = checked((clutY - palette.GfxY) * rowStride + (clutX16 - palette.GfxX) * 2);
        Require(offset >= 0 && offset + 512 <= dataLength,
            "target CLUT row is outside the palette entry");
        return offset;
    }

    private static void ValidateTargetCodes(ushort tpage, ushort clut)
    {
        int depthMode = (tpage >> 7) & 3;
        Require(depthMode == 1, "target-native atlas requires an 8bpp target TPAGE");
        int pageX16 = (tpage & 0x0F) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        Require(pageX16 == TargetTextureX16 && pageY == TargetTextureY,
            "target TPAGE does not select the proven Regina texture origin");
        Require(((clut & 0x3F) << 4) == TargetPaletteX16 && (clut >> 6) == TargetPaletteY,
            "target CLUT does not select the proven Regina palette row");
    }

    private static void ValidateDonor(Dc1DonorTexture donor)
    {
        Require(donor.DonorVersion == BioVersion.Biohazard3,
            "RE1/RE2 donor conventions are unresolved; only parsed RE3 is supported");
        Require(donor.TexelsPerWord == 2, "donor texture must be indexed 8bpp");
        long pixelCount = (long)donor.Width * donor.Height;
        Require(donor.Width > 0 && donor.Height > 0 && pixelCount <= int.MaxValue &&
                donor.Indices.Length == pixelCount,
            "donor indexed texture dimensions do not match its payload");
        Require(donor.PaletteRows.Count > 0, "donor must provide at least one palette row");
        Require(donor.PaletteRows.All(row => row.Length == 256),
            "every donor palette row must contain exactly 256 entries");
        Require(donor.Pages.Count is > 0 and <= 2,
            "donor page count cannot be represented by one target 256x256 window");
        Require(donor.Pages.Select(p => p.Code).Distinct().Count() == donor.Pages.Count,
            "donor page codes are ambiguous");

        foreach (var page in donor.Pages)
        {
            Require(((page.Code >> 7) & 3) == 1,
                $"donor TPAGE 0x{page.Code:X4} is not indexed 8bpp");
            int codeX = ((page.Code & 0x0F) * 64) * donor.TexelsPerWord;
            int codeY = ((page.Code >> 4) & 1) * 256;
            Require(page.X == codeX && page.Y == codeY,
                $"donor page 0x{page.Code:X4} does not match its parsed origin");
            Require(page.Width == TextureAtlas.PageWidth && page.Height == TextureAtlas.PageHeight,
                "donor page dimensions must be exactly 128x256");
            Require(page.X >= 0 && page.Y >= 0 && page.X % TextureAtlas.PageWidth == 0 &&
                    (long)page.X + page.Width <= donor.Width && (long)page.Y + page.Height <= donor.Height,
                "donor page rectangle is outside the parsed donor atlas");
            Require((uint)page.PaletteRow < (uint)donor.PaletteRows.Count,
                $"donor page 0x{page.Code:X4} has no palette row {page.PaletteRow}");
        }

        for (int i = 0; i < donor.Pages.Count; i++)
            for (int j = i + 1; j < donor.Pages.Count; j++)
            {
                var a = donor.Pages[i];
                var b = donor.Pages[j];
                bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width &&
                               a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Require(!overlap, "donor page rectangles overlap ambiguously");
            }
    }

    private static Uv MapCorner(
        ushort sourceTPage,
        Uv sourceUv,
        Dc1DonorTexture donor,
        IReadOnlyDictionary<ushort, Dc1DonorPage> pages,
        IReadOnlyDictionary<ushort, int> destinationSlots,
        IReadOnlyDictionary<ushort, int> targetPaletteRows,
        ICollection<Dc1AtlasCornerSample> samples)
    {
        Require(pages.TryGetValue(sourceTPage, out var page),
            $"source TPAGE 0x{sourceTPage:X4} has no donor page metadata");
        int sourceX = checked(page.X + sourceUv.U);
        int sourceY = checked(page.Y + sourceUv.V);
        Require(sourceUv.U < page.Width && sourceUv.V < page.Height &&
                sourceX < donor.Width && sourceY < donor.Height,
            "source polygon corner is outside its donor page or target 256x256 window");
        int slot = destinationSlots[sourceTPage];
        int targetU = checked(slot * TextureAtlas.PageWidth + sourceUv.U);
        Require(targetU < TargetTextureW16 * 2,
            "source polygon corner cannot remain within one target 256x256 window");

        byte index = donor.Indices[sourceY * donor.Width + sourceX];
        int sourcePaletteRow = page.PaletteRow;
        Require(targetPaletteRows.ContainsKey(sourceTPage),
            $"source TPAGE 0x{sourceTPage:X4} has no target palette-row mapping");
        samples.Add(new Dc1AtlasCornerSample(
            sourceTPage, sourceUv, new Uv(checked((byte)targetU), sourceUv.V),
            sourcePaletteRow, index, donor.PaletteRows[sourcePaletteRow][index]));
        return new Uv(checked((byte)targetU), sourceUv.V);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException("DC1 target-native atlas: " + message);
    }

    private static int IndexOf(this GianEntry entry, GianPackage package)
    {
        for (int i = 0; i < package.Entries.Count; i++)
            if (package.Entries[i].Equals(entry)) return i;
        throw new InvalidDataException("target-native atlas: selected GIAN entry was not found");
    }
}
