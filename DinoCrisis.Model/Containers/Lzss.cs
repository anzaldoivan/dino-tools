namespace DinoCrisis.Model.Containers;

/// <summary>
/// LZSS de/compression used by Dino Crisis package files. Clean-room implementation of the
/// documented algorithm (a flag byte consumed LSB-first, eight tokens per flag; a set bit
/// copies one literal, a clear bit reads <c>ch</c> then <c>t</c> and copies
/// <c>length = (t&gt;&gt;4)+2</c> bytes from <c>distance = ((t&amp;0x0F)&lt;&lt;8)|ch</c> behind the
/// output). Distances 1..4095, lengths 2..17.
///
/// <para>Ported from DinoRand's own MIT codec (our code) so this library carries no external
/// dependency; <c>Decompress(Compress(x)) == x</c>.</para>
/// </summary>
public static class Lzss
{
    /// <summary>12-bit back-reference window.</summary>
    public const int WindowSize = 4095;

    /// <summary>Smallest encodable match.</summary>
    public const int MinMatch = 2;

    /// <summary>Largest encodable match.</summary>
    public const int MaxMatch = MinMatch + 0x0F;

    /// <summary>Decompress an LZSS stream.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length * 4);
        int pos = 0;
        int flag = 1; // 0x100 sentinel re-armed after eight shifts
        while (pos < input.Length)
        {
            if (flag == 1)
            {
                if (pos >= input.Length) break;
                flag = input[pos++] | 0x100;
            }

            if (pos >= input.Length) break;
            int ch = input[pos++];

            if ((flag & 1) != 0)
            {
                output.Add((byte)ch);
            }
            else
            {
                if (pos >= input.Length) break;
                int t = input[pos++];
                int distance = ((t & 0x0F) << 8) | ch;
                int length = (t >> 4) + 2;

                int start = output.Count - distance;
                if (start < 0) throw new InvalidDataException("LZSS back-reference before start of stream.");
                for (int k = 0; k < length; k++)
                    output.Add(output[start + k]);
            }

            flag >>= 1;
        }
        return output.ToArray();
    }

    /// <summary>Compress raw bytes (greedy longest-match); round-trips with <see cref="Decompress"/>.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        int pos = 0;
        while (pos < input.Length)
        {
            int flagIndex = output.Count;
            output.Add(0);
            int flags = 0;
            for (int bit = 0; bit < 8 && pos < input.Length; bit++)
            {
                (int matchDistance, int matchLength) = FindLongestMatch(input, pos);
                if (matchLength >= MinMatch)
                {
                    int lenCode = matchLength - MinMatch;
                    int ch = matchDistance & 0xFF;
                    int t = ((matchDistance >> 8) & 0x0F) | (lenCode << 4);
                    output.Add((byte)ch);
                    output.Add((byte)t);
                    pos += matchLength;
                }
                else
                {
                    flags |= 1 << bit;
                    output.Add(input[pos++]);
                }
            }
            output[flagIndex] = (byte)flags;
        }
        return output.ToArray();
    }

    private static (int distance, int length) FindLongestMatch(ReadOnlySpan<byte> input, int pos)
    {
        int bestLength = 0, bestDistance = 0;
        int windowStart = Math.Max(0, pos - WindowSize);
        for (int start = pos - 1; start >= windowStart; start--)
        {
            int length = 0;
            while (length < MaxMatch && pos + length < input.Length && input[start + length] == input[pos + length])
                length++;
            if (length > bestLength)
            {
                bestLength = length;
                bestDistance = pos - start;
                if (length == MaxMatch) break;
            }
        }
        return (bestDistance, bestLength);
    }
}
