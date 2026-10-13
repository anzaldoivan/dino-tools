namespace DinoCrisis.Model;

/// <summary>How a parsed DCM's position and normal counts were established.</summary>
public enum DcmPoolCountProvenance
{
    /// <summary>The strict native layout: both counts come from adjacent pointer spans.</summary>
    ContiguousPointerSpans,

    /// <summary>Positions come from the vertex-to-normal span; an explicit contract declares an
    /// equal, parallel normal count and proves every byte before the triangle pool.</summary>
    ParallelPoolsWithKnownSections,
}

/// <summary>The two section classes accepted between an explicitly counted normal pool and the
/// triangle pool. There is deliberately no unconstrained opaque-section kind.</summary>
public enum DcmKnownSectionKind
{
    ZeroFill,
    PreservedTemplateBytes,
}

/// <summary>One caller-proven byte range in a discontinuous forensic layout.</summary>
public sealed class DcmKnownSection
{
    private readonly byte[] _templateBytes;

    private DcmKnownSection(
        DcmKnownSectionKind kind, int offset, int length, byte[]? templateBytes)
    {
        Kind = kind;
        Offset = offset;
        Length = length;
        _templateBytes = templateBytes is null ? Array.Empty<byte>() : (byte[])templateBytes.Clone();
    }

    public DcmKnownSectionKind Kind { get; }
    public int Offset { get; }
    public int Length { get; }

    /// <summary>A defensive copy of the expected bytes for a preserved-template section.</summary>
    public byte[] TemplateBytes => (byte[])_templateBytes.Clone();

    public static DcmKnownSection ZeroFill(int offset, int length) =>
        new(DcmKnownSectionKind.ZeroFill, offset, length, null);

    public static DcmKnownSection PreservedTemplate(int offset, byte[] expectedBytes)
    {
        ArgumentNullException.ThrowIfNull(expectedBytes);
        return new DcmKnownSection(
            DcmKnownSectionKind.PreservedTemplateBytes,
            offset,
            expectedBytes.Length,
            expectedBytes);
    }

    internal ReadOnlySpan<byte> ExpectedTemplateBytes => _templateBytes;
}

/// <summary>
/// Explicit provenance required by <see cref="DcmModel.ReadForensicDc1"/>. The normal count is
/// caller-declared but must equal the independently derived position count. <see cref="Sections"/>
/// must form an exact ordered partition of the normal-to-triangle gap.
/// </summary>
public sealed class DcmForensicLayout
{
    private readonly DcmKnownSection[] _sections;

    public DcmForensicLayout(int normalCount, IEnumerable<DcmKnownSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        NormalCount = normalCount;
        _sections = sections.Select(section => section ??
            throw new ArgumentException("known sections cannot contain null", nameof(sections))).ToArray();
    }

    public int NormalCount { get; }
    public IReadOnlyList<DcmKnownSection> Sections => _sections;
}
