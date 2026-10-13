namespace DinoCrisis.Model;

/// <summary>A PSX <c>SVECTOR</c>: three signed-16 coordinates plus a pad word (8 bytes).</summary>
public readonly record struct SVector(short X, short Y, short Z, short Pad);

/// <summary>A texture-coordinate pair (one polygon corner).</summary>
public readonly record struct Uv(byte U, byte V);

/// <summary>The DCM model header (counts + pool pointers). Mutable so the writer can re-emit it.</summary>
public sealed class DcmHeader
{
    /// <summary>VA of the vertex (SVECTOR) pool.</summary>
    public uint VertexPtr { get; set; }

    /// <summary>VA of the normal (SVECTOR) pool.</summary>
    public uint NormalPtr { get; set; }

    /// <summary>VA of the triangle pool (== part 0's triangle pointer).</summary>
    public uint TrianglePtr { get; set; }

    /// <summary>VA of the quad pool (== part 0's quad pointer).</summary>
    public uint QuadPtr { get; set; }

    /// <summary>Total triangle count across all parts.</summary>
    public int TriangleCount { get; set; }

    /// <summary>Total quad count across all parts.</summary>
    public int QuadCount { get; set; }

    /// <summary>Part (bone) count.</summary>
    public int PartCount { get; set; }

    /// <summary>DC2-only header texture page (0 on DC1).</summary>
    public ushort TPage { get; set; }

    /// <summary>DC2-only header CLUT (0 on DC1).</summary>
    public ushort Clut { get; set; }
}

/// <summary>One <c>DCM_MAPPING</c> part descriptor: a bone (rest position + parent) plus its
/// triangle/quad pool slice.</summary>
public sealed class DcmPart
{
    /// <summary>Rest-pose X relative to the parent.</summary>
    public short X { get; set; }

    /// <summary>Rest-pose Y relative to the parent.</summary>
    public short Y { get; set; }

    /// <summary>Rest-pose Z relative to the parent.</summary>
    public short Z { get; set; }

    /// <summary>Parent part index (the SDK's <c>super</c>); negative for the root.</summary>
    public sbyte Parent { get; set; }

    /// <summary>Legacy SDK/API name for descriptor byte +7. DC1 static decode proves this byte is
    /// the authored RTPT-triple count for the part's positional transform run; DC2 semantics are
    /// not changed by that finding.</summary>
    public byte Child { get; set; }

    /// <summary>VA of this part's first triangle.</summary>
    public uint TrianglePtr { get; set; }

    /// <summary>VA of this part's first quad.</summary>
    public uint QuadPtr { get; set; }

    /// <summary>Triangle count owned by this part.</summary>
    public int TriangleCount { get; set; }

    /// <summary>Quad count owned by this part.</summary>
    public int QuadCount { get; set; }
}

/// <summary>A triangle: three vertex indices + per-corner UVs (+ clut/tpage on DC1).</summary>
public sealed class DcmTriangle
{
    public ushort V0 { get; set; }
    public ushort V1 { get; set; }
    public ushort V2 { get; set; }
    public Uv Uv0 { get; set; }
    public Uv Uv1 { get; set; }
    public Uv Uv2 { get; set; }

    /// <summary>DC1-only per-poly CLUT (0 on DC2 — texture codes are in the header).</summary>
    public ushort Clut { get; set; }

    /// <summary>DC1-only per-poly texture page (0 on DC2).</summary>
    public ushort TPage { get; set; }
}

/// <summary>A quad: four vertex indices + per-corner UVs (+ clut/tpage on DC1).</summary>
public sealed class DcmQuad
{
    public ushort V0 { get; set; }
    public ushort V1 { get; set; }
    public ushort V2 { get; set; }
    public ushort V3 { get; set; }
    public Uv Uv0 { get; set; }
    public Uv Uv1 { get; set; }
    public Uv Uv2 { get; set; }
    public Uv Uv3 { get; set; }
    public ushort Clut { get; set; }
    public ushort TPage { get; set; }
}
