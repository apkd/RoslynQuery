using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynQuery;

static class WorkspaceProjectFilter
{
    const string IgnoreFileName = ".roslynqueryignore";

    public static WorkspaceLoadTarget Create(string path, string targetKind)
    {
        if (!string.Equals(targetKind, "solution", StringComparison.Ordinal))
            return new(path);

        var solutionDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(solutionDirectory))
            return new(path);

        var ignorePath = Path.Combine(solutionDirectory, IgnoreFileName);
        if (!File.Exists(ignorePath))
            return new(path);

        var filter = ProjectIgnoreFilter.Load(ignorePath);
        if (filter.IsEmpty)
            return new(path);

        var source = File.ReadAllText(path, Encoding.UTF8);
        var extension = Path.GetExtension(path);
        var result = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? FilterSlnx(source, solutionDirectory, filter)
            : FilterSln(source, solutionDirectory, filter);

        if (result.ExcludedProjects.Length is 0)
            return new(path, solutionDirectory: solutionDirectory, filter: filter);

        var temporaryPath = Path.Combine(
            solutionDirectory,
            $".{Path.GetFileNameWithoutExtension(path)}.roslynquery.{Guid.NewGuid():N}{extension}"
        );

        File.WriteAllText(temporaryPath, result.Source, Encoding.UTF8);

