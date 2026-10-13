namespace DinoCrisis.Model;

/// <summary>A consecutive DC1 renderer transform run authored by one part descriptor.</summary>
public sealed record Dc1TransformRun(int PartIndex, int StartRecord, int TripletCount)
{
    /// <summary>Number of parallel position/normal records transformed by the run.</summary>
    public int RecordCount => TripletCount * 3;

    /// <summary>First record after the run.</summary>
    public int EndRecordExclusive => StartRecord + RecordCount;
}

/// <summary>The result of validating the independent DC1 transform-run runtime contract.</summary>
public sealed class Dc1TransformRunValidationResult
{
    internal Dc1TransformRunValidationResult(
        bool isLegal,
        string? error,
        int poolRecordCount,
        int coveredRecordCount,
        IReadOnlyList<Dc1TransformRun> runs)
    {
        IsLegal = isLegal;
        Error = error;
        PoolRecordCount = poolRecordCount;
        CoveredRecordCount = coveredRecordCount;
        Runs = runs;
    }

    /// <summary>True only when every transform-run rule passed.</summary>
    public bool IsLegal { get; }

    /// <summary>The first deterministic refusal reason, or null when legal.</summary>
    public string? Error { get; }

    /// <summary>Length of each parallel pool, or the position-pool length on a parallelism failure.</summary>
    public int PoolRecordCount { get; }

    /// <summary>Aggregate records covered by the authored part runs.</summary>
    public int CoveredRecordCount { get; }

    /// <summary>Consecutive per-part runs in descriptor order.</summary>
    public IReadOnlyList<Dc1TransformRun> Runs { get; }
}

/// <summary>
/// Validates the DC1 runtime transform-run contract independently of structural parsing, packet
/// budgets, and transformed-table capacity diagnostics. File part byte <c>+7</c>
/// (<see cref="DcmPart.Child"/>) is interpreted as an RTPT-triplet count only for DC1.
/// </summary>
public static class Dc1TransformRunValidator
{
    /// <summary>
    /// Maximum safe DC1 part count proven by the native builder's 33-entry local pointer array.
    /// </summary>
    public const int MaximumPartCount = 33;

    /// <summary>
    /// Validate transform coverage. <paramref name="declaredPaddingIndices"/> is optional authored
    /// metadata from a repacker; padding is never inferred from <see cref="SVector.Pad"/>.
    /// </summary>
    public static Dc1TransformRunValidationResult Validate(
        DcmModel model,
        IEnumerable<int>? declaredPaddingIndices = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var noRuns = Array.Empty<Dc1TransformRun>();
        if (model.Variant.Game != DcmGame.Dc1)
            return Invalid("validator requires a DC1 model", model.Vertices.Count, 0, noRuns);

        int partCount = model.Parts.Count;
        if (model.Header.PartCount != partCount)
        {
            return Invalid(
                $"header declares {model.Header.PartCount} parts but the parsed model contains {partCount}",
                model.Vertices.Count, 0, noRuns);
        }

        if (partCount is < 1 or > MaximumPartCount)
        {
            return Invalid(
                $"part count {partCount} is outside the supported range 1..{MaximumPartCount}",
                model.Vertices.Count, 0, noRuns);
        }

        int positionCount = model.Vertices.Count;
        int normalCount = model.Normals.Count;
        if (positionCount != normalCount)
        {
            return Invalid(
                $"position pool has {positionCount} records but normal pool has {normalCount}",
                positionCount, 0, noRuns);
        }

        var mutableRuns = new Dc1TransformRun[partCount];
        int covered = 0;
        for (int i = 0; i < partCount; i++)
        {
            int tripletCount = model.Parts[i].Child;
            var run = new Dc1TransformRun(i, covered, tripletCount);
            mutableRuns[i] = run;
            covered = checked(covered + run.RecordCount);
        }
        IReadOnlyList<Dc1TransformRun> runs = Array.AsReadOnly(mutableRuns);

        if (covered < positionCount)
        {
            return Invalid(
                $"under-coverage: part runs cover {covered} records but parallel pools contain {positionCount}",
                positionCount, covered, runs);
        }
        if (covered > positionCount)
        {
            return Invalid(
                $"over-coverage: part runs cover {covered} records but parallel pools contain {positionCount}",
                positionCount, covered, runs);
        }

        var padding = declaredPaddingIndices is null
            ? new SortedSet<int>()
            : new SortedSet<int>(declaredPaddingIndices);
        foreach (int index in padding)
        {
            if (index < 0 || index >= positionCount)
            {
                return Invalid(
                    $"declared padding record {index} is outside the pool range [0,{positionCount})",
                    positionCount, covered, runs);
            }
        }

        for (int i = 0; i < model.Triangles.Count; i++)
        {
            var triangle = model.Triangles[i];
            ushort[] indices = [triangle.V0, triangle.V1, triangle.V2];
            foreach (ushort index in indices)
            {
                var error = ValidateReference(index, "triangle", i, covered, padding);
                if (error is not null)
                    return Invalid(error, positionCount, covered, runs);
            }
        }

        for (int i = 0; i < model.Quads.Count; i++)
        {
            var quad = model.Quads[i];
            ushort[] indices = [quad.V0, quad.V1, quad.V2, quad.V3];
            foreach (ushort index in indices)
            {
                var error = ValidateReference(index, "quad", i, covered, padding);
                if (error is not null)
                    return Invalid(error, positionCount, covered, runs);
            }
        }

        return new Dc1TransformRunValidationResult(
            true, null, positionCount, covered, runs);
    }

    private static string? ValidateReference(
        int index,
        string primitiveKind,
        int primitiveIndex,
        int covered,
        IReadOnlySet<int> padding)
    {
        if (index < 0 || index >= covered)
        {
            return $"{primitiveKind} {primitiveIndex} references position/normal index {index} " +
                $"outside defined runs [0,{covered})";
        }
        if (padding.Contains(index))
            return $"declared padding record {index} is referenced by {primitiveKind} {primitiveIndex}";
        return null;
    }

    private static Dc1TransformRunValidationResult Invalid(
        string reason,
        int poolRecordCount,
        int coveredRecordCount,
        IReadOnlyList<Dc1TransformRun> runs) =>
        new(false, $"DC1 transform runs: {reason}", poolRecordCount, coveredRecordCount, runs);
}
