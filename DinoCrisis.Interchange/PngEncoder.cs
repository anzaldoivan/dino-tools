using System.Buffers.Binary;
using System.IO.Compression;
using DinoCrisis.Model.Textures;

namespace DinoCrisis.Interchange;

/// <summary>
/// Minimal RGBA8 PNG writer for embedding decoded DC skins in glTF (glTF only allows PNG/JPEG
/// images). Kept dependency-free: zlib framing over <see cref="DeflateStream"/> + our own
/// Adler-32/CRC-32, filter 0 scanlines — everything a spec-compliant baseline PNG needs and
/// nothing more. Not a general imaging library.
/// </summary>
public static class PngEncoder
{
    /// <summary>Encode a decoded PSX skin (RGBA pixel buffer) as a PNG byte stream.</summary>
    public static byte[] Encode(PsxImage image) => Encode(image.Pixels, image.Width, image.Height);

    /// <summary>Encode a raw RGBA8 buffer (row-major, 4 bytes/pixel) as a PNG byte stream.</summary>
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba.Length < width * height * 4) throw new ArgumentException("buffer too small", nameof(rgba));

        // PNG scanlines: one filter byte (0 = None) before each row of raw RGBA.
        int stride = width * 4;
        var raw = new byte[height * (1 + stride)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (1 + stride);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, y * stride, raw, dst + 1, stride);
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT payload is a zlib stream: 2-byte header + raw deflate + big-endian Adler-32.
        using (var z = new MemoryStream())
        {
            z.WriteByte(0x78);
            z.WriteByte(0x9C);
            using (var d = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true))
                d.Write(raw);
            Span<byte> adler = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
            z.Write(adler);
            WriteChunk(ms, "IDAT", z.ToArray());
        }

        WriteChunk(ms, "IEND", Span<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body.AsSpan(4));
        s.Write(body);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        s.Write(crc);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (byte t in data)
        {
            a = (a + t) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte t in data)
            c = CrcTable[(c ^ t) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
