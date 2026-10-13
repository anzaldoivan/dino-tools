using DinoCrisis.Model.Containers;

namespace DinoCrisis.Model.Textures;

/// <summary>
/// Writes a textured OBJ set — <c>&lt;name&gt;.obj</c> + <c>&lt;name&gt;.mtl</c> + <c>&lt;name&gt;.tga</c>.
/// DC1 textures live in the model's own Gian package (<c>core*.dat</c> / <c>stNXX.dat</c>); DC2 textures
/// live in a separate sibling <c>E*.TEX</c> whose bytes the caller supplies via <paramref name="texFileBytes"/>.
/// The OBJ geometry/assembly is <see cref="ObjExporter"/>'s; this adds the real UV mapping
/// (<see cref="DcmTexture.MapUv"/>), a material referencing the TGA, and the decoded image.
/// </summary>
public static class TexturedObjExporter
{
    private const string Material = "dcm";

    /// <summary>
    /// Decode the model's texture and write the three files into <paramref name="outDir"/> as
    /// <paramref name="baseName"/>.{obj,mtl,tga}. The texture source depends on the game: DC1 from the
    /// model's own package (<paramref name="fileBytes"/>); DC2 from <paramref name="texFileBytes"/> (the
    /// sibling <c>E*.TEX</c>). Returns false (writing an untextured OBJ instead) when no texture could be
    /// resolved — a DC2 model with no <c>.TEX</c> supplied, an unparseable package/<c>.TEX</c>, or a
    /// non-8bpp page.
    /// </summary>
    public static bool Export(byte[] fileBytes, DcmModel model, string outDir, string baseName,
                              bool flipY = true, byte[]? texFileBytes = null)
    {
        Directory.CreateDirectory(outDir);
        string objPath = Path.Combine(outDir, baseName + ".obj");

        DcmTexture? tex;
        if (model.Variant.Game == DcmGame.Dc2)
        {
            tex = texFileBytes is null ? null : DcmTextureDecoder.TryDecodeDc2(texFileBytes, model);
        }
        else
        {
            var pkg = GianPackage.TryParse(fileBytes);
            tex = pkg is null ? null : DcmTextureDecoder.TryDecode(pkg, fileBytes, model);
        }
        if (tex is null)
        {
            File.WriteAllText(objPath, ObjExporter.Export(model, flipY));
            return false;
        }

        string mtlName = baseName + ".mtl";
        string tgaName = baseName + ".tga";
        string obj = ObjExporter.BuildObj(model, flipY, tex.MapUv, mtlName, Material);
        File.WriteAllText(objPath, obj);
        File.WriteAllText(Path.Combine(outDir, mtlName), $"newmtl {Material}\nmap_Kd {tgaName}\n");
        tex.Image.WriteTga(Path.Combine(outDir, tgaName));
        return true;
    }
}
