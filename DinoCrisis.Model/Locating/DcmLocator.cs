using DinoCrisis.Model.Containers;

namespace DinoCrisis.Model.Locating;

/// <summary>
/// Finds DCM models inside the three Dino Crisis container shapes and hands back parsed
/// <see cref="DcmModel"/>s. Each container yields a (buffer, head, base) the reader can consume:
/// <list type="bullet">
/// <item>DC1 room (<c>stNXX.dat</c>) / character (<c>core*.dat</c>): Gian package; each entry is
/// decompressed and brute-scanned (<see cref="ScanBuffer"/>) — the load base is derived per model.</item>
/// <item>DC2 enemy (<c>E*.DAT</c>): the model rides an <c>LZSS0</c> entry whose
/// <see cref="GianEntry.Reserve0"/> is the absolute load base; the header is at offset 0.</item>
/// </list>
/// </summary>
public static class DcmLocator
{
    /// <summary>Every valid DC1 model in a Gian package (room or character file), across all entries.</summary>
    public static IReadOnlyList<DcmModel> FromDc1Package(byte[] fileBytes)
    {
        var pkg = GianPackage.TryParse(fileBytes);
        if (pkg is null) return Array.Empty<DcmModel>();

        var models = new List<DcmModel>();
        foreach (var entry in pkg.Entries)
        {
            byte[] buffer = pkg.DecodeEntry(fileBytes, entry);
            models.AddRange(ScanBuffer(buffer, DcmVariant.Dc1));
        }
        return models;
    }

    /// <summary>
    /// Brute-scan a decompressed buffer for valid DCM model heads at 4-byte alignment. A candidate
    /// only survives if every <see cref="DcmModel.Validate"/> invariant holds, so false positives are
    /// rejected. Works for both DC1 in-RDT enemy models and standalone <c>core*</c> character models.
    /// </summary>
    public static IReadOnlyList<DcmModel> ScanBuffer(byte[] buffer, DcmVariant variant)
    {
        var models = new List<DcmModel>();
        int limit = buffer.Length - variant.HeaderSize - 4;
        for (int o = 0; o <= limit; o += 4)
        {
            var model = DcmModel.TryRead(buffer, o, variant);
            if (model is not null) models.Add(model);
        }
        return models;
    }

    /// <summary>
    /// Every DC2 model in an <c>E*.DAT</c>: each <c>LZSS0</c> entry whose decompressed header at
    /// offset 0 parses as a valid DCM, with the load base taken from the entry's
    /// <see cref="GianEntry.Reserve0"/>. Non-model LZSS0 entries (textures, params) are skipped.
    /// </summary>
    public static IReadOnlyList<DcmModel> FromDc2Package(byte[] fileBytes)
    {
        var pkg = GianPackage.TryParse(fileBytes);
        if (pkg is null) return Array.Empty<DcmModel>();

        var models = new List<DcmModel>();
        foreach (var entry in pkg.Entries)
        {
            if (entry.Type != GianEntryType.Lzss0) continue;
            byte[] buffer = pkg.DecodeEntry(fileBytes, entry);
            var model = DcmModel.TryReadAt(buffer, modelHead: 0, loadBase: entry.Reserve0, DcmVariant.Dc2);
            if (model is not null) models.Add(model);
        }
        return models;
    }

    /// <summary>The first DC2 model in an <c>E*.DAT</c>; throws if none parses.</summary>
    public static DcmModel FromDc2EnemyDat(byte[] fileBytes)
    {
        var models = FromDc2Package(fileBytes);
        return models.Count > 0 ? models[0]
            : throw new InvalidDataException("no LZSS0 entry parses as a DC2 DCM model");
    }

    /// <summary>
    /// Every DCM model in a Gian package, auto-detecting the game from the package entry stride
    /// (16 B → DC1 room/character scan; 32 B → DC2 <c>E*.DAT</c>). The one entry point a CLI or
    /// caller needs without knowing the file kind. Empty if the bytes are not a Gian package.
    /// </summary>
    public static IReadOnlyList<DcmModel> FromPackage(byte[] fileBytes)
    {
        var pkg = GianPackage.TryParse(fileBytes);
        if (pkg is null) return Array.Empty<DcmModel>();
        return pkg.IsDc2 ? FromDc2Package(fileBytes) : FromDc1Package(fileBytes);
    }
}
