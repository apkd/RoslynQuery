using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using static System.StringComparison;

namespace RoslynQuery;

static class MsBuildBootstrapper
{
    internal const string MSBuildPathOverrideEnvironmentVariable = "ROSLYNQUERY_MSBUILD_PATH";

    static readonly Lock gate = new();
    static MsBuildInstanceInfo registeredInstance;

    internal static MsBuildInstanceInfo RegisteredInstance => registeredInstance;

    internal static MsBuildInstanceInfo EnsureRegistered(string targetPath, string targetKind)
    {
        lock (gate)
        {
            if (MSBuildLocator.IsRegistered)
            {
                if (registeredInstance.IsEmpty)
                {
                    registeredInstance = new(
                        Name: "MSBuild",
                        Version: "",
                        MSBuildPath: "",
                        VisualStudioRootPath: "",
                        DiscoveryType: "AlreadyRegistered"
                    )
                    {
                        SelectionReason = "MSBuild was already registered before RoslynQuery selected an instance.",
                    };
                }

                BuildHostExtractor.EnsurePresent();
                return registeredInstance;
            }

            var selected = CreateOverrideInstance();
            VisualStudioInstance? selectedVisualStudioInstance = null;
            if (selected is null)
            {
                var instances = QueryInstances(targetPath);
                if (instances.Length == 0)
                {
                    MSBuildLocator.RegisterDefaults();
                    registeredInstance = new(
                        Name: "MSBuild",
                        Version: "",
                        MSBuildPath: "",
                        VisualStudioRootPath: "",
                        DiscoveryType: "RegisterDefaults"
                    )
                    {
                        SelectionReason = "Microsoft.Build.Locator.RegisterDefaults() was used because explicit instance discovery returned no results.",
                    };

                    BuildHostExtractor.EnsurePresent();
                    return registeredInstance;
                }

                selected = SelectBestInstance(instances.Select(ToInfo).ToArray(), targetPath, targetKind);
                selectedVisualStudioInstance = instances.First(instance => PathsEqual(instance.MSBuildPath, selected.Value.MSBuildPath));
            }

            if (selected.Value.IsOverride)
                MSBuildLocator.RegisterMSBuildPath(selected.Value.MSBuildPath);
            else
                MSBuildLocator.RegisterInstance(selectedVisualStudioInstance!);

            registeredInstance = selected.Value;
            BuildHostExtractor.EnsurePresent();
            return registeredInstance;
        }
    }

