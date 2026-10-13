using DinoCrisis.Model.Cli;
using Xunit;
namespace DinoCrisis.Model.Tests;
public sealed class OutputSafetyTests
{
    [Theory]
    [InlineData("obj")]
    [InlineData("gltf")]
    [InlineData("import")]
    public void Editors_refuse_input_overwrite_before_reading(string verb)
    {
        string input = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        var args = verb == "import" ? new[] { verb, "missing.glb", input, input } : new[] { verb, input, input };
        using var output = new StringWriter();
        Assert.Equal(2, DcmCli.Run(args, output));
        Assert.Contains("Refusing to overwrite input", output.ToString());
        Assert.False(File.Exists(input));
    }

    [Fact]
    public void Editor_refuses_configured_install_and_symlink_destination()
    {
        string root = Path.Combine(Path.GetTempPath(), "dino-tools-safety-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DINORAND_DC1_DIR", root);
            Check(Path.Combine(root, "output.dat"));
            if (!OperatingSystem.IsWindows())
            {
                string alias = root + "-alias";
                Directory.CreateSymbolicLink(alias, root);
                try { Check(Path.Combine(alias, "output.dat")); }
                finally { Directory.Delete(alias); }
            }
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DINORAND_DC1_DIR", previous);
            Directory.Delete(root, true);
        }
    }
    private static void Check(string destination)
    {
        using var output = new StringWriter();
        Assert.Equal(2, DcmCli.Run(new[] { "import", "missing.glb", "missing.dat", destination }, output));
        Assert.Contains("Refusing output inside original installation", output.ToString());
    }
}
