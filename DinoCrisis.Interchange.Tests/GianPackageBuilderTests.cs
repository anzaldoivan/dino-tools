using DinoCrisis.Model.Containers;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>The Gian-package writer round-trips through the reader (entries, types, gfx coords, payloads).</summary>
public class GianPackageBuilderTests
{
    [Fact]
    public void Build_round_trips_through_the_reader()
    {
        var model = new byte[] { 1, 2, 3, 4, 5 };
        var fb = new byte[100];
        for (int i = 0; i < fb.Length; i++) fb[i] = (byte)(i + 1);
        var pal = new byte[512];
        for (int i = 0; i < pal.Length; i++) pal[i] = (byte)(255 - (i & 0xFF));

        var bytes = new GianPackageBuilder()
            .AddData(model)
            .AddTexture(fb, x16: 10, y: 20, w16: 50, h: 30)
            .AddPalette(pal, x16: 0, y: 480, w16: 256, h: 1)
            .Build();

        var pkg = GianPackage.TryParse(bytes);
        Assert.NotNull(pkg);
        Assert.False(pkg!.IsDc2);                 // three entries ⇒ not mis-detected as DC2
        Assert.Equal(3, pkg.Entries.Count);

        Assert.Equal(GianEntryType.Data, pkg.Entries[0].Type);
        Assert.Equal((uint)5, pkg.Entries[0].DeclaredSize);

        var tex = pkg.Entries[1];
        Assert.Equal(GianEntryType.Texture, tex.Type);
        Assert.Equal((ushort)10, tex.GfxX);
        Assert.Equal((ushort)20, tex.GfxY);
        Assert.Equal((ushort)50, tex.GfxW);
        Assert.Equal((ushort)30, tex.GfxH);

        Assert.Equal(GianEntryType.Palette, pkg.Entries[2].Type);
        Assert.Equal((ushort)480, pkg.Entries[2].GfxY);

        // Payloads survive verbatim.
        Assert.Equal(model, pkg.DecodeEntry(bytes, pkg.Entries[0]));
        Assert.Equal(fb, pkg.DecodeEntry(bytes, pkg.Entries[1]));
        Assert.Equal(pal, pkg.DecodeEntry(bytes, pkg.Entries[2]));
    }
}
