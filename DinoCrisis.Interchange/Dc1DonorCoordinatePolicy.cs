using IntelOrca.Biohazard;

namespace DinoCrisis.Interchange;

/// <summary>A coordinate in the donor/assembled model space used by the private DC1 bridge.</summary>
public readonly record struct Dc1Coordinate(double X, double Y, double Z)
{
    public static Dc1Coordinate Zero => new(0, 0, 0);

    public static Dc1Coordinate operator +(Dc1Coordinate left, Dc1Coordinate right)
        => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Dc1Coordinate operator -(Dc1Coordinate left, Dc1Coordinate right)
        => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
}

/// <summary>
/// A proper linear coordinate transform.  The matrix is intentionally explicit so determinant and
/// winding preservation can be checked without relying on a graphics-library convention.
/// </summary>
public readonly record struct Dc1CoordinateTransform(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    public static Dc1CoordinateTransform Identity => new(
        1, 0, 0,
        0, 1, 0,
        0, 0, 1);

    /// <summary>The proper 180-degree rotation around the vertical Y axis.</summary>
    public static Dc1CoordinateTransform RotateY180 => new(
        -1, 0, 0,
        0, 1, 0,
        0, 0, -1);

    /// <summary>The established RE3 assembly conversion: (x,y,z) becomes (z,y,-x).</summary>
    public static Dc1CoordinateTransform Re3AssemblyPlus90Y => new(
        0, 0, 1,
        0, 1, 0,
        -1, 0, 0);

    public double Determinant =>
        M11 * (M22 * M33 - M23 * M32) -
        M12 * (M21 * M33 - M23 * M31) +
        M13 * (M21 * M32 - M22 * M31);

    public Dc1Coordinate ApplyDirection(Dc1Coordinate value) => new(
        M11 * value.X + M12 * value.Y + M13 * value.Z,
        M21 * value.X + M22 * value.Y + M23 * value.Z,
        M31 * value.X + M32 * value.Y + M33 * value.Z);

    public Dc1Coordinate ApplyAround(Dc1Coordinate point, Dc1Coordinate pivot)
        => pivot + ApplyDirection(point - pivot);

    /// <summary>Return the transform that applies this transform, then <paramref name="next"/>.</summary>
    public Dc1CoordinateTransform Then(Dc1CoordinateTransform next)
    {
        return new Dc1CoordinateTransform(
            next.M11 * M11 + next.M12 * M21 + next.M13 * M31,
            next.M11 * M12 + next.M12 * M22 + next.M13 * M32,
            next.M11 * M13 + next.M12 * M23 + next.M13 * M33,
            next.M21 * M11 + next.M22 * M21 + next.M23 * M31,
            next.M21 * M12 + next.M22 * M22 + next.M23 * M32,
            next.M21 * M13 + next.M22 * M23 + next.M23 * M33,
            next.M31 * M11 + next.M32 * M21 + next.M33 * M31,
            next.M31 * M12 + next.M32 * M22 + next.M33 * M32,
            next.M31 * M13 + next.M32 * M23 + next.M33 * M33);
    }
}

/// <summary>
/// Donor-version keyed coordinate policy for the private DC1 cross-rig bridge.  The RE3 policy is
/// the only one currently resolved by repository/runtime evidence; RE1 and RE2 are refused.
/// </summary>
public sealed class Dc1DonorCoordinatePolicy
{
    private Dc1DonorCoordinatePolicy(BioVersion donorVersion,
        Dc1CoordinateTransform donorToAssembly,
        Dc1CoordinateTransform forwardCorrection)
    {
        DonorVersion = donorVersion;
        DonorToAssembly = donorToAssembly;
        ForwardCorrection = forwardCorrection;
    }

    public BioVersion DonorVersion { get; }
    public Dc1CoordinateTransform DonorToAssembly { get; }
    public Dc1CoordinateTransform ForwardCorrection { get; }
    public Dc1CoordinateTransform CombinedRotation => DonorToAssembly.Then(ForwardCorrection);

    /// <summary>
    /// Resolve only a donor convention established by the current evidence.  The refusal is part of
    /// the contract: a filename or an unparsed model family cannot select an orientation policy.
    /// </summary>
    public static Dc1DonorCoordinatePolicy Resolve(BioVersion donorVersion)
    {
        if (donorVersion != BioVersion.Biohazard3)
            throw new InvalidDataException(
                $"DC1 donor coordinate convention is unresolved for {donorVersion}; RE1/RE2 are refused");

        return new Dc1DonorCoordinatePolicy(
            donorVersion,
            Dc1CoordinateTransform.Re3AssemblyPlus90Y,
            Dc1CoordinateTransform.RotateY180);
    }

    /// <summary>
    /// Transform a donor point after assembly.  The correction is applied around the assembled donor
    /// root, which is deterministic and common to model-space and joint-relative pieces.
    /// </summary>
    public Dc1Coordinate ToTargetPosition(Dc1Coordinate donorPosition, Dc1Coordinate donorPivot)
    {
        var assembled = DonorToAssembly.ApplyDirection(donorPosition);
        var assembledPivot = DonorToAssembly.ApplyDirection(donorPivot);
        return ForwardCorrection.ApplyAround(assembled, assembledPivot);
    }

    /// <summary>Transform a donor direction/normal with the same proper rotation, without a pivot.</summary>
    public Dc1Coordinate ToTargetNormal(Dc1Coordinate donorNormal)
        => CombinedRotation.ApplyDirection(donorNormal);
}
