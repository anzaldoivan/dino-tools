using System.Linq;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>End-to-end: assemble a DC1 <c>.dat</c> from a textured interchange model + skin, then load it
/// back through this toolset's own readers — the model brute-scan AND the textured decode — proving the
/// package is one DC1 accepts.</summary>
public class DcPackageAssemblerTests
{
    private static InterchangeMesh OneTriMesh()
    {
        var mesh = new InterchangeMesh();
        var p = new InterchangePart { Parent = -1, RestX = 0, RestY = 0, RestZ = 0 };
        p.Positions.Add(new SVector(0, 0, 0, 0));
        p.Positions.Add(new SVector(100, 0, 0, 0));
        p.Positions.Add(new SVector(0, 100, 0, 0));
        for (int i = 0; i < 3; i++) p.Normals.Add(new SVector(0, 0, 4096, 0));
        p.Triangles.Add(new InterchangeTriangle
        {
            A = 0, B = 1, C = 2,
            Ua = new Uv(10, 20), Ub = new Uv(30, 40), Uc = new Uv(50, 60),
            Page = 0,
        });
        mesh.Parts.Add(p);
        return mesh;
    }

    private static DcSkin SyntheticSkin(int w = 256, int h = 256)
    {
        var indices = new byte[w * h];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i & 0xFF);
        var clut = new ushort[256];
        for (int i = 0; i < 256; i++) clut[i] = (ushort)(((i & 0x1F) << 10) | (((i * 3) & 0x1F) << 5) | (i & 0x1F) | (i == 0 ? 0 : 0));
        return new DcSkin(w, h, indices, clut);
    }

    [Fact]
    public void Assembled_dc1_dat_loads_model_and_textures_through_the_readers()
    {
        var skin = SyntheticSkin();
        var dat = DcPackageAssembler.BuildDc1(OneTriMesh(), skin, loadBase: 0x80100000, scale: 1.0);

        // Model reader finds our 1-part / 1-triangle model.
        var model = DcmLocator.FromDc1Package(dat).FirstOrDefault(m => m.Parts.Count == 1 && m.Header.TriangleCount == 1);
        Assert.NotNull(model);

        // Textured decode resolves the in-package Texture + Palette the model's tpage/clut point at.
        var pkg = GianPackage.TryParse(dat)!;
        var tex = DcmTextureDecoder.TryDecode(pkg, dat, model!);
        Assert.NotNull(tex);
        Assert.Equal(256, tex!.Image.Width);
        Assert.Equal(256, tex.Image.Height);

        // The decoded skin equals the source indices through the source CLUT (lossless in-package skin).
        for (int row = 0; row < 256; row++)
            for (int col = 0; col < 256; col++)
            {
                var (r, g, b, a) = PsxImage.FromPsv(skin.Clut[skin.Indices[row * 256 + col]]);
                int o = (row * 256 + col) * 4;
                Assert.Equal((r, g, b, a), (tex.Image.Pixels[o], tex.Image.Pixels[o + 1], tex.Image.Pixels[o + 2], tex.Image.Pixels[o + 3]));
            }

        // Every polygon UV maps into the texture.
        var tri = model!.Triangles[0];
        foreach (var uv in new[] { tri.Uv0, tri.Uv1, tri.Uv2 })
        {
            var (s, t) = tex.MapUv(uv.U, uv.V, tri.TPage);
            Assert.InRange(s, 0.0, 1.0);
            Assert.InRange(t, 0.0, 1.0);
        }
    }
}
