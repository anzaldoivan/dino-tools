using System;
using System.Buffers.Binary;
using System.IO;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

internal enum V1ContainerKind
{
    Emd,
    Emw,
}

/// <summary>Detects positively identified RE1/RE2/RE3 EMD donor containers.</summary>
public static class EmdDonor
{
    /// <summary>Detect the donor's RE version, or throw <see cref="InvalidDataException"/>
    /// if the bytes are not a recognized EMD.</summary>
    public static BioVersion Detect(byte[] donorEmd)
    {
        if (donorEmd is null) throw new ArgumentNullException(nameof(donorEmd));
        if (donorEmd.Length < 8)
            throw new InvalidDataException("Donor too small to be an EMD (< 8 bytes).");

        if (TryGetV2Frame(donorEmd, out int numOffsets))
        {
            return numOffsets switch
            {
                8 => BioVersion.Biohazard2,
                15 => BioVersion.Biohazard3,
                4 or 5 or 9 => throw new InvalidDataException(
                    $"Donor looks like a PLD/PLW player file ({numOffsets} chunks), not an EMD."),
                _ => throw new InvalidDataException(
                    $"Unsupported EMD directory size ({numOffsets} chunks)."),
            };
        }

        if (!TryDetectV1(donorEmd, out var kind))
            throw new InvalidDataException("Donor is not a positively identified V1 or V2 EMD.");
        if (kind == V1ContainerKind.Emw)
            throw new InvalidDataException("Donor is a V1 EMW weapon model, not an EMD.");
        return BioVersion.Biohazard1;
    }

    /// <summary>Try to identify exactly one measured V1 EMD/EMW content shape.</summary>
    internal static bool TryDetectV1(ReadOnlySpan<byte> bytes, out V1ContainerKind kind)
    {
        kind = default;
        if (TryGetV2Frame(bytes, out _)) return false;

        bool emd = TryValidateV1Emd(bytes);
        bool emw = TryValidateV1Emw(bytes);
        if (emd == emw) return false;
        kind = emd ? V1ContainerKind.Emd : V1ContainerKind.Emw;
        return true;
    }

    /// <summary>Non-throwing V2 EMD detection. PLD/PLW and malformed V2 inputs return false.</summary>
    public static bool TryDetectV2(byte[] donorEmd, out BioVersion version)
    {
        version = default;
        if (donorEmd is null || !TryGetV2Frame(donorEmd, out int numOffsets)) return false;
        switch (numOffsets)
        {
            case 8: version = BioVersion.Biohazard2; return true;
            case 15: version = BioVersion.Biohazard3; return true;
            default: return false;
        }
    }

    private static bool TryGetV2Frame(ReadOnlySpan<byte> bytes, out int numOffsets)
    {
        numOffsets = 0;
        if (bytes.Length < 8) return false;
        int directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
        if (directoryOffset < 8 || count <= 0 || count >= 64 ||
            (long)directoryOffset + (long)count * 4 != bytes.Length)
            return false;
        numOffsets = count;
        return true;
    }

    private static bool TryValidateV1Emd(ReadOnlySpan<byte> bytes)
    {
        if (!TryReadTail(bytes, 5, out int directory, out int[] offsets)) return false;

        if (offsets[0] == 0 && offsets[1] == 0 &&
            0 < offsets[2] && offsets[2] < offsets[3] && offsets[3] < offsets[4] && offsets[4] < directory)
        {
            return ValidateChunks(bytes,
                new[] { 0, offsets[2], offsets[3], offsets[4] },
                new[] { offsets[2], offsets[3], offsets[4], directory },
                hasSecondBank: false, hasTim: true);
        }

        if (0 < offsets[0] && offsets[0] < offsets[1] && offsets[1] < offsets[2] &&
            offsets[2] < offsets[3] && offsets[3] < offsets[4] && offsets[4] < directory)
        {
            return ValidateChunks(bytes,
                new[] { 0, offsets[0], offsets[1], offsets[2], offsets[3], offsets[4] },
                new[] { offsets[0], offsets[1], offsets[2], offsets[3], offsets[4], directory },
                hasSecondBank: true, hasTim: true);
        }

        return false;
    }

