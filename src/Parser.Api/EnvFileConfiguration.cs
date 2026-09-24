namespace Parser.Api;

public static class EnvFileConfiguration
{
    public static IConfigurationBuilder AddProjectEnvFile(this IConfigurationBuilder builder, string contentRoot)
        => builder.AddInMemoryCollection(Read(FindPath(contentRoot)));

    public static string FindPath(string contentRoot)
    {
        // dotnet run sets the content root to src/Parser.Api; the shared .env lives by the solution.
        for (var directory = new DirectoryInfo(contentRoot); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Parser.slnx")))
                return Path.Combine(directory.FullName, ".env");
        // Published apps without the source tree use .env in their content root.
        return Path.Combine(contentRoot, ".env");
    }

    public static IReadOnlyDictionary<string, string?> Read(string filePath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath)) return values;
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(filePath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator <= 0) throw InvalidLine(lineNumber);
            var key = line[..separator].Trim();
            if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_')
                || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw InvalidLine(lineNumber);
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 0 && value[0] is '\'' or '"')
            {
                var end = value.IndexOf(value[0], 1);
                if (end < 0) throw InvalidLine(lineNumber);
                var remainder = value[(end + 1)..].TrimStart();
                if (remainder.Length > 0 && !remainder.StartsWith('#')) throw InvalidLine(lineNumber);
                value = value[1..end];
            }
            else
            {
                for (var index = 0; index < value.Length; index++)
                    if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                    {
                        value = value[..index].TrimEnd();
                        break;
                    }
            }
            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }
        return values;
    }

    private static FormatException InvalidLine(int lineNumber) =>
        new($"Некорректная строка {lineNumber} в .env. Ожидается KEY=value на одной строке; проверьте кавычки.");
}
