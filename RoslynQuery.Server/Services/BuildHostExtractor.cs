using System.Reflection;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

static class BuildHostExtractor
{
    const string versionMarkerFileName = ".roslynquery-buildhost-version";
    static readonly Lock gate = new();

    static readonly BuildHostSpec[] buildHosts =
    [
        new(
            "BuildHost-netcore",
            "RoslynQuery.BuildHostNetCore",
            [
                "Microsoft.Build.Locator.dll",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.deps.json",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.runtimeconfig.json",
                "System.Collections.Immutable.dll",
            ]
        ),
        new(
            "BuildHost-net472",
            "RoslynQuery.BuildHostNet472",
            [
                "Microsoft.Bcl.AsyncInterfaces.dll",
                "Microsoft.Build.Locator.dll",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config",
                "Microsoft.IO.Redist.dll",
                "System.Buffers.dll",
                "System.Collections.Immutable.dll",
                "System.IO.Pipelines.dll",
                "System.Memory.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Text.Encodings.Web.dll",
                "System.Text.Json.dll",
                "System.Threading.Tasks.Extensions.dll",
                "System.ValueTuple.dll",
            ],
            WindowsOnly: true
        ),
    ];

    static bool ensured;

    public static void EnsurePresent()
    {
        lock (gate)
        {
            if (ensured)
                return;

            EnsurePresentCore();
            ensured = true;
        }
    }

    static void EnsurePresentCore()
    {
        var version = typeof(MSBuildWorkspace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(MSBuildWorkspace).Assembly.GetName().Version?.ToString()
                      ?? "unknown";

        foreach (var buildHost in buildHosts)
            if (!buildHost.WindowsOnly || OperatingSystem.IsWindows())
                EnsureBuildHostPresent(buildHost, version);
    }

#pragma warning disable IL3000
    static string GetWorkspaceDirectory()
        => Path.GetDirectoryName(typeof(MSBuildWorkspace).Assembly.Location) ?? AppContext.BaseDirectory;
#pragma warning restore IL3000

    static void EnsureBuildHostPresent(BuildHostSpec buildHost, string version)
    {
        var directoryPath = Path.Combine(GetWorkspaceDirectory(), buildHost.DirectoryName);
        var versionPath = Path.Combine(directoryPath, versionMarkerFileName);

        if (File.Exists(versionPath)
            && File.ReadAllText(versionPath).AsSpan().Trim().SequenceEqual(version.AsSpan())
            && HasAllFiles(buildHost, directoryPath))
            return;

        try
        {
            Directory.CreateDirectory(directoryPath);

            foreach (var fileName in buildHost.FileNames)
                ExtractFile(buildHost, directoryPath, fileName);

            File.WriteAllText(versionPath, version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Unable to extract the Roslyn build host to '{directoryPath}'. The directory must be writable the first time a workspace is opened.",
                exception
            );
        }
    }

    static bool HasAllFiles(BuildHostSpec buildHost, string directoryPath)
    {
        foreach (var fileName in buildHost.FileNames)
            if (!File.Exists(Path.Combine(directoryPath, fileName)))
                return false;

        return true;
    }

    static void ExtractFile(BuildHostSpec buildHost, string directoryPath, string fileName)
    {
        var resourceName = $"{buildHost.ResourcePrefix}.{fileName}";
        using var input = typeof(BuildHostExtractor).Assembly.GetManifestResourceStream(resourceName)
                          ?? throw new InvalidOperationException($"Embedded build host resource '{resourceName}' was not found.");

        var destinationPath = Path.Combine(directoryPath, fileName);
        var temporaryPath = Path.Combine(directoryPath, $"{fileName}.{Environment.ProcessId}.tmp");
        try
        {
            using (var output = File.Create(temporaryPath))
                input.CopyTo(output);

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    readonly record struct BuildHostSpec(string DirectoryName, string ResourcePrefix, string[] FileNames, bool WindowsOnly = false);
}
