using static System.StringComparison;

namespace RoslynQuery;

static class WorkspaceTargetResolver
{
    public static WorkspaceTargetResolution Resolve(string path)
    {
        var fullPath = Path.GetFullPath(WorkspacePathNormalizer.Normalize(path));
        if (File.Exists(fullPath))
        {
            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".sln", OrdinalIgnoreCase) || extension.Equals(".slnx", OrdinalIgnoreCase))
                return new() { Success = true, TargetPath = fullPath, TargetKind = "solution" };

            return extension.Equals(".csproj", OrdinalIgnoreCase)
                ? new() { Success = true, TargetPath = fullPath, TargetKind = "project" }
                : new() { Error = $"Unsupported file type '{fullPath}'." };
        }

        if (Directory.Exists(fullPath))
        {
            var rootSolutions = EnumerateSolutions(fullPath, recurseSubdirectories: false);
            if (rootSolutions.Length == 1)
                return new() { Success = true, TargetPath = rootSolutions[0], TargetKind = "solution" };

            if (rootSolutions.Length > 1)
            {
                return new()
                {
                    Error = $"Directory '{fullPath}' contains multiple solution files.",
                    Candidates = rootSolutions,
                };
            }

            var solutions = EnumerateSolutions(fullPath);
            if (solutions.Length == 1)
                return new() { Success = true, TargetPath = solutions[0], TargetKind = "solution" };

            if (solutions.Length > 1)
            {
                return new()
                {
                    Error = $"Directory '{fullPath}' contains multiple solution files.",
                    Candidates = solutions,
                };
            }

            var projects = EnumerateCandidates(fullPath, ".csproj");
            Array.Sort(projects, StringComparer.OrdinalIgnoreCase);

            return projects.Length switch
            {
                1 => new() { Success = true, TargetPath = projects[0], TargetKind = "project" },
                > 1 => new()
                {
                    Error = $"Directory '{fullPath}' contains multiple project files and no unique solution.",
                    Candidates = projects,
                },
                _ => new() { Error = $"Directory '{fullPath}' does not contain a .sln, .slnx, or .csproj file." },
            };
        }

        return new()
        {
            Error = $"Path '{fullPath}' does not exist.",
        };
    }

    static string[] EnumerateSolutions(string root, bool recurseSubdirectories = true)
    {
        var solutions = EnumerateCandidates(root, ".sln", recurseSubdirectories)
            .AsValueEnumerable()
            .Concat(EnumerateCandidates(root, ".slnx", recurseSubdirectories).AsValueEnumerable())
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return solutions;
    }

    static string[] EnumerateCandidates(string root, string extension, bool recurseSubdirectories = true)
        => Directory.EnumerateFiles(
                path: root,
                searchPattern: "*" + extension,
                enumerationOptions: new EnumerationOptions
                {
                    RecurseSubdirectories = recurseSubdirectories,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                }
            )
            .AsValueEnumerable()
            .Where(static path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .AsValueEnumerable()
                .Any(static segment => segment is ".git" or "bin" or "obj" or "node_modules")
            )
            .ToArray();
}

readonly record struct WorkspaceTargetResolution()
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public string? TargetPath { get; init; }

    public string? TargetKind { get; init; }

    public string[] Candidates { get; init; } = [];
}