        return new(path, temporaryPath, solutionDirectory, filter, result.ExcludedProjects);
    }

    static FilterResult FilterSlnx(string source, string solutionDirectory, ProjectIgnoreFilter filter)
    {
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        var excludedProjects = new List<string>();

        foreach (var projectElement in document.Descendants().Where(static element => element.Name.LocalName == "Project").ToArray())
        {
            var pathAttribute = projectElement
                .Attributes()
                .FirstOrDefault(static attribute => attribute.Name.LocalName == "Path");

            if (pathAttribute is null || !filter.IsMatch(solutionDirectory, pathAttribute.Value))
                continue;

            excludedProjects.Add(ResolveProjectPath(solutionDirectory, pathAttribute.Value));
            projectElement.Remove();
        }

        return new(document.ToString(SaveOptions.DisableFormatting), excludedProjects.ToArray());
    }

    static FilterResult FilterSln(string source, string solutionDirectory, ProjectIgnoreFilter filter)
    {
        var lines = SplitLines(source);
        var filteredLines = new List<string>(lines.Length);
        var excludedProjects = new List<string>();
        var excludedProjectGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length;)
        {
            if (!TryParseProjectHeader(lines[i], out var projectName, out var projectPath, out var projectGuid))
            {
                filteredLines.Add(lines[i]);
                i++;
                continue;
            }

            var blockEnd = FindProjectBlockEnd(lines, i);
            if (!IsProjectFilePath(projectPath) || !filter.IsMatch(solutionDirectory, projectPath, projectName))
            {
                for (int blockLine = i; blockLine <= blockEnd; blockLine++)
                    filteredLines.Add(lines[blockLine]);

                i = blockEnd + 1;
                continue;
            }

            excludedProjects.Add(ResolveProjectPath(solutionDirectory, projectPath));
            excludedProjectGuids.Add(projectGuid);
            i = blockEnd + 1;
        }

        if (excludedProjectGuids.Count is 0)
            return new(source, []);

        filteredLines = filteredLines
            .AsValueEnumerable()
            .Where(line => !excludedProjectGuids.AsValueEnumerable().Any(guid => line.Contains(guid, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new(string.Join(Environment.NewLine, filteredLines), excludedProjects.ToArray());
    }

    static string[] SplitLines(string source)
        => source.ReplaceLineEndings("\n").Split('\n');

    static int FindProjectBlockEnd(string[] lines, int start)
    {
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), "EndProject", StringComparison.Ordinal))
                return i;
        }

        return start;
    }

    static bool TryParseProjectHeader(string line, out string projectName, out string projectPath, out string projectGuid)
    {
        projectName = "";
        projectPath = "";
        projectGuid = "";

        if (!line.StartsWith("Project(", StringComparison.Ordinal))
            return false;

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var fields = ReadQuotedFields(line[(equalsIndex + 1)..]);
        if (fields.Count < 3)
            return false;

        projectName = fields[0];
        projectPath = fields[1];
        projectGuid = fields[2];
        return true;
    }

    static List<string> ReadQuotedFields(string value)
    {
        var fields = new List<string>(3);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '"')
                continue;

            var builder = new StringBuilder();
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

    static bool IsProjectFilePath(string path)
        => Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase);

    static string ResolveProjectPath(string solutionDirectory, string projectPath)
        => Path.GetFullPath(Path.IsPathRooted(projectPath) ? projectPath : Path.Combine(solutionDirectory, projectPath));

    readonly record struct FilterResult(string Source, string[] ExcludedProjects);

    internal sealed class ProjectIgnoreFilter
    {
        readonly Regex[] patterns;

        ProjectIgnoreFilter(Regex[] patterns) => this.patterns = patterns;

        public bool IsEmpty => patterns.Length is 0;

        public static ProjectIgnoreFilter Load(string path)
        {
            var patterns = File.ReadLines(path, Encoding.UTF8)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Select(static line => new Regex(GlobToRegex(NormalizePattern(line)), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                .ToArray();

            return new(patterns);
        }

        public bool IsMatch(string solutionDirectory, string projectPath, string? projectName = null)
        {
            var normalizedProjectPath = NormalizePath(projectPath);
            var fullPath = Path.IsPathRooted(projectPath)
                ? Path.GetFullPath(projectPath)
                : Path.GetFullPath(Path.Combine(solutionDirectory, projectPath));

            var relativePath = NormalizePath(Path.GetRelativePath(solutionDirectory, fullPath));
            var fileName = Path.GetFileName(projectPath);
            var shortName = projectName ?? Path.GetFileNameWithoutExtension(projectPath);

            var candidates = new[]
            {
                normalizedProjectPath,
                relativePath,
                NormalizePath(fullPath),
                NormalizePath(fileName),
                NormalizePath(Path.GetFileNameWithoutExtension(projectPath)),
                NormalizePath(shortName),
            };

            return patterns.Any(pattern => candidates.Any(candidate => pattern.IsMatch(candidate)));
        }

        static string NormalizePattern(string pattern)
        {
            pattern = NormalizePath(pattern);
            return pattern.StartsWith("./", StringComparison.Ordinal) ? pattern[2..] : pattern;
        }

        static string NormalizePath(string path)
            => path.Replace('\\', '/').Trim();

        static string GlobToRegex(string pattern)
        {
            var builder = new StringBuilder(pattern.Length * 2);
            builder.Append('^');

            foreach (var character in pattern)
            {
                switch (character)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        builder.Append(Regex.Escape(character.ToString()));
                        break;
                }
            }

            builder.Append('$');
            return builder.ToString();
        }
    }
}

sealed class WorkspaceLoadTarget(
    string targetPath,
    string? temporaryPath = null,
    string? solutionDirectory = null,
    WorkspaceProjectFilter.ProjectIgnoreFilter? filter = null,
    string[]? excludedProjectPaths = null) : IDisposable
{
    readonly HashSet<string> excludedProjects = new(excludedProjectPaths ?? [], StringComparer.OrdinalIgnoreCase);

    public string LoadPath => temporaryPath ?? targetPath;

    public int ExcludedProjectCount => excludedProjects.Count;

    public Solution ApplyFilter(Solution solution)
    {
        if (filter is null || solutionDirectory is null)
            return solution;

        foreach (var project in solution.Projects.ToArray())
        {
            if (project.FilePath is not null && filter.IsMatch(solutionDirectory, project.FilePath, project.Name))
            {
                excludedProjects.Add(Path.GetFullPath(project.FilePath));
                solution = solution.RemoveProject(project.Id);
            }
        }

        return solution;
    }

    public void Dispose()
    {
        if (temporaryPath is null || !File.Exists(temporaryPath))
            return;

        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
