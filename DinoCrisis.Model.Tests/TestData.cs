namespace DinoCrisis.Model.Tests;

/// <summary>
/// Golden-test data resolution. This repo's injection tooling swaps REAL game files in place
/// (e.g. a character import replaces <c>core00.dat</c> / <c>E00.DAT</c>) while keeping a pristine
/// <c>.dinorand-bak</c> sibling. Golden tests assert against the PRISTINE bytes, so they must
/// prefer the backup when one exists — otherwise an installed swap breaks every witness.
/// </summary>
internal static class TestData
{
    /// <summary>The pristine version of a game file: its <c>.dinorand-bak</c> sibling if present.</summary>
    public static string Pristine(string path)
        => File.Exists(path + ".dinorand-bak") ? path + ".dinorand-bak" : path;
}
