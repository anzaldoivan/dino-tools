using Xunit;

namespace DinoCrisis.Interchange.Tests;

public sealed class ProbeDiscoveryTests
{
    [Fact]
    public void Throwaway_probes_are_not_compiled_into_the_maintained_test_assembly()
    {
        var probeTypes = typeof(ProbeDiscoveryTests).Assembly
            .GetTypes()
            .Where(type => type.Name.StartsWith("_ZZ_", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            probeTypes.Length == 0,
            $"throwaway probes compiled into maintained tests: {string.Join(", ", probeTypes)}");
    }
}
