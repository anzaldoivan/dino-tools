using DinoCrisis.Model;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Dc1AndDc2VariantsHaveTheValidatedLayoutConstants()
    {
        // The §7 facts, pinned as the project's first guard.
        Assert.Equal(0x18, DcmVariant.Dc1.HeaderSize);
        Assert.Equal(16, DcmVariant.Dc1.TriangleStride);
        Assert.Equal(20, DcmVariant.Dc1.QuadStride);
        Assert.True(DcmVariant.Dc1.PerPolyTexture);

        Assert.Equal(0x1c, DcmVariant.Dc2.HeaderSize);
        Assert.Equal(12, DcmVariant.Dc2.TriangleStride);
        Assert.Equal(16, DcmVariant.Dc2.QuadStride);
        Assert.False(DcmVariant.Dc2.PerPolyTexture);
    }
}
