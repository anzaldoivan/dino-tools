using System.Buffers.Binary;
using IntelOrca.Biohazard;
using Xunit;
namespace DinoCrisis.Interchange.Tests;
public sealed class TimSnapshotTests
{
    [Theory]
    [InlineData(4, 8, 1)]
    [InlineData(8, 9, 1)]
    [InlineData(16, 2, 0)]
    public void Snapshot_preserves_all_six_metadata_properties(int bpp, int format, int palettes)
    {
        byte[] bytes = new TimFile(8, 2, bpp == 4 ? 8 : bpp).GetBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), format);
        int image = 8;
        if (bpp != 16)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 480);
            image += BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        }
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(image + 4), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(image + 6), 256);
        var tim = new TimFile(bytes);
        Assert.Equal(format, tim.PixelFormat);
        Assert.Equal(palettes, tim.PaletteCount);
        Assert.Equal(64, tim.ImageOriginX);
        Assert.Equal(256, tim.ImageOriginY);
        Assert.Equal(bpp == 16 ? 0 : 32, tim.PaletteOriginX);
        Assert.Equal(bpp == 16 ? 0 : 480, tim.PaletteOriginY);
        Assert.Equal(bytes, tim.GetBytes());
    }
}
