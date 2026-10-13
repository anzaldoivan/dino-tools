namespace DinoCrisis.Model.Cli;

internal static class OutputSafety
{
    public static void Check(string output, params string[] inputs)
    {
        string target = Canonical(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (inputs.Any(input => target.Equals(Canonical(input), comparison)))
            throw new IOException("Refusing to overwrite input");
        foreach (string key in new[] { "DINORAND_DC1_DIR", "DINORAND_DC2_DIR" })
        {
            string? value = Environment.GetEnvironmentVariable(key) ?? DotEnv(key);
            if (string.IsNullOrWhiteSpace(value)) continue;
            string root = Canonical(value.Trim().Trim('\"', '\''));
            if (target.Equals(root, comparison) || target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Refusing output inside original installation");
        }
    }

    // Resolve existing parent links too: an output through a symlink must not evade the gate.
    private static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return current;
    }

    private static string? DotEnv(string key)
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            string file = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(file)) continue;
            foreach (string raw in File.ReadLines(file))
            {
                int separator = raw.IndexOf('=');
                if (separator > 0 && raw[..separator].Trim() == key)
                    return raw[(separator + 1)..].Trim();
            }
        }
        return null;
    }
}
