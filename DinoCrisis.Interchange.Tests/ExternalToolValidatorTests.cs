using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DinoCrisis.Interchange;
using DinoCrisis.Model;
using DinoCrisis.Model.Tests;
using DinoCrisis.Model.Textures;
using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class ExternalToolValidatorTests
{
    [PipelineTheory]
    [InlineData("dc1")]
    [InlineData("dc2")]
    public void Khronos_validator_reports_clean_deterministic_json_without_sidecars(string game)
    {
        var variant = game == "dc1" ? DcmVariant.Dc1 : DcmVariant.Dc2;
        using var temp = ExternalToolTestSupport.CreateTempDirectory();
        string glb = Path.Combine(temp.Path, $"synthetic-{game}.glb");
        ExternalToolTestSupport.WriteSyntheticGlb(variant, glb);

        var first = ExternalToolTestSupport.ValidateGlb(glb);
        var second = ExternalToolTestSupport.ValidateGlb(glb);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(string.Empty, first.StandardError);
        Assert.Equal(first.StandardOutput, second.StandardOutput);

        using var json = JsonDocument.Parse(first.StandardOutput);
        var root = json.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("2.0.0-dev.3.10", root.GetProperty("validatorVersion").GetString());
        Assert.Equal(0, root.GetProperty("report").GetProperty("issues").GetProperty("numErrors").GetInt32());
        Assert.Equal(0, root.GetProperty("report").GetProperty("issues").GetProperty("numWarnings").GetInt32());

        Assert.Equal(new[] { Path.GetFileName(glb) }, Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order().ToArray());
    }
}

internal static class ExternalToolTestSupport
{
    private const string BlenderVersion = "5.2.0";
    private const string NodeVersion = "24.16.0";
    private const string ValidatorVersion = "2.0.0-dev.3.10";

    public static TemporaryDirectory CreateTempDirectory() => new();

    public static SyntheticWitness WriteSyntheticGlb(DcmVariant variant, string path)
    {
        var dcm = DcmModel.Read(SyntheticBlob.Build(variant), 0, SyntheticBlob.Base, variant);
        var image = new PsxImage(256, 512);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image.Set(x, y, (byte)x, (byte)y, (byte)(x ^ y), 255);

        ushort tpage = variant.Game == DcmGame.Dc1 ? dcm.Triangles[0].TPage : dcm.Header.TPage;
        int pageX16 = (tpage & 0xF) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        // DcmTexture is intentionally created only by decoders in production. Reflection keeps this
        // clean-room witness test-local instead of widening that API solely for an integration test.
        var constructor = typeof(DcmTexture).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var texture = (DcmTexture)constructor.Invoke(new object[] { image, pageX16, pageY, 2 });
        GltfInterchange.FromDcm(dcm, texture).SaveGLB(path);
        return new SyntheticWitness(dcm, texture);
    }

    public static ProcessResult ValidateGlb(string inputPath)
    {
        string root = FindRepoRoot();
        string validatorRoot = Environment.GetEnvironmentVariable("DINORAND_GLTF_VALIDATOR_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DinoRand", "deps", "gltf-validator", ValidatorVersion);
        string node = Environment.GetEnvironmentVariable("DINORAND_NODE_EXE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DinoRand", "deps", "node", NodeVersion, "node.exe");

        return Run(node, new[]
        {
            Path.Combine(root, "model-pipeline", "validate-gltf.mjs"),
            validatorRoot,
            inputPath,
        });
    }

    public static ProcessResult BlenderRoundTrip(string inputPath, string outputPath, string resultPath)
    {
        string root = FindRepoRoot();
        string blender = Environment.GetEnvironmentVariable("DINORAND_BLENDER_EXE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DinoRand", "deps", "blender", BlenderVersion, "blender.exe");

        return Run(blender, new[]
        {
            "--factory-startup",
            "--offline-mode",
            "--background",
            "--disable-autoexec",
            "--python-exit-code", "1",
            "--python", Path.Combine(root, "model-pipeline", "blender-roundtrip.py"),
            "--", inputPath, outputPath, resultPath,
        }, timeoutMilliseconds: 180_000);
    }

    public static ProcessResult Run(string executable, IEnumerable<string> arguments, int timeoutMilliseconds = 60_000)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} did not exit within {timeoutMilliseconds} ms.");
        }
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(process.ExitCode, stdout.Result.TrimEnd(), stderr.Result.TrimEnd());
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "model-pipeline", "package.json")))
                return directory;
            directory = Path.GetDirectoryName(directory);
        }
        throw new DirectoryNotFoundException("Could not find the DinoRand repository root.");
    }

    internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    internal readonly record struct SyntheticWitness(DcmModel Model, DcmTexture Texture);

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dinorand-model-pipeline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
