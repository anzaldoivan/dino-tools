using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class Re1PlayerNpcEmdPreflightTests
{
    [Fact]
    public void Analyze_reports_identity_and_explicit_unresolved_gates()
    {
        var result = Re1PlayerNpcEmdPreflight.Analyze(BuildPlayerEmd());

        Assert.True(result.IdentityAccepted);
        Assert.Equal(15, result.EmrPartCount);
        Assert.Equal(16, result.TmdObjectCount);
        Assert.Equal(15, result.Objects.Count);
        Assert.False(result.BridgeReady);
        Assert.Contains("coordinate transform", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("object 15", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("binding", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("palette-origin", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("atlas", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capacity", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transform-run", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unrelated", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", string.Join("\n", result.RefusalReasons), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_refuses_non_player_or_non_v1_inputs()
    {
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(BuildPlayerEmd(sixChunks: true)));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(BuildEmw()));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(SyntheticEmd.BuildV2(4)));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(SyntheticEmd.BuildRe2()));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(SyntheticEmd.BuildRe3()));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(new byte[] { 1, 2, 3 }));
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(new byte[64]));

        var ambiguous = BuildPlayerEmd();
        int timOffset = BinaryPrimitives.ReadInt32LittleEndian(ambiguous.AsSpan(ambiguous.Length - 4));
        BinaryPrimitives.WriteInt32LittleEndian(ambiguous.AsSpan(timOffset, 4), 0x41);
        Assert.Throws<InvalidDataException>(() => Re1PlayerNpcEmdPreflight.Analyze(ambiguous));
    }

    private static byte[] BuildPlayerEmd(bool sixChunks = false)
    {
        var emr = BuildEmr(15);
        var edd = BuildEdd();
        var tmd = BuildTmd(16);
        var tim = new TimFile(256, 256, 8).GetBytes();
        return BuildV1(sixChunks
            ? new[] { emr, edd, BuildEmr(15), BuildEdd(), tmd, tim }
            : new[] { emr, edd, tmd, tim }, 5);
    }

    private static byte[] BuildEmw()
        => BuildV1(new[] { BuildEmr(1), BuildEdd(), BuildTmd(1) }, 2);

    private static byte[] BuildEmr(int partCount)
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

    private static byte[] BuildTmd(int objectCount)
    {
        var builder = new Tmd.Builder();
        for (int i = 0; i < objectCount; i++)
        {
            var part = new Tmd.Builder.Part();
            int count = i == 0 ? 3 : 1;
            for (int j = 0; j < count; j++)
            {
                part.Positions.Add(new Tmd.Vector((short)(i + j), 0, 0));
                part.Normals.Add(new Tmd.Vector(0, 0, 4096));
            }
            if (i == 0)
            {
                part.Triangles.Add(new Tmd.Triangle
                {
                    v0 = 0,
                    v1 = 1,
                    v2 = 2,
                    n0 = 0,
                    n1 = 1,
                    n2 = 2,
                });
            }
            builder.Parts.Add(part);
        }
        return builder.ToMesh().Data.ToArray();
    }

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
}
