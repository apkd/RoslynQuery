using System.Xml.Linq;

namespace RoslynQuery;

static class WorkspaceProjectDiscovery
{
    public static bool ContainsLegacyCSharpProject(string targetPath, string targetKind)
    {
        try
        {
            foreach (var project in EnumerateProjects(targetPath, targetKind))
                if (IsLegacyCSharpProject(project.Path))
                    return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool IsLegacyCSharpProject(string projectPath)
    {
        if (!File.Exists(projectPath) || !Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return false;

        var document = XDocument.Load(projectPath, LoadOptions.None);
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Project", StringComparison.Ordinal))
            return false;

        if (root.Attribute("Sdk") is not null)
            return false;

        if (root.Elements().Any(static element => string.Equals(element.Name.LocalName, "Sdk", StringComparison.Ordinal)))
            return false;

        if (root.Elements()
            .Any(static element => string.Equals(element.Name.LocalName, "Import", StringComparison.Ordinal)
                                   && element.Attribute("Sdk") is not null))
            return false;

        return FindProjectProperty(root, "TargetFramework") is null
               && FindProjectProperty(root, "TargetFrameworks") is null;
    }

    public static WorkspaceProjectEntry[] EnumerateProjects(string targetPath, string targetKind)
        => string.Equals(targetKind, "project", StringComparison.Ordinal)
            ? [new(Path.GetFileNameWithoutExtension(targetPath), Path.GetFullPath(targetPath))]
            : EnumerateSolutionProjects(targetPath).ToArray();

    public static IEnumerable<WorkspaceProjectEntry> EnumerateSolutionProjects(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            yield break;

        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var project in EnumerateSlnxProjects(solutionPath, solutionDirectory))
                yield return project;

            yield break;
        }

        foreach (var line in File.ReadLines(solutionPath))
        {
            if (!TryParseSlnProjectHeader(line, out var projectName, out var projectPath))
                continue;

            if (Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                yield return new(projectName, GetFullProjectPath(projectPath, solutionDirectory));
        }
    }

    static IEnumerable<WorkspaceProjectEntry> EnumerateSlnxProjects(string solutionPath, string solutionDirectory)
    {
        var document = XDocument.Load(solutionPath, LoadOptions.None);
        foreach (var element in document.Descendants().Where(static element => string.Equals(element.Name.LocalName, "Project", StringComparison.Ordinal)))
        {
            var path = element.Attribute("Path")?.Value;
            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                yield return new(Path.GetFileNameWithoutExtension(path), GetFullProjectPath(path, solutionDirectory));
        }
    }

    static bool TryParseSlnProjectHeader(string line, out string projectName, out string projectPath)
    {
        projectName = "";
        projectPath = "";

        if (!line.StartsWith("Project(", StringComparison.Ordinal))
            return false;

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var fields = ReadQuotedFields(line[(equalsIndex + 1)..]);
        if (fields.Count < 2)
            return false;

        projectName = fields[0];
        projectPath = fields[1];
        return true;
    }

    static List<string> ReadQuotedFields(string value)
    {
        var fields = new List<string>(3);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '"')
                continue;

            var builder = new System.Text.StringBuilder();
            i++;
            for (; i < value.Length; i++)
            {
                if (value[i] == '"')
                {
                    if (i + 1 < value.Length && value[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                        continue;
                    }

                    break;
                }

                builder.Append(value[i]);
            }

            fields.Add(builder.ToString());
        }

        return fields;
    }

    static string GetFullProjectPath(string projectPath, string solutionDirectory)
    {
        projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(projectPath)
            ? Path.GetFullPath(projectPath)
            : Path.GetFullPath(projectPath, solutionDirectory);
    }

    static string? FindProjectProperty(XElement root, string localName)
    {
        foreach (var element in root.Descendants())
            if (element.Name.LocalName == localName)
                return element.Value;

        return null;
    }
}

readonly record struct WorkspaceProjectEntry(string Name, string Path);
