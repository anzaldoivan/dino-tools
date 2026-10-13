using DinoCrisis.Model;

namespace DinoCrisis.Interchange;

/// <summary>
/// A neutral, rig-preserving mesh — the shared intermediate between DCM (Dino Crisis) and
/// RE EMD/MD1. It is deliberately format-agnostic: per-part, <b>bone-local</b> vertex pools
/// (NOT forward-kinematic baked), a parent tree, and per-corner UVs. This is the "G5"
/// primitive from <c>docs/dc1/CROSS-MODEL-PIPELINE-PARITY.md</c>: the DCM <see cref="ObjExporter"/>
/// bakes FK into a single flat group (great for viewing, lossy for rigs), whereas a transplant
/// needs each part's geometry in its own bone space plus the skeleton — exactly what this holds.
/// </summary>
public sealed class InterchangeMesh
{
    /// <summary>The parts (bones), in skeleton order. Part 0 is the root.</summary>
    public List<InterchangePart> Parts { get; } = new();
}

/// <summary>One part (bone): its rest pose relative to its parent, a parent index, and its own
/// bone-local geometry. Vertex/normal pools are <b>parallel</b> (one normal per position) and
/// polygon indices are <b>local</b> to this part's pool — the convention RE MD1 also uses
/// (each MD1 object descriptor carries its own vertex base).</summary>
public sealed class InterchangePart
{
    /// <summary>Rest-pose offset relative to the parent part (DCM part xyz / RE EMR relative pos).</summary>
    public short RestX { get; set; }
    public short RestY { get; set; }
    public short RestZ { get; set; }

    /// <summary>Parent part index; negative for the root.</summary>
    public int Parent { get; set; } = -1;

    /// <summary>Bone-local vertex positions (PSX SVECTOR units, raw — no scale applied). The 4th SVECTOR
    /// word (<see cref="SVector.Pad"/>) carries the per-vertex <b>bone index</b> — DC skins by it, so
    /// <c>DcmInterchange</c> preserves the source model's value and <c>ReverseInterchange.FromReMesh</c>
    /// stamps it with the owning part index (RE MD1 rigs by per-part object, one object per bone).</summary>
    public List<SVector> Positions { get; } = new();

    /// <summary>Per-vertex normals, parallel to <see cref="Positions"/> (equal count).</summary>
    public List<SVector> Normals { get; } = new();

    public List<InterchangeTriangle> Triangles { get; } = new();
    public List<InterchangeQuad> Quads { get; } = new();
}

/// <summary>A triangle with part-local vertex indices and per-corner UVs. <see cref="Page"/> is the
/// texture page (0..n); CLUT/atlas resolution is the texture stage's concern, not the mesh's.</summary>
public sealed class InterchangeTriangle
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public Uv Ua { get; set; }
    public Uv Ub { get; set; }
    public Uv Uc { get; set; }
    public int Page { get; set; }
}

/// <summary>A quad with part-local vertex indices and per-corner UVs.</summary>
public sealed class InterchangeQuad
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public Uv Ua { get; set; }
    public Uv Ub { get; set; }
    public Uv Uc { get; set; }
    public Uv Ud { get; set; }
    public int Page { get; set; }
}
