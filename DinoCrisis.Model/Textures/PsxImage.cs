namespace DinoCrisis.Model.Textures;

/// <summary>A decoded RGBA image (8 bits/channel), plus a minimal dependency-free TGA writer so the
/// library stays self-contained (no System.Drawing / ImageSharp). Blender and Noesis read TGA.</summary>
public sealed class PsxImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGBA, 4 bytes per pixel.</summary>
    public byte[] Pixels { get; }

    public PsxImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public void Set(int x, int y, byte r, byte g, byte b, byte a)
    {
        int o = (y * Width + x) * 4;
        Pixels[o + 0] = r; Pixels[o + 1] = g; Pixels[o + 2] = b; Pixels[o + 3] = a;
    }

    /// <summary>Convert a PSX 16-bit color (BGR555 + bit15 STP) to RGBA. A raw value of 0 is fully
    /// transparent (PSX convention); 5-bit channels are expanded to 8 bits.</summary>
    public static (byte R, byte G, byte B, byte A) FromPsv(ushort c)
    {
        if (c == 0) return (0, 0, 0, 0);
        int r5 = c & 0x1F, g5 = (c >> 5) & 0x1F, b5 = (c >> 10) & 0x1F;
        byte Expand(int v5) => (byte)((v5 << 3) | (v5 >> 2));
        return (Expand(r5), Expand(g5), Expand(b5), 255);
    }

    /// <summary>Write an uncompressed 32-bit TGA (BGRA, bottom-up) — the simplest universally-read format.</summary>
    public void WriteTga(string path)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        Span<byte> header = stackalloc byte[18];
        header[2] = 2;                       // uncompressed true-color
        header[12] = (byte)(Width & 0xFF);
        header[13] = (byte)(Width >> 8);
        header[14] = (byte)(Height & 0xFF);
        header[15] = (byte)(Height >> 8);
        header[16] = 32;                     // bits per pixel
        header[17] = 0x28;                   // top-left origin (bit5) + 8-bit alpha (bits0-3)
        w.Write(header);

        var row = new byte[Width * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int s = (y * Width + x) * 4;
                int d = x * 4;
                row[d + 0] = Pixels[s + 2]; // B
                row[d + 1] = Pixels[s + 1]; // G
                row[d + 2] = Pixels[s + 0]; // R
                row[d + 3] = Pixels[s + 3]; // A
            }
            w.Write(row);
        }
    }

    /// <summary>True when any pixel is opaque (a sanity check that decoding produced real content).</summary>
    public bool HasOpaquePixels()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 0) return true;
        return false;
    }
}
