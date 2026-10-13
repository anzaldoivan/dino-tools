using System;
using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>
/// The DC2 (32-byte-entry) Gian-package writer must round-trip EVERY entry-header field through the
/// reader — in particular the gfx VRAM rect <c>{x,y,w,h}</c> of Texture/Palette entries. Dropping
/// <c>GfxW</c>/<c>GfxH</c> (offset +12/+14) makes the game upload a zero-size texture rect, so the model
/// samples empty VRAM and renders completely WHITE. These tests pin that the rebuilt package keeps the
/// gfx coords and that the model's texture still decodes.
/// </summary>
public class GianPackageBuilderDc2Tests
{
    [Fact]
    public void BuildDc2_round_trips_all_entry_fields_including_gfx_w_and_h()
    {
        var model = new byte[] { 1, 2, 3, 4, 5 };
        var tex = new byte[200]; for (int i = 0; i < tex.Length; i++) tex[i] = (byte)(i + 1);
        var pal = new byte[512]; for (int i = 0; i < pal.Length; i++) pal[i] = (byte)(255 - (i & 0xFF));
        var sound = new byte[64];

        var bytes = new GianPackageBuilder()
            .AddDc2Model(model, loadBase: 0x00662500)
            .Add(GianEntryType.Texture, tex, x: 640, y: 256, w: 128, h: 200)
            .Add(GianEntryType.Palette, pal, x: 0, y: 480, w: 256, h: 1)
            .Add(GianEntryType.Sound, sound, x: 0, y: 0, w: 0, h: 0)
            .BuildDc2();

        var pkg = GianPackage.TryParse(bytes);
        Assert.NotNull(pkg);
        Assert.True(pkg!.IsDc2);
        Assert.Equal(4, pkg.Entries.Count);

        // DC2 model rides an uncompressed Data entry with its load base in reserve0.
        Assert.Equal(GianEntryType.Data, pkg.Entries[0].Type);
        Assert.Equal(0x00662500u, pkg.Entries[0].Reserve0);

        // Texture entry: ALL four gfx coords must survive — W and H are the ones the bug dropped.
        var t = pkg.Entries[1];
        Assert.Equal(GianEntryType.Texture, t.Type);
        Assert.Equal((ushort)640, t.GfxX);
        Assert.Equal((ushort)256, t.GfxY);
        Assert.Equal((ushort)128, t.GfxW);
        Assert.Equal((ushort)200, t.GfxH);

        var p = pkg.Entries[2];
        Assert.Equal(GianEntryType.Palette, p.Type);
        Assert.Equal((ushort)0, p.GfxX);
        Assert.Equal((ushort)480, p.GfxY);
        Assert.Equal((ushort)256, p.GfxW);
        Assert.Equal((ushort)1, p.GfxH);

        // Payloads survive verbatim.
        Assert.Equal(model, pkg.DecodeEntry(bytes, pkg.Entries[0]));
        Assert.Equal(tex, pkg.DecodeEntry(bytes, pkg.Entries[1]));
        Assert.Equal(pal, pkg.DecodeEntry(bytes, pkg.Entries[2]));
    }

    [Dc2Fact("WEP_P002.DAT")]
    public void Repackaging_stock_Regina_preserves_the_texture_decode()
    {
        // Regression for the WHITE-model bug, against the real game file (gated: skips if absent).
        string datPath = FixtureGate.Pristine(FixtureGate.Dc2File("WEP_P002.DAT"));

        const uint modelBase = 0x00662500;
        var fileBytes = File.ReadAllBytes(datPath);
        var pkg = GianPackage.TryParse(fileBytes)!;
        var modelEntry = pkg.Entries.First(e => e.Type == GianEntryType.Lzss0 && e.Reserve0 == modelBase);
        var stockBlob = pkg.DecodeEntry(fileBytes, modelEntry);
        var stockModel = DcmModel.Read(stockBlob, 0, modelBase, DcmVariant.Dc2);

        // Baseline: the stock model's texture decodes (this is what "textured, not white" means offline).
        var stockTex = DcmTextureDecoder.TryDecode(pkg, fileBytes, stockModel);
        Assert.NotNull(stockTex);

        // Rebuild the package the way the injector does: model as an uncompressed Data entry at its base,
        // every other entry carried verbatim (type + gfx coords + payload) via the production DC2 builder.
        var b = new GianPackageBuilder();
        foreach (var e in pkg.Entries)
        {
            var payload = fileBytes.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();
            if (e.Type == GianEntryType.Lzss0 && e.Reserve0 == modelBase)
                b.AddDc2Model(stockBlob, modelBase);
            else
                b.Add(e.Type, payload, e.GfxX, e.GfxY, e.GfxW, e.GfxH);
        }
        var rebuilt = b.BuildDc2();

        var rpkg = GianPackage.TryParse(rebuilt)!;
        Assert.True(rpkg.IsDc2);
        var rModelEntry = rpkg.Entries.First(e => e.Type == GianEntryType.Data && e.Reserve0 == modelBase);
        var rModel = DcmModel.Read(rpkg.DecodeEntry(rebuilt, rModelEntry), 0, modelBase, DcmVariant.Dc2);

        // The rebuilt package's model texture must STILL decode, pixel-identical to stock (no white).
        var rTex = DcmTextureDecoder.TryDecode(rpkg, rebuilt, rModel);
        Assert.NotNull(rTex);
        Assert.Equal(stockTex!.Image.Width, rTex!.Image.Width);
        Assert.Equal(stockTex.Image.Height, rTex.Image.Height);
        Assert.Equal(stockTex.Image.Pixels, rTex.Image.Pixels);
    }
}
