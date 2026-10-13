using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IntelOrca.Biohazard;
using IntelOrca.Biohazard.Model;

namespace DinoCrisis.Interchange;

public sealed record Re1PlayerNpcObjectFact(
    int ObjectIndex,
    int PositionCount,
    int NormalCount,
    int TriangleCount,
    int PaddedRecordCount);

public sealed record Re1PlayerNpcCapacityFacts(
    int RawRecordCount,
    IReadOnlyList<int> PerObjectPaddedRecordCounts,
    int PaddedRecordCount,
    int TriangleCount,
    int RawGeometryBytes,
    int PaddedGeometryBytes,
    bool StockRecordLimitFits,
    bool LiftedRecordLimitFits,
    bool StockGeometryRegionFits);

public sealed record Re1PlayerNpcMaterialFact(
    int ObjectIndex,
    ushort Page,
    ushort Clut,
    int TriangleCount);

public sealed record Re1PlayerNpcTextureFacts(
    int PixelFormat,
    int Width,
    int Height,
    int ImageOriginX,
    int ImageOriginY,
    int PaletteOriginX,
    int PaletteOriginY,
    int PaletteRows,
    IReadOnlyList<Re1PlayerNpcMaterialFact> Materials);

public sealed record Re1PlayerNpcEmdPreflightResult(
    bool IdentityAccepted,
    int EmrPartCount,
    int TmdObjectCount,
    int AnimationCount,
    IReadOnlyList<Re1PlayerNpcObjectFact> Objects,
    Re1PlayerNpcTextureFacts Texture,
    Re1PlayerNpcCapacityFacts CapacityForFirst15Objects,
    Re1PlayerNpcCapacityFacts CapacityForAll16Objects,
    IReadOnlyList<string> RefusalReasons)
{
    public bool BridgeReady => RefusalReasons.Count == 0;
}

/// <summary>
/// Read-only RE1 player/NPC EMD identity and measurement boundary. It never converts, selects a
/// target policy, edits a package, or emits model bytes.
/// </summary>
public static class Re1PlayerNpcEmdPreflight
{
    private const int StockRecordLimit = 400;
    private const int LiftedRecordLimit = 1024;
    private const int StockGeometryRegionBytes = 12_884;
    private const int Dc1RecordStride = 16;
    private const int Dc1TriangleStride = 16;

