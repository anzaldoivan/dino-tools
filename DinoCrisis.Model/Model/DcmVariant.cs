namespace DinoCrisis.Model;

/// <summary>
/// Which Dino Crisis the DCM model came from — the one thing that varies between the two
/// otherwise-identical model layouts. Encapsulated as data (a strategy record) so a reader,
/// writer, or future third dialect is an entry here, not a new code path.
///
/// <para>Validated byte-for-byte in docs/dc1/CHARACTER-IMPORT-AUTORIG-RESEARCH.md §7. Shared by
/// both games: bone/part count at <c>header+0x14</c>; part-descriptor stride <c>0x14</c>
/// (xyz@+0, parent@+6, child@+7, triPtr@+8, quadPtr@+0xc, triCnt@+0x10, quadCnt@+0x12);
/// header <c>vertex_ptr@0 / normal_ptr@4 / tri_ptr@8 / quad_ptr@0xc</c>, totals
/// <c>tri_cnt@0x10 / quad_cnt@0x12</c>. The differences are below.</para>
/// </summary>
public enum DcmGame
{
    /// <summary>Dino Crisis 1: header 0x18 bytes, polys carry per-poly clut/tpage
    /// (<c>DCM1_TRIMAP</c> 16B, <c>DCM1_QUADMAP</c> 20B).</summary>
    Dc1,

    /// <summary>Dino Crisis 2: header 0x1c bytes (adds <c>tpage@0x18 / clut@0x1a</c>), polys are
    /// texture-coordinate-only (<c>DCM_TRIMAP</c> 12B, <c>DCM_QUADMAP</c> 16B).</summary>
    Dc2,
}

/// <summary>The fixed layout constants for one <see cref="DcmGame"/>. Pure data.</summary>
public sealed class DcmVariant
{
    /// <summary>Part-descriptor stride, shared by both games.</summary>
    public const int PartStride = 0x14;

    /// <summary>SVECTOR size (s16 x,y,z,pad), shared by both games.</summary>
    public const int VertexStride = 8;

    private DcmVariant(DcmGame game, int headerSize, int triStride, int quadStride, bool perPolyTexture)
    {
        Game = game;
        HeaderSize = headerSize;
        TriangleStride = triStride;
        QuadStride = quadStride;
        PerPolyTexture = perPolyTexture;
    }

    /// <summary>The game this variant describes.</summary>
    public DcmGame Game { get; }

    /// <summary>Header size in bytes; the part-descriptor array (<c>map[]</c>) begins here.</summary>
    public int HeaderSize { get; }

    /// <summary>Byte stride of one triangle record.</summary>
    public int TriangleStride { get; }

    /// <summary>Byte stride of one quad record.</summary>
    public int QuadStride { get; }

    /// <summary>True when each polygon carries its own clut/tpage (DC1); false when texture
    /// codes live in the header instead (DC2).</summary>
    public bool PerPolyTexture { get; }

    /// <summary>Offset of the part-descriptor array within the model block (== <see cref="HeaderSize"/>).</summary>
    public int MapOffset => HeaderSize;

    /// <summary>Dino Crisis 1 layout.</summary>
    public static readonly DcmVariant Dc1 = new(DcmGame.Dc1, headerSize: 0x18, triStride: 16, quadStride: 20, perPolyTexture: true);

    /// <summary>Dino Crisis 2 layout.</summary>
    public static readonly DcmVariant Dc2 = new(DcmGame.Dc2, headerSize: 0x1c, triStride: 12, quadStride: 16, perPolyTexture: false);

    /// <summary>The variant for a <see cref="DcmGame"/>.</summary>
    public static DcmVariant For(DcmGame game) => game == DcmGame.Dc1 ? Dc1 : Dc2;
}
