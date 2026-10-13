using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class DependencySetupScriptTests
{
    [Fact]
    public void Setup_script_pins_sources_hashes_versions_and_user_cache()
    {
        string script = Path.Combine(FindRepoRoot(), "model-pipeline", "setup-dependencies.ps1");

        Assert.True(File.Exists(script), $"Missing reproducible dependency setup script: {script}");
        string source = File.ReadAllText(script);
        Assert.Contains("5.2.0", source);
        Assert.Contains("2D184B626C001692C362291911293B6A297179D618D95E9E9192C3A80318ADC4", source);
        Assert.Contains("https://download.blender.org/release/Blender5.2/blender-5.2.0-windows-x64.zip", source);
        Assert.Contains("24.16.0", source);
        Assert.Contains("edaca9bd58ec8e92037dac4e877d52f6b8f430b81c18b57e264b4e2fb111cd56", source);
        Assert.Contains("https://nodejs.org/download/release/v24.16.0/node-v24.16.0-win-x64.zip", source);
        Assert.Contains("11.13.0", source);
        Assert.Contains("2.0.0-dev.3.10", source);
        Assert.Contains("npm.cmd", source);
        Assert.Contains("LocalApplicationData", source);
        Assert.DoesNotContain("DINORAND_DC1_DIR", source);
        Assert.DoesNotContain("DINORAND_DC2_DIR", source);
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
}