    public static Re1PlayerNpcEmdPreflightResult Analyze(ReadOnlyMemory<byte> emd)
    {
        byte[] bytes = emd.ToArray();
        if (!EmdDonor.TryDetectV1(bytes, out var kind))
            Refuse("input is not a uniquely identified V1 player/NPC EMD");
        if (kind != V1ContainerKind.Emd)
            Refuse("V1 EMW is an unsupported weapon family");

        using var stream = new MemoryStream(bytes, writable: false);
        var file = new EmdFile(BioVersion.Biohazard1, stream);
        if (file.NumChunks != 4)
            Refuse($"V1 EMD-{file.NumChunks} is not the supported player/NPC EMD-4 shape");

        var emr = file.GetEmr(0);
        var edd = file.GetEdd(0) as Edd1
            ?? throw new InvalidDataException("RE1 player/NPC EMD preflight: V1 animation chunk is not EDD1");
        var tmd = file.GetMesh(0) as Tmd
            ?? throw new InvalidDataException("RE1 player/NPC EMD preflight: V1 mesh chunk is not TMD");
        var tim = file.GetTim(0);
        if (emr.NumParts != 15 || tmd.NumObjects != 16)
            Refuse($"player/NPC identity requires 15 EMR parts and 16 TMD objects, found {emr.NumParts}/{tmd.NumObjects}");

        var allObjects = new List<Re1PlayerNpcObjectFact>(tmd.NumObjects);
        var materials = new List<Re1PlayerNpcMaterialFact>();
        for (int i = 0; i < tmd.NumObjects; i++)
        {
            var obj = tmd.Objects[i];
            int positionCount = tmd.GetPositionData(in obj).Length;
            int normalCount = tmd.GetNormalData(in obj).Length;
            var triangles = tmd.GetTriangles(in obj);
            var fact = new Re1PlayerNpcObjectFact(
                i,
                positionCount,
                normalCount,
                triangles.Length,
                checked((positionCount + 2) / 3 * 3));
            allObjects.Add(fact);

            foreach (var triangle in triangles)
            {
                int material = materials.FindIndex(m =>
                    m.ObjectIndex == i && m.Page == triangle.page && m.Clut == triangle.clutId);
                if (material < 0)
                {
                    materials.Add(new Re1PlayerNpcMaterialFact(i, triangle.page, triangle.clutId, 1));
                }
                else
                {
                    var existing = materials[material];
                    materials[material] = existing with { TriangleCount = checked(existing.TriangleCount + 1) };
                }
            }
        }

        var first15 = allObjects.Take(15).ToArray();
        var reasons = new[]
        {
            "RE1 player/NPC coordinate transform is unresolved; no Biohazard1 coordinate policy is selected.",
            "TMD object 15 disposition is unresolved; no deterministic 16-to-15 ownership rule is selected.",
            "RE1 player/NPC 16-to-15 part ownership is unresolved; no mesh-to-joint binding or remap is selected.",
            "RE1 player/NPC mesh-to-joint binding is unresolved; no semantic binding is selected.",
            "RE1 player/NPC TIM palette-origin semantics are unresolved across native and builder-zeroed origins.",
            "DC1 atlas compatibility is unresolved; the target-native atlas converter remains Biohazard3-only.",
            "DC1 capacity is diagnostic only; renderer limits and target ownership legality remain unresolved.",
            "DC1 transform-run legality is unresolved; no target part +7 ownership is selected.",
            "Unrelated target-byte preservation is unverified because this preflight emits no output.",
            "Runtime legality is unverified; no installation or process observation is performed.",
        };

        return new Re1PlayerNpcEmdPreflightResult(
            IdentityAccepted: true,
            EmrPartCount: emr.NumParts,
            TmdObjectCount: tmd.NumObjects,
            AnimationCount: edd.AnimationCount,
            Objects: Array.AsReadOnly(first15),
            Texture: new Re1PlayerNpcTextureFacts(
                tim.PixelFormat,
                tim.Width,
                tim.Height,
                tim.ImageOriginX,
                tim.ImageOriginY,
                tim.PaletteOriginX,
                tim.PaletteOriginY,
                tim.PaletteCount,
                Array.AsReadOnly(materials.ToArray())),
            CapacityForFirst15Objects: MeasureCapacity(first15),
            CapacityForAll16Objects: MeasureCapacity(allObjects),
            RefusalReasons: Array.AsReadOnly(reasons));
    }

    private static Re1PlayerNpcCapacityFacts MeasureCapacity(IReadOnlyList<Re1PlayerNpcObjectFact> objects)
    {
        int rawRecords = checked(objects.Sum(o => o.PositionCount));
        int paddedRecords = checked(objects.Sum(o => o.PaddedRecordCount));
        int triangles = checked(objects.Sum(o => o.TriangleCount));
        int rawBytes = checked((rawRecords * Dc1RecordStride) + (triangles * Dc1TriangleStride));
        int paddedBytes = checked((paddedRecords * Dc1RecordStride) + (triangles * Dc1TriangleStride));
        return new Re1PlayerNpcCapacityFacts(
            rawRecords,
            Array.AsReadOnly(objects.Select(o => o.PaddedRecordCount).ToArray()),
            paddedRecords,
            triangles,
            rawBytes,
            paddedBytes,
            paddedRecords <= StockRecordLimit,
            paddedRecords <= LiftedRecordLimit,
            paddedBytes <= StockGeometryRegionBytes);
    }

    private static void Refuse(string message)
        => throw new InvalidDataException("RE1 player/NPC EMD preflight: " + message);
}
