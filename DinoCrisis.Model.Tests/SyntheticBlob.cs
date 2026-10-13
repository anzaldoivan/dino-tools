using System.Buffers.Binary;
using DinoCrisis.Model;

namespace DinoCrisis.Model.Tests;

/// <summary>
/// Builds a minimal but fully valid DCM blob from scratch (2 parts, 2 tris, 1 quad). The DC2
/// variant has 6 vertices/normals so its two one-triplet part descriptors cover the pool exactly;
/// DC1 retains the original 4-record fixture, so reader / round-trip / OBJ tests run with no
/// copyrighted data. Layout matches the reader:
/// header → parts → vertices → normals → triangles → quads, contiguous, no trailing.
/// </summary>
internal static class SyntheticBlob
{
    public const uint Base = 0x80000000;

    public static byte[] Build(DcmVariant v)
    {
        const int nParts = 2, nTris = 2, nQuads = 1;
        int nVerts = v.Game == DcmGame.Dc2 ? 6 : 4;
        int nNorms = nVerts;
        int partsOff = v.HeaderSize;
        int vertsOff = partsOff + nParts * DcmVariant.PartStride;
        int normsOff = vertsOff + nVerts * DcmVariant.VertexStride;
        int trisOff = normsOff + nNorms * DcmVariant.VertexStride;
        int quadsOff = trisOff + nTris * v.TriangleStride;
        int end = quadsOff + nQuads * v.QuadStride;

        var b = new byte[end];
        void W32(int o, uint val) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), val);
        void W16(int o, ushort val) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), val);
        void WI16(int o, short val) => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o, 2), val);
        uint Va(int off) => Base + (uint)off;

        // header
        W32(0, Va(vertsOff));
        W32(4, Va(normsOff));
        W32(8, Va(trisOff));     // == part0 triPtr
        W32(0xc, Va(quadsOff));   // == part0 quadPtr
        W16(0x10, nTris);
        W16(0x12, nQuads);
        W32(0x14, nParts);
        if (v.Game == DcmGame.Dc2) { W16(0x18, 158); W16(0x1a, 32704); }

        // part0 owns tri0 + quad0, part1 owns tri1
        WI16(partsOff + 0, 0); WI16(partsOff + 2, -100); WI16(partsOff + 4, 0);
        b[partsOff + 6] = unchecked((byte)-1);
        b[partsOff + 7] = v.Game == DcmGame.Dc2 ? (byte)1 : (byte)3;
        W32(partsOff + 8, Va(trisOff)); W32(partsOff + 0xc, Va(quadsOff));
        W16(partsOff + 0x10, 1); W16(partsOff + 0x12, 1);

        int p1 = partsOff + DcmVariant.PartStride;
        WI16(p1 + 0, 10); WI16(p1 + 2, 20); WI16(p1 + 4, 30);
        b[p1 + 6] = 0;
        b[p1 + 7] = v.Game == DcmGame.Dc2 ? (byte)1 : (byte)7;
        W32(p1 + 8, Va(trisOff + v.TriangleStride)); W32(p1 + 0xc, Va(quadsOff + v.QuadStride));
        W16(p1 + 0x10, 1); W16(p1 + 0x12, 0);

        // vertices + normals. DC1 uses the vertex pad as its rigid part binding; keep the shared
        // fixture realistic so part 1's triangle cross-references a vertex bound to part 0.
        for (int i = 0; i < nVerts; i++)
        {
            int o = vertsOff + i * 8;
            WI16(o + 0, (short)(i * 7)); WI16(o + 2, (short)(i * 11)); WI16(o + 4, (short)(i * 13)); WI16(o + 6, (short)(i / 2));
        }
        for (int i = 0; i < nNorms; i++)
        {
            int o = normsOff + i * 8;
            WI16(o + 0, (short)(-i)); WI16(o + 2, (short)(i * 2)); WI16(o + 4, (short)(i * 3)); WI16(o + 6, 0);
        }

        WriteTri(b, trisOff, v, 0, 1, 2);
        WriteTri(b, trisOff + v.TriangleStride, v, 1, 2, 3);
        WriteQuad(b, quadsOff, v, 0, 1, 2, 3);
        return b;

        static void WriteTri(byte[] b, int o, DcmVariant v, ushort a, ushort c, ushort d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), a);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 2, 2), c);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4, 2), d);
            for (int k = 6; k < v.TriangleStride; k++) b[o + k] = (byte)(0x40 + k);
        }
        static void WriteQuad(byte[] b, int o, DcmVariant v, ushort a, ushort c, ushort d, ushort e)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), a);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 2, 2), c);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4, 2), d);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 6, 2), e);
            for (int k = 8; k < v.QuadStride; k++) b[o + k] = (byte)(0x50 + k);
        }
    }
}
