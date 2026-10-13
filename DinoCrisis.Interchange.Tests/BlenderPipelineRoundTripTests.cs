using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using DinoCrisis.Model;
using DinoCrisis.Model.Containers;
using DinoCrisis.Model.Locating;
using DinoCrisis.Model.Textures;
using SharpGLTF.Schema2;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class BlenderPipelineRoundTripTests
{
    [PipelineFact]
    public void Headless_Blender_failure_writes_deterministic_machine_readable_result()
    {
        using var temp = ExternalToolTestSupport.CreateTempDirectory();
        string input = Path.Combine(temp.Path, "missing.glb");
        string output = Path.Combine(temp.Path, "output.glb");
        string result = Path.Combine(temp.Path, "result.json");

        var blender = ExternalToolTestSupport.BlenderRoundTrip(input, output, result);

        Assert.Equal(1, blender.ExitCode);
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(result));
        using var json = JsonDocument.Parse(File.ReadAllText(result));
        Assert.Equal("error", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("missing.glb", json.RootElement.GetProperty("input").GetString());
        Assert.Equal("output.glb", json.RootElement.GetProperty("output").GetString());
        Assert.Contains("input does not exist", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(new[] { "result.json" }, Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }

    [PipelineTheory]
    [InlineData("dc1")]
    [InlineData("dc2")]
    public void Headless_Blender_preserves_synthetic_model_semantics(string game)
    {
        var variant = game == "dc1" ? DcmVariant.Dc1 : DcmVariant.Dc2;
        using var temp = ExternalToolTestSupport.CreateTempDirectory();
        string input = Path.Combine(temp.Path, $"synthetic-{game}-input.glb");
        string output = Path.Combine(temp.Path, $"synthetic-{game}-output.glb");
        string result = Path.Combine(temp.Path, $"synthetic-{game}-result.json");
        var witness = ExternalToolTestSupport.WriteSyntheticGlb(variant, input);

        AssertValidatorClean(input);
        var before = SemanticSnapshot.Read(input);
        Assert.Equal(new DisplayContract(false, 9728, 9728, 33071, 33071), DisplayContract.Read(input));

        var blender = ExternalToolTestSupport.BlenderRoundTrip(input, output, result);
        Assert.True(blender.ExitCode == 0, $"Blender failed ({blender.ExitCode}).\nstdout:\n{blender.StandardOutput}\nstderr:\n{blender.StandardError}");
        AssertNoUnexpectedDiagnostics(blender);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(result));

        using (var json = JsonDocument.Parse(File.ReadAllText(result)))
        {
            var root = json.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal("BLENDER", root.GetProperty("boneHeuristic").GetString());
            Assert.Equal("5.2.0 LTS", root.GetProperty("blenderVersion").GetString());
            Assert.Equal(Path.GetFileName(input), root.GetProperty("input").GetString());
            Assert.Equal(Path.GetFileName(output), root.GetProperty("output").GetString());
            var scene = root.GetProperty("scene");
            Assert.Equal(2, scene.GetProperty("uvLayers").GetInt32());
            Assert.Equal(1, scene.GetProperty("singleSidedMaterials").GetInt32());
            Assert.Equal(1, scene.GetProperty("nearestImageTextures").GetInt32());
            Assert.Equal(1, scene.GetProperty("clampToEdgeImageTextures").GetInt32());
        }

        AssertValidatorClean(output);
        Assert.Equal(new DisplayContract(false, 9728, 9984, 33071, 33071), DisplayContract.Read(output));
        SemanticSnapshot after;
        try
        {
            after = SemanticSnapshot.Read(output);
        }
        catch (Exception error)
        {
            throw new InvalidDataException($"{error.Message}\nBlender stdout:\n{blender.StandardOutput}\nBlender stderr:\n{blender.StandardError}", error);
        }

        Assert.Equal(before.Joints, after.Joints);
        Assert.Equal(before.InverseBindMatrices, after.InverseBindMatrices);
        Assert.Equal(before.MeshCount, after.MeshCount);
        Assert.Equal(before.PrimitiveCount, after.PrimitiveCount);
        Assert.Equal(before.TriangleCount, after.TriangleCount);
        Assert.Equal(before.MaterialNames, after.MaterialNames);
        Assert.Equal(before.ImageCount, after.ImageCount);
        Assert.Equal(before.TextureCount, after.TextureCount);
        Assert.Equal(1, after.ImageCount);
        Assert.Equal(1, after.TextureCount);
        Assert.Equal(before.DecodedPixelHash, after.DecodedPixelHash);
        Assert.Equal(before.PositionBounds, after.PositionBounds);
        Assert.Equal(before.UvCorners, after.UvCorners);
        Assert.Equal(before.PolygonOwnerCorners, after.PolygonOwnerCorners);
        Assert.Equal(before.VertexJointAssignments, after.VertexJointAssignments);
        Assert.True(Vector3.Dot(before.WindingNormal, after.WindingNormal) > 0.999f,
            $"Triangle winding changed: {before.WindingNormal} -> {after.WindingNormal}.");
        Assert.True(after.AllWeightsRigidAndNormalized);

        var imported = GltfImporter.ReadSameRig(ModelRoot.Load(output), witness.Model, witness.Texture);
        Assert.NotEmpty(imported.Vertices);
        Assert.NotEmpty(imported.Triangles.Cast<object>().Concat(imported.Quads));
        Assert.All(imported.Triangles, triangle => Assert.InRange(triangle.Part, 0, witness.Model.Parts.Count - 1));
        Assert.All(imported.Quads, quad => Assert.InRange(quad.Part, 0, witness.Model.Parts.Count - 1));

        Assert.Equal(
            new[] { Path.GetFileName(input), Path.GetFileName(output), Path.GetFileName(result) }.Order(),
            Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Dc1Fact("core00.dat")]
    public void Headless_Blender_regina_round_trip_still_maps_to_the_exact_stock_pool()
    {
        using var temp = ExternalToolTestSupport.CreateTempDirectory();
        string input = Path.Combine(temp.Path, "regina-input.glb");
        string output = Path.Combine(temp.Path, "regina-output.glb");
        string result = Path.Combine(temp.Path, "regina-result.json");
        string core = FixtureGate.Pristine(FixtureGate.Dc1File("core00.dat"));
        byte[] package = File.ReadAllBytes(core);
        var model = DcmLocator.FromPackage(package).First(candidate =>
            candidate.Header.PartCount == 15 && candidate.Header.TriangleCount == 314 &&
            candidate.Header.QuadCount == 141);
        Assert.True(GltfInterchange.ExportGlb(package, model, input));
        var before = SemanticSnapshot.Read(input);
        Assert.Equal(new DisplayContract(false, 9728, 9728, 33071, 33071), DisplayContract.Read(input));

        var blender = ExternalToolTestSupport.BlenderRoundTrip(input, output, result);
        Assert.True(blender.ExitCode == 0,
            $"Blender failed ({blender.ExitCode}).\nstdout:\n{blender.StandardOutput}\nstderr:\n{blender.StandardError}");
        using (var json = JsonDocument.Parse(File.ReadAllText(result)))
        {
            var scene = json.RootElement.GetProperty("scene");
            Assert.True(scene.GetProperty("triangles").GetInt32() == 596, File.ReadAllText(result));
            Assert.Equal(595, scene.GetProperty("alignedTriangles").GetInt32());
            Assert.Equal(1, scene.GetProperty("opposedTriangles").GetInt32());
            Assert.Equal(0, scene.GetProperty("degenerateTriangles").GetInt32());
            Assert.Equal(2, scene.GetProperty("uvLayers").GetInt32());
            Assert.Equal(15, scene.GetProperty("jointNames").GetArrayLength());
            Assert.Equal(1, scene.GetProperty("singleSidedMaterials").GetInt32());
            Assert.Equal(1, scene.GetProperty("nearestImageTextures").GetInt32());
            Assert.Equal(1, scene.GetProperty("clampToEdgeImageTextures").GetInt32());
        }
        var after = SemanticSnapshot.Read(output);
        Assert.Equal(before.Joints, after.Joints);
        Assert.Equal(before.InverseBindMatrices, after.InverseBindMatrices);
        Assert.Equal(before.TriangleCount, after.TriangleCount);
        Assert.Equal(before.DecodedPixelHash, after.DecodedPixelHash);
        Assert.Equal(before.PositionBounds, after.PositionBounds);
        Assert.Equal(before.UvCorners, after.UvCorners);
        Assert.Equal(before.PolygonOwnerCorners, after.PolygonOwnerCorners);
        Assert.Equal(before.VertexJointAssignments.Distinct().Order(), after.VertexJointAssignments.Distinct().Order());
        Assert.True(after.AllWeightsRigidAndNormalized);
        Assert.Equal(new DisplayContract(false, 9728, 9984, 33071, 33071), DisplayContract.Read(output));
        var imported = GltfImporter.ReadSameRig(ModelRoot.Load(output), model,
            DcmTextureDecoder.TryDecode(GianPackage.TryParse(package)!, package, model));

        var spliced = Dc1ModelSplicer.Splice(package, candidate =>
            candidate.Header.PartCount == 15 && candidate.Header.TriangleCount == 314 &&
            candidate.Header.QuadCount == 141, imported);
        var reread = DcmLocator.FromPackage(spliced.Package).Single(candidate => candidate.Header.PartCount == 15);
        Assert.Equal(model.Vertices, reread.Vertices);
        Assert.Equal(model.Normals, reread.Normals);
    }

    private static void AssertNoUnexpectedDiagnostics(ExternalToolTestSupport.ProcessResult blender)
    {
        // Reviewed Blender exporter warning: the script deliberately roots skinned meshes so the
        // resulting glTF satisfies NODE_SKINNED_MESH_NON_ROOT. The armature modifier still names
        // the correct armature, and the semantic + importer assertions below prove the binding.
        string[] allowedWarnings = { "Armature must be the parent of skinned mesh" };
        var unexpected = (blender.StandardOutput + "\n" + blender.StandardError)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Traceback", StringComparison.OrdinalIgnoreCase))
            .Where(line => !allowedWarnings.Any(allowed => line.Contains(allowed, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(unexpected.Length == 0, $"Unexpected Blender diagnostics:\n{string.Join('\n', unexpected)}");
    }

    private static void AssertValidatorClean(string path)
    {
        var validation = ExternalToolTestSupport.ValidateGlb(path);
        Assert.True(validation.ExitCode == 0,
            $"Validator failed ({validation.ExitCode}).\nstdout:\n{validation.StandardOutput}\nstderr:\n{validation.StandardError}");
        using var json = JsonDocument.Parse(validation.StandardOutput);
        var issues = json.RootElement.GetProperty("report").GetProperty("issues");
        Assert.True(issues.GetProperty("numErrors").GetInt32() == 0,
            $"Validator errors for {Path.GetFileName(path)}:\n{validation.StandardOutput}");
        Assert.True(issues.GetProperty("numWarnings").GetInt32() == 0,
            $"Validator warnings for {Path.GetFileName(path)}:\n{validation.StandardOutput}");
    }

    private sealed record DisplayContract(bool DoubleSided, int MagFilter, int MinFilter, int WrapS, int WrapT)
    {
        public static DisplayContract Read(string path)
        {
            byte[] glb = File.ReadAllBytes(path);
            int jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
            using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
            var root = document.RootElement;
            var material = root.GetProperty("materials")[0];
            bool doubleSided = material.TryGetProperty("doubleSided", out var value) && value.GetBoolean();
            var texture = root.GetProperty("textures")[0];
            int samplerIndex = texture.GetProperty("sampler").GetInt32();
            var sampler = root.GetProperty("samplers")[samplerIndex];
            return new DisplayContract(
                doubleSided,
                sampler.GetProperty("magFilter").GetInt32(),
                sampler.GetProperty("minFilter").GetInt32(),
                sampler.GetProperty("wrapS").GetInt32(),
                sampler.GetProperty("wrapT").GetInt32());
        }
    }

    private sealed record SemanticSnapshot(
        string[] Joints,
        string[] InverseBindMatrices,
        int MeshCount,
        int PrimitiveCount,
        int TriangleCount,
        string[] MaterialNames,
        int ImageCount,
        int TextureCount,
        string DecodedPixelHash,
        string PositionBounds,
        string[] UvCorners,
        string[] PolygonOwnerCorners,
        string[] VertexJointAssignments,
        Vector3 WindingNormal,
        bool AllWeightsRigidAndNormalized)
    {
        public static SemanticSnapshot Read(string path)
        {
            var root = ModelRoot.Load(path);
            var skin = root.LogicalSkins.Single();
            var joints = Enumerable.Range(0, skin.JointsCount)
                .Select(i => skin.GetJoint(i).Joint)
                .Select(j => $"{j.Name}|{JointParentName(j)}|{MatrixSignature(j.LocalMatrix)}")
                .Order()
                .ToArray();
            var primitives = root.LogicalMeshes.SelectMany(mesh => mesh.Primitives).ToArray();
            var materialNames = primitives.Select(p => p.Material?.Name ?? "<none>").Order().ToArray();

            var allPositions = new List<Vector3>();
            var uvCorners = new List<string>();
            var polygonOwnerCorners = new List<string>();
            Vector3 winding = Vector3.Zero;
            int triangles = 0;
            bool rigidWeights = true;
            var vertexJoints = new List<string>();

            foreach (var primitive in primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION")!.AsVector3Array();
                var uvs = primitive.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
                var owners = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array()
                    ?? throw new InvalidDataException($"A primitive in {Path.GetFileName(path)} lost DCM polygon-part metadata.");
                var jointAccessor = primitive.GetVertexAccessor("JOINTS_0");
                var weightAccessor = primitive.GetVertexAccessor("WEIGHTS_0");
                if (jointAccessor is null || weightAccessor is null)
                    throw new InvalidDataException($"A primitive in {Path.GetFileName(path)} is not skinned.");
                var jointVectors = jointAccessor.AsVector4Array();
                var weightVectors = weightAccessor.AsVector4Array();
                allPositions.AddRange(positions);

                for (int i = 0; i < weightVectors.Count; i++)
                {
                    var weights = weightVectors[i];
                    var joint = jointVectors[i];
                    rigidWeights &= Nearly(weights.X, 1) && Nearly(weights.Y + weights.Z + weights.W, 0)
                        && joint.X >= 0 && joint.X < root.LogicalSkins[0].JointsCount;
                    int dominant = DominantJoint(weights, joint);
                    string jointName = skin.GetJoint(dominant).Joint.Name ?? "<unnamed>";
                    vertexJoints.Add($"{VectorSignature(positions[i])}|{jointName}");
                }

                foreach (var (a, b, c) in primitive.GetTriangleIndices())
                {
                    triangles++;
                    winding += Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    uvCorners.Add(UvSignature(uvs[a]));
                    uvCorners.Add(UvSignature(uvs[b]));
                    uvCorners.Add(UvSignature(uvs[c]));
                    polygonOwnerCorners.Add(UvSignature(owners[a]));
                    polygonOwnerCorners.Add(UvSignature(owners[b]));
                    polygonOwnerCorners.Add(UvSignature(owners[c]));
                }
            }

            Vector3 min = new(allPositions.Min(p => p.X), allPositions.Min(p => p.Y), allPositions.Min(p => p.Z));
            Vector3 max = new(allPositions.Max(p => p.X), allPositions.Max(p => p.Y), allPositions.Max(p => p.Z));
            return new SemanticSnapshot(
                joints,
                skin.InverseBindMatrices.Select(MatrixSignature).ToArray(),
                root.LogicalMeshes.Count,
                primitives.Length,
                triangles,
                materialNames,
                root.LogicalImages.Count,
                root.LogicalTextures.Count,
                PngPixelHash(root.LogicalImages.Single().Content.Content.ToArray()),
                $"{VectorSignature(min)}|{VectorSignature(max)}",
                uvCorners.Order().ToArray(),
                polygonOwnerCorners.Order().ToArray(),
                vertexJoints.Order().ToArray(),
                Vector3.Normalize(winding),
                rigidWeights);
        }

        private static bool Nearly(float actual, float expected) => MathF.Abs(actual - expected) < 0.0001f;
        private static int DominantJoint(Vector4 weights, Vector4 joints)
        {
            float[] weight = { weights.X, weights.Y, weights.Z, weights.W };
            float[] joint = { joints.X, joints.Y, joints.Z, joints.W };
            int index = Array.IndexOf(weight, weight.Max());
            return checked((int)joint[index]);
        }
        private static string? JointParentName(Node joint) =>
            joint.VisualParent?.Name?.StartsWith("joint_", StringComparison.Ordinal) == true
                ? joint.VisualParent.Name
                : null;
        private static string MatrixSignature(Matrix4x4 m) => string.Join(",", new[]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
        }.Select(value => Normalize(value).ToString("G9", System.Globalization.CultureInfo.InvariantCulture)));
        private static float Normalize(float value)
        {
            float rounded = MathF.Round(value, 5);
            return rounded == 0 ? 0 : rounded;
        }
        private static string VectorSignature(Vector3 value) =>
            $"{ScalarSignature(value.X)},{ScalarSignature(value.Y)},{ScalarSignature(value.Z)}";
        private static string UvSignature(Vector2 value) =>
            $"{ScalarSignature(value.X)},{ScalarSignature(value.Y)}";
        private static string ScalarSignature(float value) =>
            Normalize(value).ToString("F5", System.Globalization.CultureInfo.InvariantCulture);

        private static string PngPixelHash(byte[] png)
        {
            const int SignatureLength = 8;
            int offset = SignatureLength;
            int width = 0, height = 0;
            using var compressed = new MemoryStream();
            while (offset + 12 <= png.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                var data = png.AsSpan(offset + 8, length);
                if (type == "IHDR")
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 6)
                        throw new InvalidDataException("Synthetic witness must remain RGBA8 PNG.");
                }
                else if (type == "IDAT")
                {
                    compressed.Write(data);
                }
                offset += 12 + length;
            }

            int stride = checked(width * 4);
            var filtered = new byte[checked(height * (stride + 1))];
            compressed.Position = 0;
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
                zlib.ReadExactly(filtered);

            var pixels = new byte[checked(height * stride)];
            for (int y = 0; y < height; y++)
            {
                int source = y * (stride + 1);
                int target = y * stride;
                byte filter = filtered[source++];
                for (int x = 0; x < stride; x++)
                {
                    byte raw = filtered[source + x];
                    byte left = x >= 4 ? pixels[target + x - 4] : (byte)0;
                    byte above = y > 0 ? pixels[target + x - stride] : (byte)0;
                    byte upperLeft = y > 0 && x >= 4 ? pixels[target + x - stride - 4] : (byte)0;
                    pixels[target + x] = filter switch
                    {
                        0 => raw,
                        1 => unchecked((byte)(raw + left)),
                        2 => unchecked((byte)(raw + above)),
                        3 => unchecked((byte)(raw + ((left + above) >> 1))),
                        4 => unchecked((byte)(raw + Paeth(left, above, upperLeft))),
                        _ => throw new InvalidDataException($"Unsupported PNG filter {filter}."),
                    };
                }
            }
            return Convert.ToHexString(SHA256.HashData(pixels));
        }

        private static byte Paeth(byte left, byte above, byte upperLeft)
        {
            int prediction = left + above - upperLeft;
            int leftDistance = Math.Abs(prediction - left);
            int aboveDistance = Math.Abs(prediction - above);
            int upperLeftDistance = Math.Abs(prediction - upperLeft);
            return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
                ? left
                : aboveDistance <= upperLeftDistance ? above : upperLeft;
        }
    }
}
