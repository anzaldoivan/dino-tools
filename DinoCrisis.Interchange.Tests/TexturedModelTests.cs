using System.IO;
using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 5 gated golden: a real DC1 textured model folds into a coherent RE atlas + UVs,
/// then composes through ToTim + Assemble into a textured EMD. Skipped when game data is absent.</summary>
public sealed class TexturedModelTests
{
    [Dc1Fact("core00.dat")]
    public void Real_regina_builds_texture_coherent_model_and_textured_emd()
    {
        // Prefer the pristine backup: this golden asserts against REAL Regina data, and core00.dat
        // may hold an installed model swap (backup-and-swap discipline keeps the pristine in the bak).
        string corePath = FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat"));
        var bytes = File.ReadAllBytes(corePath);
        var model = DcmLocator.FromDc1Package(bytes).FirstOrDefault(m => m.Header.PartCount == 15);
        Assert.NotNull(model);
        var tex = DcmTextureDecoder.TryDecode(GianPackage.TryParse(bytes)!, bytes, model!);
        Assert.NotNull(tex); // Regina skin (256×464)

        var textured = TexturedModel.Build(model!, tex!);
        Assert.NotNull(textured);

        // Skin folded to a single 256-tall page row; width = srcWidth * numBands.
        Assert.Equal(256, textured!.Skin.Height);
        Assert.Equal(TextureAtlas.RepackedWidth(tex!.Image.Width, tex.Image.Height), textured.Skin.Width);

        // The load-bearing proof: for EVERY polygon corner, the RE UV the mesh now carries addresses the
        // SAME colour in the folded atlas that the model originally sampled in the source skin. Texture
        // and UVs agree — a self-consistent textured model.
        int triCursor = 0;
        for (int pi = 0; pi < model!.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            var mp = textured.Mesh.Parts[pi];
            for (int t = 0; t < part.TriangleCount; t++)
            {
                var src = model.Triangles[triCursor + t];
                var mt = mp.Triangles[t];
                AssertCornerAgrees(tex, src.TPage, src.Uv0, mt.Page, mt.Ua, textured.Skin);
                AssertCornerAgrees(tex, src.TPage, src.Uv1, mt.Page, mt.Ub, textured.Skin);
                AssertCornerAgrees(tex, src.TPage, src.Uv2, mt.Page, mt.Uc, textured.Skin);
                Assert.True(mt.Page <= 15);
            }
            triCursor += part.TriangleCount;
        }

        // Composes end-to-end: folded skin → TIM, mesh → RE2 EMD.
        var tim = TimInterchange.ToTim(textured.Skin);
        Assert.Equal(textured.Skin.Width, tim.Width);
        var emd = EmdAssembler.Assemble(SyntheticEmd.BuildRe2(), textured.Mesh, EmdAssembler.RigMode.KeepDonorRig);
        using var ms = new MemoryStream(emd, writable: false);
        Assert.Equal(15, new EmdFile(BioVersion.Biohazard2, ms).Md1.NumParts);
    }

    private static void AssertCornerAgrees(DcmTexture tex, ushort tpage, Uv uv, int page, Uv re, PsxImage skin)
    {
        // Where the model sampled in the SOURCE skin.
        int pageX16 = (tpage & 0xF) * 64, pageY = ((tpage >> 4) & 1) * 256;
        int col = (pageX16 - tex.EntryX16) * tex.TexelsPerWord + uv.U;
        int row = (pageY - tex.EntryY) + uv.V;
        int so = (row * tex.Image.Width + col) * 4;

        // Where the RE UV points in the FOLDED skin (u must stay within a 128-wide page; v is a byte).
        int ax = page * 128 + re.U, ay = re.V;
        Assert.True(re.U < 128);
        int ao = (ay * skin.Width + ax) * 4;

        Assert.Equal(
            (tex.Image.Pixels[so], tex.Image.Pixels[so + 1], tex.Image.Pixels[so + 2], tex.Image.Pixels[so + 3]),
            (skin.Pixels[ao], skin.Pixels[ao + 1], skin.Pixels[ao + 2], skin.Pixels[ao + 3]));
    }
}
