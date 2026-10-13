using Xunit;
namespace DinoCrisis.Interchange.Tests;
internal static class PipelineGate
{
    public static string? SkipReason()
    {
        if (Environment.GetEnvironmentVariable("DINO_TOOLS_REQUIRE_PIPELINE") == "1") return null;
        return "Optional external pipeline skipped; set DINO_TOOLS_REQUIRE_PIPELINE=1 after installing pinned prerequisites.";
    }
}
internal sealed class PipelineFactAttribute : FactAttribute
{
    public PipelineFactAttribute() => Skip = PipelineGate.SkipReason();
}
internal sealed class PipelineTheoryAttribute : TheoryAttribute
{
    public PipelineTheoryAttribute() => Skip = PipelineGate.SkipReason();
}
