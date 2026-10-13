using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

/// <summary>Increment 3: donor version detection / validation.</summary>
public class EmdDonorTests
{
    [Fact]
    public void Detect_recognizes_re2_eight_chunk_emd()
        => Assert.Equal(BioVersion.Biohazard2, EmdDonor.Detect(SyntheticEmd.BuildRe2()));

    [Fact]
    public void Detect_recognizes_re3_fifteen_chunk_emd()
        => Assert.Equal(BioVersion.Biohazard3, EmdDonor.Detect(SyntheticEmd.BuildRe3()));

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void Detect_rejects_v2_player_file_shapes(int chunkCount)
        => Assert.Throws<InvalidDataException>(() => EmdDonor.Detect(SyntheticEmd.BuildV2(chunkCount)));

    [Fact]
    public void Detect_rejects_unsupported_v2_directory_count()
        => Assert.Throws<InvalidDataException>(() => EmdDonor.Detect(SyntheticEmd.BuildV2(7)));

    [Fact]
    public void Detect_rejects_too_small()
        => Assert.Throws<InvalidDataException>(() => EmdDonor.Detect(new byte[] { 1, 2, 3 }));

    [Fact]
    public void Detect_rejects_arbitrary_non_v2_bytes()
    {
        var bytes = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 0x12345);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 3);
        Assert.Throws<InvalidDataException>(() => EmdDonor.Detect(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryDetectV1_accepts_emd4_with_both_tmd_word_zero_encodings(bool standardTmd)
        => AssertV1Kind(BuildEmd(sixChunks: false, standardTmd), V1ContainerKind.Emd);

    [Fact]
    public void TryDetectV1_accepts_emd6()
        => AssertV1Kind(BuildEmd(sixChunks: true), V1ContainerKind.Emd);

    [Fact]
    public void Detect_accepts_only_positively_identified_v1_emd()
        => Assert.Equal(BioVersion.Biohazard1, EmdDonor.Detect(BuildEmd(sixChunks: false)));

    [Fact]
    public void TryDetectV1_accepts_emw3_but_Detect_refuses_it()
    {
        var emw = BuildEmw();
        AssertV1Kind(emw, V1ContainerKind.Emw);
        Assert.Throws<InvalidDataException>(() => EmdDonor.Detect(emw));
    }

    [Fact]
    public void TryDetectV1_gives_v2_closure_precedence()
        => Assert.False(EmdDonor.TryDetectV1(SyntheticEmd.BuildRe2(), out _));

    [Theory]
    [InlineData(TailOffsetFault.Negative)]
    [InlineData(TailOffsetFault.Decreasing)]
    [InlineData(TailOffsetFault.DirectoryCrossing)]
    [InlineData(TailOffsetFault.OutOfRange)]
    public void TryDetectV1_rejects_invalid_tail_offsets(TailOffsetFault fault)
    {
        var bytes = BuildEmd(sixChunks: false);
        int directory = bytes.Length - 20;
        switch (fault)
        {
            case TailOffsetFault.Negative:
                WriteInt32(bytes, directory + 8, -1);
                break;
            case TailOffsetFault.Decreasing:
                WriteInt32(bytes, directory + 12, ReadInt32(bytes, directory + 8) - 1);
                break;
            case TailOffsetFault.DirectoryCrossing:
                WriteInt32(bytes, directory + 16, directory + 1);
                break;
            case TailOffsetFault.OutOfRange:
                WriteInt32(bytes, directory + 16, bytes.Length + 100);
                break;
        }
        Assert.False(EmdDonor.TryDetectV1(bytes, out _));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void TryDetectV1_rejects_wrong_nonempty_emd_chunk_count(int chunkCount)
    {
        byte[][] chunks = chunkCount == 3
            ? new[] { BuildEmr(), BuildEdd(), BuildTmd() }
            : new[] { BuildEmr(), BuildEdd(), BuildTmd(), BuildTim(), BuildTim() };
        Assert.False(EmdDonor.TryDetectV1(BuildV1(chunks, 5), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_empty_chunk()
        => Assert.False(EmdDonor.TryDetectV1(BuildV1(
            new[] { BuildEmr(), Array.Empty<byte>(), BuildTmd(), BuildTim() }, 5), out _));

    [Fact]
    public void TryDetectV1_rejects_invalid_emr_part_range()
    {
        var emr = BuildEmr();
        BinaryPrimitives.WriteUInt16LittleEndian(emr.AsSpan(4, 2), ushort.MaxValue);
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(emr: emr), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_invalid_emr_child_range()
    {
        var emr = BuildEmr(2);
        int armature = BinaryPrimitives.ReadUInt16LittleEndian(emr.AsSpan(0, 2));
        int childOffset = BinaryPrimitives.ReadUInt16LittleEndian(emr.AsSpan(armature + 2, 2));
        emr[armature + childOffset] = 2;
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(emr: emr), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_invalid_edd_animation_range()
    {
        var edd = BuildEdd();
        BinaryPrimitives.WriteUInt16LittleEndian(edd.AsSpan(2, 2), 6);
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(edd: edd), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_invalid_edd_frame_range()
    {
        var edd = BuildEdd();
        BinaryPrimitives.WriteUInt16LittleEndian(edd.AsSpan(0, 2), ushort.MaxValue);
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(edd: edd), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_invalid_tmd_span()
    {
        var tmd = BuildTmd();
        BinaryPrimitives.WriteInt32LittleEndian(tmd.AsSpan(12, 4), int.MaxValue);
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(tmd: tmd), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_invalid_tmd_primitive_index()
    {
        var tmd = BuildTmd();
        int primitive = 12 + BinaryPrimitives.ReadInt32LittleEndian(tmd.AsSpan(28, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(tmd.AsSpan(primitive + 18, 2), 1);
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(tmd: tmd), out _));
    }

    [Theory]
    [InlineData(TimFault.Magic)]
    [InlineData(TimFault.Depth)]
    [InlineData(TimFault.ClutBounds)]
    [InlineData(TimFault.ImageBounds)]
    public void TryDetectV1_rejects_invalid_tim(TimFault fault)
    {
        var tim = BuildTim();
        switch (fault)
        {
            case TimFault.Magic:
                WriteInt32(tim, 0, 0);
                break;
            case TimFault.Depth:
                WriteInt32(tim, 4, 3);
                break;
            case TimFault.ClutBounds:
                WriteInt32(tim, 8, tim.Length + 100);
                break;
            case TimFault.ImageBounds:
                int imageHeader = 8 + ReadInt32(tim, 8);
                WriteInt32(tim, imageHeader, tim.Length + 100);
                break;
        }
        Assert.False(EmdDonor.TryDetectV1(BuildEmd(tim: tim), out _));
    }

    [Fact]
    public void TryDetectV1_rejects_deliberately_ambiguous_tail_layout()
    {
        // An EMD tail always also offers two final words to the EMW candidate. Make both directory
        // layouts structurally plausible but destroy the decisive TIM/TMD semantic boundary.
        var bytes = BuildEmd(sixChunks: false);
        int tim = ReadInt32(bytes, bytes.Length - 4);
        WriteInt32(bytes, tim, 0x41);
        Assert.False(EmdDonor.TryDetectV1(bytes, out _));
    }

    [Fact]
    public void TryDetectV2_reports_v2_and_rejects_others()
    {
        Assert.True(EmdDonor.TryDetectV2(SyntheticEmd.BuildRe2(), out var v2));
        Assert.Equal(BioVersion.Biohazard2, v2);
        Assert.True(EmdDonor.TryDetectV2(SyntheticEmd.BuildRe3(), out var v3));
        Assert.Equal(BioVersion.Biohazard3, v3);
        Assert.False(EmdDonor.TryDetectV2(SyntheticEmd.BuildV2(4), out _));
        Assert.False(EmdDonor.TryDetectV2(BuildEmd(), out _));
    }

    private static void AssertV1Kind(byte[] bytes, V1ContainerKind expected)
    {
        Assert.True(EmdDonor.TryDetectV1(bytes, out var actual));
        Assert.Equal(expected, actual);
    }

    private static byte[] BuildEmd(
        bool sixChunks = false,
        bool standardTmd = false,
        byte[]? emr = null,
        byte[]? edd = null,
        byte[]? tmd = null,
        byte[]? tim = null)
    {
        emr ??= BuildEmr();
        edd ??= BuildEdd();
        tmd ??= BuildTmd(standardTmd);
        tim ??= BuildTim();
        return BuildV1(sixChunks
            ? new[] { emr, edd, BuildEmr(), BuildEdd(), tmd, tim }
            : new[] { emr, edd, tmd, tim }, 5);
    }

    private static byte[] BuildEmw()
        => BuildV1(new[] { BuildEmr(), BuildEdd(), BuildTmd() }, 2);

    private static byte[] BuildEmr(int partCount = 1)
    {
        var builder = new Emr.Builder(BioVersion.Biohazard1)
        {
            NumParts = checked((ushort)partCount),
            KeyFrameSize = checked((ushort)(12 + partCount * 6)),
        };
        for (int i = 0; i < partCount; i++)
        {
            builder.RelativePositions.Add(default);
            builder.Armatures.Add(i == 0 && partCount > 1 ? new byte[] { 1 } : Array.Empty<byte>());
        }
        return builder.ToEmr().Data.ToArray();
    }

    private static byte[] BuildEdd()
    {
        var builder = new Edd1.Builder(BioVersion.Biohazard1);
        var animation = new Edd1.Builder.Animation();
        animation.Frames.Add(default);
        builder.Animations.Add(animation);
        return builder.ToEdd().Data.ToArray();
    }

    private static byte[] BuildTmd(bool standardWordZero = false)
    {
        var builder = new Tmd.Builder();
        var part = new Tmd.Builder.Part();
        part.Positions.Add(default);
        part.Normals.Add(default);
        part.Triangles.Add(default);
        builder.Parts.Add(part);
        var bytes = builder.ToMesh().Data.ToArray();
        if (standardWordZero) WriteInt32(bytes, 0, 0x41);
        return bytes;
    }

    private static byte[] BuildTim() => new TimFile(2, 1, 8).GetBytes();

    private static byte[] BuildV1(IReadOnlyList<byte[]> chunks, int directoryWords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offsets = new int[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            offsets[i] = checked((int)stream.Position);
            writer.Write(chunks[i]);
        }
        for (int i = 0; i < directoryWords; i++)
        {
            int offsetIndex = chunks.Count - directoryWords + i;
            writer.Write(offsetIndex < 0 ? 0 : offsets[offsetIndex]);
        }
        return stream.ToArray();
    }

    private static int ReadInt32(byte[] bytes, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static void WriteInt32(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    public enum TailOffsetFault
    {
        Negative,
        Decreasing,
        DirectoryCrossing,
        OutOfRange,
    }

    public enum TimFault
    {
        Magic,
        Depth,
        ClutBounds,
        ImageBounds,
    }
}
