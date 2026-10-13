using System.Text.RegularExpressions;
using Xunit;

namespace DinoCrisis.Model.Tests;

public sealed class FixtureGatePolicyTests
{
    [Fact]
    public void MaintainedFixtureTests_DoNotSilentlyReturn()
    {
        string tools = FindToolsDirectory();
        var offenders = new List<string>();
        var silentReturn = new Regex(
            @"if\s*\([^\r\n]*(?:null|File\.Exists|Directory\.Exists)[^\r\n]*\)\s*return\s*;",
            RegexOptions.CultureInvariant);

        foreach (string project in new[] { "DinoCrisis.Model.Tests", "DinoCrisis.Interchange.Tests" })
        {
            string projectDir = Path.Combine(tools, project);
            foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith("_ZZ_", StringComparison.Ordinal))
                    continue;

                string source = File.ReadAllText(file);
                if (silentReturn.IsMatch(source))
                    offenders.Add(Path.GetRelativePath(tools, file));
            }
        }

        Assert.Empty(offenders);
    }

    private static string FindToolsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string tools = directory.FullName;
            if (Directory.Exists(Path.Combine(tools, "DinoCrisis.Model.Tests")) &&
                Directory.Exists(Path.Combine(tools, "DinoCrisis.Interchange.Tests")))
                return tools;
        }

        throw new DirectoryNotFoundException("Could not locate the DinoRand tools test projects.");
    }
}
