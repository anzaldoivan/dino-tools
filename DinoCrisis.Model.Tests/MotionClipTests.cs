using DinoCrisis.Model;
using DinoCrisis.Model.Locating;
using Xunit;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// The DC1 motion-clip container and its 12-bit rotation frames (generic across rigs, not specific to
/// the Therizinosaurus the format was first read on).
/// </summary>
public sealed class MotionClipTests
{
    private const uint Base = 0x80100000;

    [Theory]
    [InlineData(7, 44)]
    [InlineData(10, 60)]
    [InlineData(15, 80)]
    [InlineData(18, 96)]
    [InlineData(20, 104)]
    [InlineData(21, 108)]
    [InlineData(22, 112)]
    public void FrameSize_IsTwelveHeaderBytesPlusThirtySixBitsPerBone_RoundedToFour(int bones, int expected)
        => Assert.Equal(expected, DcmMotion.FrameSize(bones));

    [Fact]
    public void UnpackRotations_ReadsTwelveBitValuesLittleEndianFromByteTwelve()
    {
        // two bones = six 12-bit values = nine bytes after the 12-byte header
        short[] values = { 0x001, 0xFFF, 0x800, 0x123, 0xABC, 0x000 };
        var frame = new byte[DcmMotion.FrameSize(2)];
        int bit = 0;
        foreach (short v in values)
        {
            for (int k = 0; k < 12; k++, bit++)
                if ((v >> k & 1) != 0) frame[12 + bit / 8] |= (byte)(1 << (bit % 8));
        }

        Assert.Equal(values, DcmMotion.UnpackRotations(frame, 2));
    }

    [Fact]
    public void ReadHeader_ReturnsTheSixWordsBeforeTheRotations()
    {
        var frame = new byte[DcmMotion.FrameSize(1)];
        for (int i = 0; i < 6; i++) { short v = (short)(-100 * (i + 1)); frame[i * 2] = (byte)v; frame[i * 2 + 1] = (byte)(v >> 8); }
        Assert.Equal(new short[] { -100, -200, -300, -400, -500, -600 }, DcmMotion.ReadHeader(frame));
    }

    [Fact]
    public void ScanClips_FindsAHeadWhosePointerNamesItsOwnFrameData()
    {
        // [u32 ptr][u32 count][(count + 1) index words] then the frames; ptr = address of the frames.
        const int at = 0x20, count = 2;
        var buf = new byte[0x200];
        int data = at + 8 + (count + 1) * 4;
        BitConverter.GetBytes(Base + (uint)data).CopyTo(buf, at);
        BitConverter.GetBytes((uint)count).CopyTo(buf, at + 4);

        var clip = Assert.Single(DcmMotion.ScanClips(buf, Base));
        Assert.Equal(new DcmClip(at, count, data), clip);
    }

    [Fact]
    public void ScanClips_RejectsAPointerThatDoesNotMatchTheHeadLayout()
    {
        var buf = new byte[0x100];
        BitConverter.GetBytes(Base + 0x40u).CopyTo(buf, 0x20);   // would need count such that 0x20+8+(n+1)*4 == 0x40
        BitConverter.GetBytes(2u).CopyTo(buf, 0x24);              // n = 2 gives 0x34, not 0x40
        Assert.Empty(DcmMotion.ScanClips(buf, Base));
    }

    [Dc1Fact("st603.dat")]
    public void Cli_Clips_ListsTheClipsOfAPackage()
    {
        var output = new StringWriter();
        int rc = DinoCrisis.Model.Cli.DcmCli.Run(
            new[] { "clips", TestData.Pristine(FixtureGate.Dc1File("st603.dat")) }, output);

        Assert.Equal(0, rc);
        Assert.Contains("49 clip(s)", output.ToString());
        Assert.Contains("0x0F970  count ", output.ToString());
    }

    /// <summary>Therizinosaurus room: the scan reproduces exactly the 49 clips its motion table lists,
    /// and each clip holds stored count + 1 frames — 112-byte frames (22 bones) for the first 35 clips,
    /// 80-byte frames for the rest.</summary>
    [Dc1Fact("st603.dat")]
    public void Dc1_Therizinosaurus_ScanMatchesTheMotionTable()
    {
        const int motionTable = 0xF8AC, clipCount = 49;
        var theri = DcmLocator.FromDc1Package(File.ReadAllBytes(TestData.Pristine(FixtureGate.Dc1File("st603.dat"))))
            .First(m => m.Header.PartCount == 22);
        byte[] rdt = theri.Raw;

        var listed = Enumerable.Range(0, clipCount)
            .Select(i => (int)(BitConverter.ToUInt32(rdt, motionTable + i * 4) - theri.LoadBase)).ToList();
        var clips = DcmMotion.ScanClips(rdt, theri.LoadBase);

        Assert.Equal(listed.OrderBy(o => o), clips.Select(c => c.Offset).OrderBy(o => o));

        for (int i = 0; i + 1 < clips.Count; i++)
        {
            int frame = i < 35 ? DcmMotion.FrameSize(22) : DcmMotion.FrameSize(15);
            Assert.Equal((clips[i].StoredCount + 1) * frame, clips[i + 1].Offset - clips[i].DataOffset);
        }

        // decoded rotations are 12-bit and move smoothly from frame to frame
        var first = DcmMotion.UnpackRotations(rdt.AsSpan(clips[1].DataOffset, 112), 22);
        var second = DcmMotion.UnpackRotations(rdt.AsSpan(clips[1].DataOffset + 112, 112), 22);
        Assert.All(first, v => Assert.InRange(v, 0, 4095));
        double meanStep = first.Zip(second, (a, b) => { int d = Math.Abs(a - b); return Math.Min(d, 4096 - d); }).Average();
        Assert.True(meanStep < 60, $"mean per-frame rotation step {meanStep:F1} is not smooth");
    }
}