    internal static MsBuildInstanceInfo SelectBestInstance(IReadOnlyList<MsBuildInstanceInfo> instances, string targetPath, string targetKind)
    {
        if (instances.Count == 0)
            throw new InvalidOperationException("No MSBuild instances were found on this machine.");

        var requestedSdkVersion = FindGlobalJsonSdkVersion(targetPath);
        var isLegacyWorkspace = IsLegacyWorkspace(targetPath, targetKind);
        var best = instances[0];
        var bestScore = ScoreInstance(best, requestedSdkVersion, isLegacyWorkspace);

        for (var index = 1; index < instances.Count; index++)
        {
            var candidate = instances[index];
            var score = ScoreInstance(candidate, requestedSdkVersion, isLegacyWorkspace);
            if (score > bestScore || score == bestScore && CompareInstanceVersion(candidate, best) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best with
        {
            IsKnownLegacyRisk = IsKnownLegacyRisk(best),
            SelectionReason = BuildSelectionReason(best, requestedSdkVersion, isLegacyWorkspace),
        };
    }

    static MsBuildInstanceInfo? CreateOverrideInstance()
    {
        var configuredPath = Environment.GetEnvironmentVariable(MSBuildPathOverrideEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var path = NormalizeMSBuildPath(configuredPath);
        if (!File.Exists(Path.Combine(path, "Microsoft.Build.dll")))
            throw new InvalidOperationException(
                $"{MSBuildPathOverrideEnvironmentVariable} points to '{configuredPath}', but '{Path.Combine(path, "Microsoft.Build.dll")}' was not found."
            );

        return new(
            Name: "MSBuild override",
            Version: TryGetSdkVersion(path) ?? "",
            MSBuildPath: path,
            VisualStudioRootPath: "",
            DiscoveryType: "EnvironmentOverride"
        )
        {
            IsOverride = true,
            IsKnownLegacyRisk = IsKnownLegacyRiskPath(path),
            SelectionReason = $"{MSBuildPathOverrideEnvironmentVariable} was set.",
        };
    }

    static VisualStudioInstance[] QueryInstances(string targetPath)
    {
        try
        {
            var options = new VisualStudioInstanceQueryOptions
            {
                AllowAllDotnetLocations = true,
                AllowAllRuntimeVersions = true,
                DiscoveryTypes = DiscoveryType.DeveloperConsole | DiscoveryType.DotNetSdk | DiscoveryType.VisualStudioSetup,
                WorkingDirectory = GetWorkspaceDirectory(targetPath),
            };

            var instances = MSBuildLocator.QueryVisualStudioInstances(options).ToArray();
            if (instances.Length > 0)
                return instances;
        }
        catch
        {
        }

        return MSBuildLocator.QueryVisualStudioInstances().ToArray();
    }

    static string GetWorkspaceDirectory(string targetPath)
        => File.Exists(targetPath)
            ? Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory()
            : targetPath;

    static MsBuildInstanceInfo ToInfo(VisualStudioInstance instance)
        => new(
            instance.Name,
            instance.Version.ToString(),
            instance.MSBuildPath,
            instance.VisualStudioRootPath ?? "",
            instance.DiscoveryType.ToString()
        );

    static int ScoreInstance(MsBuildInstanceInfo instance, string? requestedSdkVersion, bool isLegacyWorkspace)
    {
        var score = ParseVersion(instance.Version).Major * 100 + ParseVersion(instance.Version).Minor;

        if (requestedSdkVersion is not null && string.Equals(TryGetSdkVersion(instance.MSBuildPath), requestedSdkVersion, OrdinalIgnoreCase))
            score += 100_000;

        if (isLegacyWorkspace)
        {
            if (IsKnownLegacyRisk(instance))
                score -= 10_000;

            if (IsVisualStudio(instance))
                score += 2_000;

            if (IsVisualStudioMajor(instance, 17))
                score += 5_000;

            if (IsDotNetSdk(instance))
            {
                score += 1_000;
                var sdkVersion = ParseVersion(TryGetSdkVersion(instance.MSBuildPath));
                if (sdkVersion.Major == 9)
                    score += 800;
                else if (sdkVersion.Major == 8)
                    score += 600;
                else if (sdkVersion is { Major: 10, Minor: 0, Build: < 200 })
                    score += 500;
            }
        }
        else
        {
            if (IsDotNetSdk(instance))
                score += 2_000;
            else if (IsVisualStudio(instance))
                score += 1_000;
        }

        return score;
    }

    static string BuildSelectionReason(MsBuildInstanceInfo instance, string? requestedSdkVersion, bool isLegacyWorkspace)
    {
        if (requestedSdkVersion is not null && string.Equals(TryGetSdkVersion(instance.MSBuildPath), requestedSdkVersion, OrdinalIgnoreCase))
            return $"Matched SDK version {requestedSdkVersion} from global.json.";

        if (isLegacyWorkspace)
        {
            if (IsKnownLegacyRisk(instance))
                return "No safer MSBuild instance was available for a legacy project shape.";

            return "Selected for legacy project compatibility.";
        }

        return "Selected from discovered MSBuild instances.";
    }

    static bool IsLegacyWorkspace(string targetPath, string targetKind)
    {
        try
        {
            if (string.Equals(targetKind, "project", Ordinal))
                return IsLegacyProject(targetPath);

            foreach (var projectPath in EnumerateSolutionProjects(targetPath).Take(128))
                if (IsLegacyProject(projectPath))
                    return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    static bool IsLegacyProject(string projectPath)
    {
        if (!File.Exists(projectPath))
            return false;

        var document = XDocument.Load(projectPath, LoadOptions.None);
        var root = document.Root;
        return root is not null
               && string.Equals(root.Name.LocalName, "Project", Ordinal)
               && root.Attribute("Sdk") is null
               && !root.Elements().Any(static element => string.Equals(element.Name.LocalName, "Sdk", Ordinal));
    }

    static IEnumerable<string> EnumerateSolutionProjects(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            yield break;

        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        if (Path.GetExtension(solutionPath).Equals(".slnx", OrdinalIgnoreCase))
        {
            foreach (var projectPath in EnumerateSlnxProjects(solutionPath, solutionDirectory))
                yield return projectPath;

            yield break;
        }

        foreach (var line in File.ReadLines(solutionPath))
        {
            var markerIndex = line.IndexOf(".csproj", OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;

            var start = line.LastIndexOf('"', markerIndex);
            var end = line.IndexOf('"', markerIndex);
            if (start < 0 || end <= start)
                continue;

            yield return GetFullProjectPath(line[(start + 1)..end], solutionDirectory);
        }
    }

    static IEnumerable<string> EnumerateSlnxProjects(string solutionPath, string solutionDirectory)
    {
        var document = XDocument.Load(solutionPath, LoadOptions.None);
        foreach (var element in document.Descendants().Where(static element => string.Equals(element.Name.LocalName, "Project", Ordinal)))
        {
            var path = element.Attribute("Path")?.Value;
            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".csproj", OrdinalIgnoreCase))
                yield return GetFullProjectPath(path, solutionDirectory);
        }
    }

    static string GetFullProjectPath(string projectPath, string solutionDirectory)
    {
        projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(projectPath)
            ? Path.GetFullPath(projectPath)
            : Path.GetFullPath(projectPath, solutionDirectory);
    }

    static string? FindGlobalJsonSdkVersion(string targetPath)
    {
        var directory = File.Exists(targetPath)
            ? Path.GetDirectoryName(targetPath)
            : targetPath;

        while (!string.IsNullOrEmpty(directory))
        {
            var globalJsonPath = Path.Combine(directory, "global.json");
            if (File.Exists(globalJsonPath))
                return ReadGlobalJsonSdkVersion(globalJsonPath);

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    static string? ReadGlobalJsonSdkVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("sdk", out var sdk)
                   && sdk.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    static bool IsKnownLegacyRisk(MsBuildInstanceInfo instance)
        => IsVisualStudioAtLeast(instance, 18) || IsKnownLegacyRiskPath(instance.MSBuildPath);

    static bool IsKnownLegacyRiskPath(string path)
    {
        var sdkVersion = ParseVersion(TryGetSdkVersion(path));
        return sdkVersion >= new Version(10, 0, 200);
    }

    static bool IsVisualStudio(MsBuildInstanceInfo instance)
        => instance.DiscoveryType.Contains("VisualStudio", OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(instance.VisualStudioRootPath);

    static bool IsDotNetSdk(MsBuildInstanceInfo instance)
        => instance.DiscoveryType.Contains("DotNetSdk", OrdinalIgnoreCase)
           || TryGetSdkVersion(instance.MSBuildPath) is not null;

    static bool IsVisualStudioMajor(MsBuildInstanceInfo instance, int major)
        => IsVisualStudio(instance) && ParseVersion(instance.Version).Major == major;

    static bool IsVisualStudioAtLeast(MsBuildInstanceInfo instance, int major)
        => IsVisualStudio(instance) && ParseVersion(instance.Version).Major >= major;

    static int CompareInstanceVersion(MsBuildInstanceInfo left, MsBuildInstanceInfo right)
    {
        var leftVersion = ParseVersion(TryGetSdkVersion(left.MSBuildPath) ?? left.Version);
        var rightVersion = ParseVersion(TryGetSdkVersion(right.MSBuildPath) ?? right.Version);
        return leftVersion.CompareTo(rightVersion);
    }

    static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new();

        var suffixIndex = value.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            value = value[..suffixIndex];

        return Version.TryParse(value, out var version) ? version : new();
    }

    static string? TryGetSdkVersion(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string marker = "/sdk/";
        var index = normalized.LastIndexOf(marker, OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = normalized.IndexOf('/', start);
        return end < 0 ? normalized[start..] : normalized[start..end];
    }

    static string NormalizeMSBuildPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;

        return Path.GetFullPath(path);
    }

    static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? OrdinalIgnoreCase
            : Ordinal;

        return string.Equals(left, right, comparison);
    }
}

readonly record struct MsBuildInstanceInfo(
    string Name,
    string Version,
    string MSBuildPath,
    string VisualStudioRootPath,
    string DiscoveryType
)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name)
                           && string.IsNullOrWhiteSpace(Version)
                           && string.IsNullOrWhiteSpace(MSBuildPath)
                           && string.IsNullOrWhiteSpace(DiscoveryType);

    public bool IsOverride { get; init; }

    public bool IsKnownLegacyRisk { get; init; }

    public string SelectionReason { get; init; } = "";
}
