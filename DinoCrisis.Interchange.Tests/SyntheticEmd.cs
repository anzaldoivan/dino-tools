using System.Buffers.Binary;
using System.IO;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Clean-room synthetic EMD donors (no copyrighted bytes) for the assembler tests. Builds the
/// "directory at tail" V2 layout EmdFile uses for RE2 (8 chunks) and RE3 (15 chunks): an 8-byte header
/// (directoryOffset, numOffsets), the chunk blobs, then numOffsets int32 offsets.</summary>
internal static class SyntheticEmd
{
    /// <summary>Build a V2 EMD with <paramref name="numChunks"/> placeholder chunks (4 bytes each,
    /// first byte = <paramref name="marker"/>, second = chunk index). RE2 EMD = 8, RE3 EMD = 15.</summary>
    public static byte[] BuildV2(int numChunks, byte marker = 0xAA)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0);          // directoryOffset placeholder
        bw.Write(numChunks);  // numOffsets

        var offsets = new int[numChunks];
        for (int i = 0; i < numChunks; i++)
        {
            offsets[i] = (int)ms.Position;
            bw.Write(new byte[] { marker, (byte)i, 0, 0 });
        }
        int dirOff = (int)ms.Position;
        foreach (var o in offsets) bw.Write(o);

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), dirOff);
        return bytes;
    }

    /// <summary>An RE2 EMD donor (8 chunks: Morph, Anim, Armature, Anim, Armature, Anim, Armature, Mesh).</summary>
    public static byte[] BuildRe2(byte marker = 0xAA) => BuildV2(8, marker);

    /// <summary>An RE3 EMD donor (15 chunks per EmdFile.g_chunkKinds3, mesh chunks at the tail).</summary>
    public static byte[] BuildRe3(byte marker = 0xBB) => BuildV2(15, marker);
}
