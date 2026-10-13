namespace DinoCrisis.Model;

/// <summary>One motion clip: where its head is, the count stored in the head, and where its frames start.</summary>
public readonly record struct DcmClip(int Offset, int StoredCount, int DataOffset);

/// <summary>
/// DC1 motion data (dinorand STATIC-SCD-RE / THERI-MOTION-ANIM-SPEC). The format is the same for
/// every rig; only the frame size depends on the bone count.
///
/// <para><b>Clip head.</b> <c>u32 pointer; u32 count; (count + 1) index words</c>, then the frames.
/// The pointer is the address of the first frame, i.e. <c>head + 8 + (count + 1) * 4</c> — that
/// self-reference is what <see cref="ScanClips"/> looks for.</para>
///
/// <para><b>Frame.</b> A 12-byte header of six s16 words, then three 12-bit values per bone packed
/// little-endian bit by bit; 4096 is a full turn.</para>
///
/// <para><b>Not established</b> — do not build on these without a live check: the order in which a
/// bone's three values apply as rotations; what the six header words are (they look like root
/// position and accumulated travel); and the frame count, which is <c>count + 1</c> for most clips
/// (all 49 of the Therizinosaurus) but plain <c>count</c> for a minority.</para>
/// </summary>
public static class DcmMotion
{
    /// <summary>Bytes before the packed rotations in every frame.</summary>
    public const int HeaderSize = 12;

    private const int MaxStoredCount = 500;

    /// <summary>Frame size for a rig of <paramref name="bones"/> bones: the header plus 36 bits per
    /// bone, rounded up to a multiple of four.</summary>
    public static int FrameSize(int bones) => (HeaderSize + (bones * 36 + 7) / 8 + 3) & ~3;

    /// <summary>The six s16 header words of a frame, as stored.</summary>
    public static short[] ReadHeader(ReadOnlySpan<byte> frame)
    {
        var words = new short[HeaderSize / 2];
        for (int i = 0; i < words.Length; i++)
            words[i] = (short)(frame[i * 2] | (frame[i * 2 + 1] << 8));
        return words;
    }

    /// <summary>The <c>bones * 3</c> raw 12-bit values of a frame (0..4095), three per bone in stored order.</summary>
    public static short[] UnpackRotations(ReadOnlySpan<byte> frame, int bones)
    {
        var values = new short[bones * 3];
        for (int i = 0; i < values.Length; i++)
        {
            int bit = i * 12;
            int at = HeaderSize + bit / 8;
            int word = frame[at] | (frame[at + 1] << 8);
            values[i] = (short)((word >> (bit % 8)) & 0xFFF);
        }
        return values;
    }

    /// <summary>Every clip head in <paramref name="buffer"/> (a decompressed room or model file loaded
    /// at <paramref name="loadBase"/>), in file order.</summary>
    public static IReadOnlyList<DcmClip> ScanClips(ReadOnlySpan<byte> buffer, uint loadBase)
    {
        var clips = new List<DcmClip>();
        for (int at = 0; at + 12 <= buffer.Length; at += 4)
        {
            uint count = BitConverter.ToUInt32(buffer.Slice(at + 4, 4));
            if (count < 1 || count > MaxStoredCount) continue;

            long data = at + 8 + ((long)count + 1) * 4;
            if (data > buffer.Length) continue;
            if (BitConverter.ToUInt32(buffer.Slice(at, 4)) != loadBase + (uint)data) continue;

            clips.Add(new DcmClip(at, (int)count, (int)data));
        }
        return clips;
    }
}
