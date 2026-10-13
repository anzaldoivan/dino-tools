using Xunit;

namespace DinoCrisis.Model.Tests;

internal static class FixtureGate
{
    public static string Dc1File(string fileName) =>
        RequireFile("DINORAND_DC1_DIR", "DC1", fileName, "english", "Data");

    public static string Dc2File(string fileName) =>
        RequireFile("DINORAND_DC2_DIR", "DC2", fileName, "rebirth", "Data");

    public static string Dc1DataDirectory() => Path.GetDirectoryName(Dc1File("st605.dat"))!;

    public static string Dc2DataDirectory() => Path.GetDirectoryName(Dc2File("E00.DAT"))!;

    private static string RequireFile(
        string variable,
        string game,
        string fileName,
        params string[] nestedDataPath)
    {
        string root = RequireRoot(variable, game);
        string? resolved = FindFile(root, fileName, nestedDataPath);
        if (resolved is not null)
            return resolved;

        throw new InvalidOperationException(
            $"{game} fixture gate admitted the test, but required file '{fileName}' is unavailable under {variable}.");
    }

    public static string? Dc1SkipReason(params string[] requiredFiles) =>
        SkipReason("DINORAND_DC1_DIR", "DC1", new[] { "english", "Data" }, requiredFiles);

    public static string? Dc2SkipReason(params string[] requiredFiles) =>
        SkipReason("DINORAND_DC2_DIR", "DC2", new[] { "rebirth", "Data" }, requiredFiles);

    private static string? SkipReason(
        string variable,
        string game,
        string[] nestedDataPath,
        string[] requiredFiles)
    {
        string? configured = ConfiguredRoot(variable);
        if (configured is null)
            return $"{game} fixture skipped: {variable} is not configured as an available directory.";

        foreach (string fileName in requiredFiles)
            if (FindFile(configured, fileName, nestedDataPath) is null)
                return $"{game} fixture skipped: required file '{fileName}' is unavailable under {variable}.";

        return null;
    }

    private static string? FindFile(string root, string fileName, string[] nestedDataPath)
    {
        string nested = Path.Combine(new[] { root }.Concat(nestedDataPath).Append(fileName).ToArray());
        string data = Path.Combine(root, "Data", fileName);
        string direct = Path.Combine(root, fileName);

        foreach (string candidate in new[] { nested, data, direct })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string RequireRoot(string variable, string game)
    {
        return ConfiguredRoot(variable) ?? throw new InvalidOperationException(
            $"{game} fixture gate admitted the test, but {variable} is unavailable.");
    }

    private static string? ConfiguredRoot(string variable)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured))
            configured = ReadDotEnv(variable);

        configured = configured?.Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured)
            ? null
            : Path.GetFullPath(configured);
    }

    private static string? ReadDotEnv(string variable)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(path))
                continue;

            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                int separator = line.IndexOf('=');
                if (separator < 1 ||
                    !line[..separator].Trim().Equals(variable, StringComparison.Ordinal))
                    continue;

                return line[(separator + 1)..].Trim();
            }

            return null;
        }

        return null;
    }
}

internal sealed class Dc1FactAttribute : FactAttribute
{
    public Dc1FactAttribute(params string[] requiredFiles) => Skip = FixtureGate.Dc1SkipReason(requiredFiles);
}

internal sealed class Dc1TheoryAttribute : TheoryAttribute
{
    public Dc1TheoryAttribute(params string[] requiredFiles) => Skip = FixtureGate.Dc1SkipReason(requiredFiles);
}

internal sealed class Dc2FactAttribute : FactAttribute
{
    public Dc2FactAttribute(params string[] requiredFiles) => Skip = FixtureGate.Dc2SkipReason(requiredFiles);
}

internal sealed class Dc2TheoryAttribute : TheoryAttribute
{
    public Dc2TheoryAttribute(params string[] requiredFiles) => Skip = FixtureGate.Dc2SkipReason(requiredFiles);
}