    private static bool TryValidateV1Emw(ReadOnlySpan<byte> bytes)
    {
        if (!TryReadTail(bytes, 2, out int directory, out int[] offsets) ||
            offsets[0] <= 0 || offsets[0] >= offsets[1] || offsets[1] >= directory)
            return false;

        return ValidateChunks(bytes,
            new[] { 0, offsets[0], offsets[1] },
            new[] { offsets[0], offsets[1], directory },
            hasSecondBank: false, hasTim: false);
    }

    private static bool TryReadTail(ReadOnlySpan<byte> bytes, int wordCount, out int directory, out int[] offsets)
    {
        directory = bytes.Length - wordCount * 4;
        offsets = new int[wordCount];
        if (directory <= 0) return false;
        for (int i = 0; i < wordCount; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(directory + i * 4, 4));
            if (offset < 0 || offset > directory || i != 0 && offset < offsets[i - 1]) return false;
            offsets[i] = offset;
        }
        return true;
    }

    private static bool ValidateChunks(
        ReadOnlySpan<byte> bytes,
        int[] starts,
        int[] ends,
        bool hasSecondBank,
        bool hasTim)
    {
        try
        {
            if (!ValidateEmr(bytes[starts[0]..ends[0]]) ||
                !ValidateEdd(bytes[starts[1]..ends[1]]))
                return false;

            int mesh = 2;
            if (hasSecondBank)
            {
                if (!ValidateEmr(bytes[starts[2]..ends[2]]) ||
                    !ValidateEdd(bytes[starts[3]..ends[3]]))
                    return false;
                mesh = 4;
            }

            return ValidateTmd(bytes[starts[mesh]..ends[mesh]]) &&
                (!hasTim || ValidateTim(bytes[starts[mesh + 1]..ends[mesh + 1]]));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool ValidateEmr(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        int armatureOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int keyFrameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        int partCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        int keyFrameSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        long positionsEnd = 8L + partCount * 6L;
        if (partCount <= 0 || keyFrameSize < 12 || positionsEnd > armatureOffset ||
            armatureOffset > keyFrameOffset || keyFrameOffset > bytes.Length ||
            (long)armatureOffset + partCount * 4L > keyFrameOffset)
            return false;

        for (int part = 0; part < partCount; part++)
        {
            int descriptor = armatureOffset + part * 4;
            int childCount = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(descriptor, 2));
            int childOffset = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(descriptor + 2, 2));
            long childStart = (long)armatureOffset + childOffset;
            long childEnd = childStart + childCount;
            if (childCount < 0 || childOffset < 0 ||
                childCount != 0 && childStart < (long)armatureOffset + partCount * 4L ||
                childEnd > keyFrameOffset)
                return false;
            for (long i = childStart; i < childEnd; i++)
                if (bytes[(int)i] >= partCount) return false;
        }

        var emr = new Emr(BioVersion.Biohazard1, bytes.ToArray());
        if (emr.ArmatureOffset != armatureOffset || emr.KeyFrameOffset != keyFrameOffset ||
            emr.NumParts != partCount || emr.KeyFrameSize != keyFrameSize)
            return false;
        for (int part = 0; part < partCount; part++)
        {
            _ = emr.GetRelativePosition(part);
            _ = emr.GetArmature(part);
            _ = emr.GetArmatureParts(part).Length;
        }
        _ = emr.KeyFrameData.Length;
        foreach (var frame in emr.KeyFrames)
        {
            _ = frame.Span.Length;
            _ = frame.Offset;
            _ = frame.Speed;
            _ = frame.Angles;
        }
        return true;
    }

    private static bool ValidateEdd(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;
        int firstFrameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        if (firstFrameOffset < 4 || firstFrameOffset % 4 != 0 || firstFrameOffset > bytes.Length)
            return false;
        int animationCount = firstFrameOffset / 4;
        for (int animation = 0; animation < animationCount; animation++)
        {
            int descriptor = animation * 4;
            int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(descriptor, 2));
            int frameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(descriptor + 2, 2));
            if (frameOffset < firstFrameOffset || (long)frameOffset + frameCount * 4L > bytes.Length)
                return false;
        }

        var edd = new Edd1(BioVersion.Biohazard1, bytes.ToArray());
        if (edd.Animations.Length != animationCount) return false;
        for (int animation = 0; animation < animationCount; animation++)
        {
            var frames = edd.GetFrames(animation);
            if (frames.Length != edd.Animations[animation].Count) return false;
            foreach (var frame in frames)
            {
                _ = frame.Index;
                _ = frame.Flags;
            }
        }
        return true;
    }

    private static bool ValidateTmd(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 40) return false;
        int wordZero = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int objectCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
        long descriptorEnd = 12L + objectCount * 28L;
        if (wordZero != 0x41 && wordZero != bytes.Length || objectCount <= 0 || descriptorEnd > bytes.Length)
            return false;

        for (int part = 0; part < objectCount; part++)
        {
            int descriptor = 12 + part * 28;
            if (!ValidateTmdSpan(bytes, descriptorEnd,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor + 4, 4)), 8) ||
                !ValidateTmdSpan(bytes, descriptorEnd,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor + 8, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor + 12, 4)), 8) ||
                !ValidateTmdSpan(bytes, descriptorEnd,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor + 16, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(descriptor + 20, 4)), 28))
                return false;
        }

        var tmd = new Tmd(bytes.ToArray());
        if (tmd.Length != wordZero || tmd.NumObjects != objectCount || tmd.Objects.Length != objectCount)
            return false;
        for (int part = 0; part < objectCount; part++)
        {
            var obj = tmd.Objects[part];
            var positions = tmd.GetPositionData(obj);
            var normals = tmd.GetNormalData(obj);
            foreach (var triangle in tmd.GetTriangles(obj))
            {
                if (triangle.v0 >= positions.Length || triangle.v1 >= positions.Length || triangle.v2 >= positions.Length ||
                    triangle.n0 >= normals.Length || triangle.n1 >= normals.Length || triangle.n2 >= normals.Length)
                    return false;
            }
        }
        return true;
    }

    private static bool ValidateTmdSpan(ReadOnlySpan<byte> bytes, long descriptorEnd, int offset, int count, int stride)
    {
        long start = 12L + offset;
        long end = start + (long)count * stride;
        return offset >= 0 && count >= 0 && start >= descriptorEnd && end >= start && end <= bytes.Length;
    }

    private static bool ValidateTim(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 16) return false;
        int format = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
        if (format is not (8 or 9 or 2)) return false;

        int imageHeader = 8;
        int paletteRows = 0;
        if (format != 2)
        {
            uint clutSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
            int colours = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(16, 2));
            paletteRows = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(18, 2));
            if (paletteRows <= 0 || colours != (format == 8 ? 16 : 256) ||
                clutSize != 12L + (long)colours * paletteRows * 2 || 8L + clutSize + 12 > bytes.Length)
                return false;
            imageHeader = checked(8 + (int)clutSize);
        }

        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(imageHeader, 4));
        int widthWords = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(imageHeader + 8, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(imageHeader + 10, 2));
        long expectedImageSize = 12L + (long)widthWords * height * 2;
        if (widthWords <= 0 || height <= 0 || imageSize != expectedImageSize ||
            (long)imageHeader + imageSize != bytes.Length)
            return false;

        var tim = new TimFile(bytes.ToArray());
        int width = checked(widthWords * (format == 8 ? 4 : format == 9 ? 2 : 1));
        if (tim.PixelFormat != format || tim.Width != width || tim.Height != height ||
            tim.PaletteCount != paletteRows)
            return false;
        if (format != 2) _ = tim.GetPalette(0).Length;
        _ = tim.GetRawPixel(width - 1, height - 1);
        return true;
    }
}
