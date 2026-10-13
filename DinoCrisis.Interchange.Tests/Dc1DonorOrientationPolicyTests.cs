using IntelOrca.Biohazard;
using DinoCrisis.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Dc1DonorOrientationPolicyTests
{
    [Fact]
    public void Identity_preserves_position_normal_and_has_positive_determinant()
    {
        var point = new Dc1Coordinate(3, 4, -5);
        var normal = new Dc1Coordinate(1, 2, 3);

        Assert.Equal(point, Dc1CoordinateTransform.Identity.ApplyDirection(point));
        Assert.Equal(normal, Dc1CoordinateTransform.Identity.ApplyDirection(normal));
        Assert.Equal(1, Dc1CoordinateTransform.Identity.Determinant, 12);
    }

    [Fact]
    public void Proper_180_degree_y_rotation_changes_xz_only_and_preserves_winding()
    {
        var pivot = new Dc1Coordinate(2, 7, 4);
        var point = new Dc1Coordinate(12, 7, -3);
        var normal = new Dc1Coordinate(1, 2, -3);

        Assert.Equal(new Dc1Coordinate(-8, 7, 11),
            Dc1CoordinateTransform.RotateY180.ApplyAround(point, pivot));
        Assert.Equal(new Dc1Coordinate(-1, 2, 3),
            Dc1CoordinateTransform.RotateY180.ApplyDirection(normal));
        Assert.Equal(1, Dc1CoordinateTransform.RotateY180.Determinant, 12);
    }

    [Fact]
    public void Re3_policy_applies_base_assembly_then_forward_correction_around_root()
    {
        var policy = Dc1DonorCoordinatePolicy.Resolve(BioVersion.Biohazard3);
        var root = new Dc1Coordinate(4, 10, 2);
        var point = new Dc1Coordinate(7, 13, 6);

        var assembledRoot = policy.DonorToAssembly.ApplyDirection(root);
        var assembledPoint = policy.DonorToAssembly.ApplyDirection(point);
        var expected = policy.ForwardCorrection.ApplyAround(assembledPoint, assembledRoot);

        Assert.Equal(expected, policy.ToTargetPosition(point, root));
        Assert.Equal(1, policy.DonorToAssembly.Determinant, 12);
        Assert.Equal(1, policy.ForwardCorrection.Determinant, 12);
        Assert.Equal(1, policy.CombinedRotation.Determinant, 12);
    }

    [Fact]
    public void Pivot_is_fixed_and_height_scale_floor_and_center_invariants_are_deterministic()
    {
        var policy = Dc1DonorCoordinatePolicy.Resolve(BioVersion.Biohazard3);
        var pivot = new Dc1Coordinate(3, 100, -8);
        var source = new[]
        {
            new Dc1Coordinate(-20, 0, -30),
            new Dc1Coordinate(15, 40, 18),
            new Dc1Coordinate(6, 80, -2),
        };
        var transformed = source.Select(p => policy.ToTargetPosition(p, pivot)).ToArray();
        var repeated = source.Select(p => policy.ToTargetPosition(p, pivot)).ToArray();

        Assert.Equal(transformed, repeated);
        Assert.Equal(source.Min(p => p.Y), transformed.Min(p => p.Y));
        Assert.Equal(source.Max(p => p.Y), transformed.Max(p => p.Y));
        Assert.Equal(source.Max(p => p.Y) - source.Min(p => p.Y),
            transformed.Max(p => p.Y) - transformed.Min(p => p.Y));
        var sourceHorizontalBounds = new[]
        {
            source.Max(p => p.X) - source.Min(p => p.X),
            source.Max(p => p.Z) - source.Min(p => p.Z),
        }.OrderBy(n => n).ToArray();
        var transformedHorizontalBounds = new[]
        {
            transformed.Max(p => p.X) - transformed.Min(p => p.X),
            transformed.Max(p => p.Z) - transformed.Min(p => p.Z),
        }.OrderBy(n => n).ToArray();
        Assert.Equal(sourceHorizontalBounds, transformedHorizontalBounds);
        Assert.Equal(policy.ToTargetPosition(pivot, pivot),
            policy.DonorToAssembly.ApplyDirection(pivot));
    }

    [Fact]
    public void Model_space_and_joint_relative_points_keep_the_same_seam_after_rotation()
    {
        var policy = Dc1DonorCoordinatePolicy.Resolve(BioVersion.Biohazard3);
        var pivot = new Dc1Coordinate(0, 0, 0);
        var modelSpace = new Dc1Coordinate(20, 12, -4);
        var jointOrigin = new Dc1Coordinate(20, 12, -4);
        var jointLocal = new Dc1Coordinate(3, 0, 5);
        var jointSpace = jointOrigin + jointLocal;

        var transformedModel = policy.ToTargetPosition(modelSpace, pivot);
        var transformedJoint = policy.ToTargetPosition(jointSpace, pivot);
        var transformedOrigin = policy.ToTargetPosition(jointOrigin, pivot);

        Assert.Equal(policy.CombinedRotation.ApplyDirection(jointLocal),
            transformedJoint - transformedOrigin);
        Assert.Equal(policy.CombinedRotation.ApplyDirection(modelSpace - jointOrigin),
            transformedModel - transformedOrigin);
    }

    [Fact]
    public void Orientation_policy_does_not_change_uv_material_or_primitive_order()
    {
        var policy = Dc1DonorCoordinatePolicy.Resolve(BioVersion.Biohazard3);
        var uvs = new[] { new Uv(1, 2), new Uv(127, 255) };
        var tpages = new ushort[] { 0x80, 0x81 };
        var order = new[] { "triangle-0", "quad-0", "triangle-1" };

        _ = policy.ToTargetPosition(new Dc1Coordinate(1, 2, 3), Dc1Coordinate.Zero);

        Assert.Equal(new[] { new Uv(1, 2), new Uv(127, 255) }, uvs);
        Assert.Equal(new ushort[] { 0x80, 0x81 }, tpages);
        Assert.Equal(new[] { "triangle-0", "quad-0", "triangle-1" }, order);
    }

    [Theory]
    [InlineData(BioVersion.Biohazard1)]
    [InlineData(BioVersion.Biohazard2)]
    public void Re1_and_re2_are_explicitly_refused_until_their_conventions_are_resolved(BioVersion version)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Dc1DonorCoordinatePolicy.Resolve(version));
        Assert.Contains("unresolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_donor_version_is_refused()
    {
        Assert.Throws<InvalidDataException>(() =>
            Dc1DonorCoordinatePolicy.Resolve((BioVersion)99));
    }
}
