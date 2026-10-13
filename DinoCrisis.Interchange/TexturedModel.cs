using DinoCrisis.Model;
using DinoCrisis.Model.Textures;

namespace DinoCrisis.Interchange;

/// <summary>
/// Builds a <b>texture-coherent</b> interchange model (Inc 5): the rig-preserving mesh from
/// <see cref="DcmInterchange"/> plus a folded skin (<see cref="TextureAtlas"/>) with every polygon's UVs
/// rewritten to address that skin in RE's (page, u, v) scheme. Feed <see cref="Skin"/> to
/// <see cref="TimInterchange.ToTim"/> for the TIM and <see cref="Mesh"/> to <see cref="EmdAssembler"/>
/// for the EMD; together they sample correctly. Returns <c>null</c> when the skin cannot fold into RE
/// addressing (<see cref="TextureAtlas.CanFit"/>) — the caller then ships the model untextured.
/// </summary>
public sealed class TexturedModel
{
    /// <summary>The rig-preserving mesh with RE-resolved UVs + page per polygon.</summary>
    public InterchangeMesh Mesh { get; }

    /// <summary>The folded skin (≤256 tall), ready for <see cref="TimInterchange.ToTim"/>.</summary>
    public PsxImage Skin { get; }

    private TexturedModel(InterchangeMesh mesh, PsxImage skin)
    {
        Mesh = mesh;
        Skin = skin;
    }

    /// <summary>Build the textured model from a parsed DCM model and its decoded texture, or null if the
    /// skin does not fit RE addressing.</summary>
    public static TexturedModel? Build(DcmModel model, DcmTexture tex)
    {
        if (model is null) throw new System.ArgumentNullException(nameof(model));
        if (tex is null) throw new System.ArgumentNullException(nameof(tex));
        if (!TextureAtlas.CanFit(tex.Image.Width, tex.Image.Height, tex.TexelsPerWord))
            return null;

        var mesh = DcmInterchange.ToInterchange(model);
        var skin = TextureAtlas.Repack(tex.Image);

        // DC1 carries tpage per polygon; DC2 carries one in the header (all polys share it).
        bool dc2 = model.Variant.Game == DcmGame.Dc2;
        ushort headerTpage = model.Header.TPage;

        int triCursor = 0, quadCursor = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            var mp = mesh.Parts[pi];

            for (int t = 0; t < part.TriangleCount; t++)
            {
                var src = model.Triangles[triCursor + t];
                ushort tp = dc2 ? headerTpage : src.TPage;
                var mt = mp.Triangles[t];
                mt.Page = Resolve(tex, tp, src.Uv0, out var a);
                mt.Ua = a;
                Resolve(tex, tp, src.Uv1, out var b); mt.Ub = b;
                Resolve(tex, tp, src.Uv2, out var c); mt.Uc = c;
            }
            triCursor += part.TriangleCount;

            for (int q = 0; q < part.QuadCount; q++)
            {
                var src = model.Quads[quadCursor + q];
                ushort tp = dc2 ? headerTpage : src.TPage;
                var mq = mp.Quads[q];
                mq.Page = Resolve(tex, tp, src.Uv0, out var a);
                mq.Ua = a;
                Resolve(tex, tp, src.Uv1, out var b); mq.Ub = b;
                Resolve(tex, tp, src.Uv2, out var c); mq.Uc = c;
                Resolve(tex, tp, src.Uv3, out var d); mq.Ud = d;
            }
            quadCursor += part.QuadCount;
        }

        return new TexturedModel(mesh, skin);
    }

    private static int Resolve(DcmTexture tex, ushort tpage, Uv uv, out Uv resolved)
    {
        var (page, u, v) = TextureAtlas.ResolveCorner(
            tex.EntryX16, tex.EntryY, tex.TexelsPerWord, tex.Image.Width, tpage, uv.U, uv.V);
        resolved = new Uv(u, v);
        return page;
    }
}
