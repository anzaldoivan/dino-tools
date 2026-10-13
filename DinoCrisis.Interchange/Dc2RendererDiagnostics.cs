namespace DinoCrisis.Interchange;

internal readonly record struct Dc2TransformCoverage(
    int VertexCount,
    int TransformTriplets,
    int CoveredVertices);

internal static class Dc2RendererDiagnostics
{
    private const int Gt3Bytes = 64;
    private const int Gt4Bytes = 84;

    internal static Dc2TransformCoverage ValidateTransformCoverage(
        int vertexCount,
        ReadOnlySpan<byte> partTripletCounts)
    {
        if (vertexCount < 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (vertexCount % 3 != 0)
            throw new InvalidDataException(
                $"DC2 vertex count {vertexCount} is not a multiple of three required by part descriptor +7 transform triplets.");

        int transformTriplets = 0;
        foreach (byte count in partTripletCounts)
            transformTriplets = checked(transformTriplets + count);
        int coveredVertices = checked(transformTriplets * 3);

        if (coveredVertices < vertexCount)
            throw new InvalidDataException(
                $"DC2 part descriptor +7 values declare too few transform triplets: " +
                $"{transformTriplets} cover {coveredVertices} vertices, but the pool has {vertexCount}.");
        if (coveredVertices > vertexCount)
            throw new InvalidDataException(
                $"DC2 part descriptor +7 values declare too many transform triplets: " +
                $"{transformTriplets} cover {coveredVertices} vertices, but the pool has {vertexCount}.");

        return new(vertexCount, transformTriplets, coveredVertices);
    }

    internal static int CalculatePacketContribution(int triangles, int quads) =>
        checked(Gt3Bytes * triangles + Gt4Bytes * quads);

    internal static string FormatPacketContribution(int triangles, int quads) =>
        FormattableString.Invariant(
            $"advisory primary model packet contribution: {CalculatePacketContribution(triangles, quads):N0} B ({Gt3Bytes} B/triangle + {Gt4Bytes} B/quad); model contribution, not whole-frame headroom");
}
