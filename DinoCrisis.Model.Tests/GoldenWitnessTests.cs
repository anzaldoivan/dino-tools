using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Gated regression tests: re-derive the docs/dc1/CHARACTER-IMPORT-AUTORIG-RESEARCH.md §7
/// witnesses from real game data via this library's own reader (no DinoRand dependency).
/// Explicitly skipped when independently configured game data is unavailable; no copyrighted
/// bytes are committed.
/// </summary>
public sealed class GoldenWitnessTests
{
    private static string Dc1(string file) => TestData.Pristine(FixtureGate.Dc1File(file));

    // ---- DC1 in-RDT enemy models ----

    [Dc1Theory("st605.dat", "st106.dat")]
    [InlineData("st605.dat", 22, 334, 57)]   // Therizinosaurus
    [InlineData("st106.dat", 15, 282, 179)]  // Velociraptor
    public void Dc1_RoomEnemyModel_MatchesSection7(string file, int parts, int triTotal, int quadTotal)
    {
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(Dc1(file)));
        // A room can host several models of the same rig; locate the §7 one by its full signature
        // (this also self-checks that the reader reproduces that model's exact counts).
        var model = models.FirstOrDefault(m =>
            m.Header.PartCount == parts && m.Header.TriangleCount == triTotal && m.Header.QuadCount == quadTotal);
        Assert.NotNull(model);
        AssertWitnesses(model!, parts, triTotal, quadTotal, expectedBase: 0x80100000);
    }

    // ---- DC1 standalone character model (Regina, core00) ----

    [Dc1Fact("core00.dat")]
    public void Dc1_Core00Regina_MatchesSection7()
    {
        var models = DcmLocator.FromDc1Package(File.ReadAllBytes(Dc1("core00.dat")));
        var model = models.FirstOrDefault(m => m.Header.PartCount == 15);
        Assert.NotNull(model);
        // Regina humanoid: 15 parts, Σtri 314, Σquad 141, standalone load base 0x80180000.
        AssertWitnesses(model!, parts: 15, triTotal: 314, quadTotal: 141, expectedBase: 0x80180000);
        // The §7 humanoid parent tuple (byte-identical to the st50c corpse rig).
        Assert.Equal(
            new sbyte[] { -1, 0, 1, 2, 0, 4, 5, 0, 0, 8, 9, 10, 8, 12, 13 },
            model!.Parts.Select(p => p.Parent).ToArray());
    }

    // ---- DC2 enemy models (E*.DAT) ----

    [Dc2Theory("E00.DAT", "E90.DAT")]
    [InlineData("E00.DAT", 21, 300, 86, 0x633000u)]  // Velociraptor
    [InlineData("E90.DAT", 17, 234, 93, 0x63d000u)]  // Oviraptor
    public void Dc2_EnemyModel_MatchesSection7(string file, int parts, int triTotal, int quadTotal, uint loadBase)
    {
        string path = TestData.Pristine(FixtureGate.Dc2File(file));

        var model = DcmLocator.FromDc2EnemyDat(File.ReadAllBytes(path));
        Assert.Equal(DcmGame.Dc2, model.Variant.Game);
        AssertWitnesses(model, parts, triTotal, quadTotal, expectedBase: loadBase);
    }

    /// <summary>The five §7 invariants, asserted against a parsed model.</summary>
    internal static void AssertWitnesses(DcmModel m, int parts, int triTotal, int quadTotal, uint expectedBase)
    {
        Assert.Equal(expectedBase, m.LoadBase);

        // (a) obj_cnt == part count
        Assert.Equal(parts, m.Header.PartCount);
        Assert.Equal(parts, m.Parts.Count);

        // (c) per-part counts sum to the header totals
        Assert.Equal(triTotal, m.Header.TriangleCount);
        Assert.Equal(quadTotal, m.Header.QuadCount);
        Assert.Equal(triTotal, m.Parts.Sum(p => p.TriangleCount));
        Assert.Equal(quadTotal, m.Parts.Sum(p => p.QuadCount));

        // (c) pointer partition: each part's pool follows the previous by count*stride
        for (int i = 1; i < m.Parts.Count; i++)
        {
            Assert.Equal(m.Parts[i - 1].TrianglePtr + (uint)(m.Parts[i - 1].TriangleCount * m.Variant.TriangleStride),
                         m.Parts[i].TrianglePtr);
            Assert.Equal(m.Parts[i - 1].QuadPtr + (uint)(m.Parts[i - 1].QuadCount * m.Variant.QuadStride),
                         m.Parts[i].QuadPtr);
        }

        // (b) parent tree is valid (root negative, others reference an earlier part)
        Assert.True(m.Parts[0].Parent < 0);
        for (int i = 1; i < m.Parts.Count; i++)
            Assert.InRange(m.Parts[i].Parent, (sbyte)0, (sbyte)(i - 1));

        // vertex pool: every polygon index is in range
        Assert.True(m.Triangles.All(t => t.V0 < m.VertexCount && t.V1 < m.VertexCount && t.V2 < m.VertexCount));
        Assert.True(m.Quads.All(q => q.V0 < m.VertexCount && q.V1 < m.VertexCount
                                  && q.V2 < m.VertexCount && q.V3 < m.VertexCount));
        Assert.Equal(triTotal, m.Triangles.Count);
        Assert.Equal(quadTotal, m.Quads.Count);
    }
}
