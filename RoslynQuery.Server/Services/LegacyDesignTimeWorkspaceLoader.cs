using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace RoslynQuery;

static class LegacyDesignTimeWorkspaceLoader
{
    static readonly SemaphoreSlim buildGate = new(1, 1);

    static readonly ImmutableDictionary<string, string> designTimeProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = bool.TrueString,
            ["BuildingInsideVisualStudio"] = bool.TrueString,
            ["BuildProjectReferences"] = bool.FalseString,
            ["BuildingProject"] = bool.FalseString,
            ["ProvideCommandLineArgs"] = bool.TrueString,
            ["SkipCompilerExecution"] = bool.TrueString,
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = bool.FalseString,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static async Task<LegacyWorkspaceLoadResult> LoadAsync(
        string targetKind,
        WorkspaceLoadTarget loadTarget,
        CancellationToken ct)
    {
        var workspace = new AdhocWorkspace(MSBuildMefHostServices.DefaultServices);
        var projects = WorkspaceProjectDiscovery.EnumerateProjects(loadTarget.LoadPath, targetKind);
        var solutionPath = string.Equals(targetKind, "solution", StringComparison.Ordinal) ? loadTarget.LoadPath : null;

        var pendingLegacyProjects = new Queue<WorkspaceProjectEntry>(projects
            .AsValueEnumerable()
            .Where(static project => WorkspaceProjectDiscovery.IsLegacyCSharpProject(project.Path))
            .ToArray());

        var legacyProjectReferences = new Dictionary<string, LegacyProjectReference[]>(StringComparer.OrdinalIgnoreCase);
        var loadedLegacyProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pendingLegacyProjects.TryDequeue(out var project))
        {
            if (!loadedLegacyProjectPaths.Add(Path.GetFullPath(project.Path)))
                continue;

            var loaded = await LoadLegacyProjectAsync(project, solutionPath, workspace, ct);
            workspace.AddProject(loaded.ProjectInfo);
            legacyProjectReferences[Path.GetFullPath(project.Path)] = loaded.ProjectReferences;

            foreach (var reference in loaded.ProjectReferences)
                if (!loadedLegacyProjectPaths.Contains(reference.ProjectPath)
                    && File.Exists(reference.ProjectPath)
                    && WorkspaceProjectDiscovery.IsLegacyCSharpProject(reference.ProjectPath))
                    pendingLegacyProjects.Enqueue(new(Path.GetFileNameWithoutExtension(reference.ProjectPath), reference.ProjectPath));
        }

        if (string.Equals(targetKind, "solution", StringComparison.Ordinal))
            await LoadSdkProjectsAsync(projects, workspace, ct);

        var solution = ConnectProjectReferences(workspace.CurrentSolution, legacyProjectReferences);
        workspace.TryApplyChanges(solution);

        return new(workspace, workspace.CurrentSolution);
    }

    public static async Task<LegacyProjectLoadResult> LoadLegacyProjectAsync(
        WorkspaceProjectEntry project,
        string? solutionPath,
        Workspace workspace,
        CancellationToken ct)
        => await Task.Run(() => LoadLegacyProject(project, solutionPath, workspace, ct), ct);

    static LegacyProjectLoadResult LoadLegacyProject(WorkspaceProjectEntry project, string? solutionPath, Workspace workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var projectDirectory = Path.GetDirectoryName(project.Path)
                               ?? throw new InvalidOperationException($"Project '{project.Path}' does not have a directory.");

        var properties = CreateGlobalProperties(solutionPath);
        var projectCollection = new ProjectCollection(
            properties,
            loggers: [],
            ToolsetDefinitionLocations.Default
        );

        try
        {
            var loadSettings = ProjectLoadSettings.IgnoreMissingImports
                               | ProjectLoadSettings.RejectCircularImports
                               | ProjectLoadSettings.IgnoreEmptyImports
                               | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition
                               | ProjectLoadSettings.IgnoreInvalidImports
                               | ProjectLoadSettings.FailOnUnresolvedSdk;

            var evaluatedProject = new MSBuildProject(project.Path, properties, toolsVersion: null, projectCollection, loadSettings);
            var projectInstance = evaluatedProject.CreateProjectInstance();
            EnsureTargets(projectInstance);

            var logger = new DesignTimeBuildLogger();
            var targets = GetBuildTargets(projectInstance);
            var request = new BuildRequestData(projectInstance, targets);
            var parameters = new BuildParameters(projectCollection)
            {
                Loggers = [logger],
                LogTaskInputs = false,
            };

            buildGate.Wait(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = BuildManager.DefaultBuildManager.Build(parameters, request);
                if (result.OverallResult == BuildResultCode.Failure)
                    throw CreateBuildFailure(project.Path, logger, result.Exception);
            }
            finally
            {
                buildGate.Release();
            }

            var commandLineArgs = ReadCommandLineArgs(projectInstance);

            if (commandLineArgs.Length is 0)
                throw CreateMissingCommandLineFailure(project.Path, logger);

            var projectInfo = CreateProjectInfo(project.Name, project.Path, commandLineArgs, projectDirectory);

            return new(
                projectInfo,
                ReadProjectReferences(projectInstance, projectDirectory)
            );
        }
        finally
        {
            projectCollection.UnloadAllProjects();
            projectCollection.Dispose();
        }
    }

    static async Task LoadSdkProjectsAsync(WorkspaceProjectEntry[] projects, AdhocWorkspace workspace, CancellationToken ct)
    {
        var loadedProjectPaths = new HashSet<string>(
            workspace.CurrentSolution.Projects
                .AsValueEnumerable()
                .Select(static project => project.FilePath)
                .Where(static path => path is not null)
                .Select(static path => Path.GetFullPath(path!))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase
        );

        var loader = new MSBuildProjectLoader(workspace);
        foreach (var project in projects)
        {
            if (loadedProjectPaths.Contains(Path.GetFullPath(project.Path)))
                continue;

            if (WorkspaceProjectDiscovery.IsLegacyCSharpProject(project.Path))
                continue;

            var projectInfos = await loader.LoadProjectInfoAsync(
                project.Path,
                ProjectMap.Create(workspace.CurrentSolution),
                cancellationToken: ct
            );

            var newProjectInfos = projectInfos
                .AsValueEnumerable()
                .Where(info => info.FilePath is null || loadedProjectPaths.Add(Path.GetFullPath(info.FilePath)))
                .ToArray();

            if (newProjectInfos.Length > 0)
                workspace.AddProjects(newProjectInfos);
        }
    }

    static Solution ConnectProjectReferences(Solution solution, IReadOnlyDictionary<string, LegacyProjectReference[]> legacyProjectReferences)
    {
        foreach (var (projectPath, references) in legacyProjectReferences)
        {
            var sourceProject = solution.Projects.FirstOrDefault(candidate => PathsEqual(candidate.FilePath, projectPath));
            if (sourceProject is null)
                continue;

            var projectReferences = references
                .AsValueEnumerable()
                .Select(reference => TryCreateProjectReference(solution, reference))
                .Where(static reference => reference is not null)
                .Select(static reference => reference!)
                .ToArray();

            if (projectReferences.Length is 0)
                continue;

            solution = solution.WithProjectReferences(
                sourceProject.Id,
                sourceProject.ProjectReferences
                    .AsValueEnumerable()
                    .Concat(projectReferences)
                    .Distinct()
                    .ToArray()
            );

            solution = RemoveProjectMetadataReferences(solution, sourceProject.Id, projectReferences);
        }

        return solution;
    }

    static ProjectReference? TryCreateProjectReference(Solution solution, LegacyProjectReference reference)
    {
        var targetProject = solution.Projects.FirstOrDefault(project => PathsEqual(project.FilePath, reference.ProjectPath));
        return targetProject is null
            ? null
            : new ProjectReference(targetProject.Id, reference.Aliases, reference.EmbedInteropTypes);
    }

    static Solution RemoveProjectMetadataReferences(Solution solution, ProjectId sourceProjectId, ProjectReference[] projectReferences)
    {
        var sourceProject = solution.GetProject(sourceProjectId);
        if (sourceProject is null)
            return solution;

        var referencedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in projectReferences)
        {
            var targetProject = solution.GetProject(reference.ProjectId);
            if (!string.IsNullOrWhiteSpace(targetProject?.OutputFilePath))
                referencedOutputPaths.Add(Path.GetFullPath(targetProject.OutputFilePath));

            if (!string.IsNullOrWhiteSpace(targetProject?.OutputRefFilePath))
                referencedOutputPaths.Add(Path.GetFullPath(targetProject.OutputRefFilePath));
        }

        if (referencedOutputPaths.Count is 0)
            return solution;

        foreach (var metadataReference in sourceProject.MetadataReferences.ToArray())
            if (TryGetReferenceFullPath(metadataReference, out var filePath) && referencedOutputPaths.Contains(filePath))
                solution = solution.RemoveMetadataReference(sourceProjectId, metadataReference);

        return solution;
    }

    static Microsoft.CodeAnalysis.ProjectInfo CreateProjectInfo(
        string projectName,
        string projectPath,
        string[] commandLineArgs,
        string projectDirectory)
    {
        var arguments = CSharpCommandLineParser.Default.Parse(
            commandLineArgs,
            projectDirectory,
            RuntimeEnvironment.GetRuntimeDirectory(),
            additionalReferenceDirectories: null
        );

        ThrowIfCommandLineHasErrors(projectPath, arguments);

        var projectId = ProjectId.CreateNewId(projectName);
        var metadataResolver = new CommandLineMetadataReferenceResolver(arguments.BaseDirectory, arguments.ReferencePaths);
        var metadataReferences = arguments
            .ResolveMetadataReferences(metadataResolver)
            .Where(static reference => reference is not UnresolvedMetadataReference)
            .Distinct()
            .ToArray();

        var outputFileName = arguments.OutputFileName ?? projectName + ".dll";
        var outputFilePath = GetOutputFilePath(arguments, outputFileName);
        var assemblyName = arguments.CompilationName
                           ?? Path.GetFileNameWithoutExtension(outputFilePath)
                           ?? projectName;

        var projectInfo = Microsoft.CodeAnalysis.ProjectInfo
            .Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                assemblyName,
                LanguageNames.CSharp,
                filePath: projectPath,
                outputFilePath: outputFilePath,
                compilationOptions: arguments.CompilationOptions.WithMetadataReferenceResolver(metadataResolver),
                parseOptions: arguments.ParseOptions,
                documents: CreateDocumentInfos(projectId, arguments.SourceFiles, projectDirectory, arguments.Encoding, arguments.ChecksumAlgorithm),
                projectReferences: [],
                metadataReferences: metadataReferences,
                analyzerReferences: CreateAnalyzerReferences(arguments),
                additionalDocuments: CreateDocumentInfos(projectId, arguments.AdditionalFiles, projectDirectory, arguments.Encoding, arguments.ChecksumAlgorithm),
                isSubmission: false,
                hostObjectType: null
            )
            .WithAnalyzerConfigDocuments(CreateAnalyzerConfigDocumentInfos(projectId, arguments.AnalyzerConfigPaths, projectDirectory, arguments.Encoding, arguments.ChecksumAlgorithm));

        return string.IsNullOrWhiteSpace(arguments.OutputRefFilePath)
            ? projectInfo
            : projectInfo.WithOutputRefFilePath(arguments.OutputRefFilePath);
    }

    static void ThrowIfCommandLineHasErrors(string projectPath, CSharpCommandLineArguments arguments)
    {
        var errors = arguments.Errors
            .AsValueEnumerable()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();

        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Legacy design-time C# command line could not be parsed for '{projectPath}'.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}"
            );
    }

    static string GetOutputFilePath(CSharpCommandLineArguments arguments, string outputFileName)
    {
        try
        {
            return arguments.GetOutputFilePath(outputFileName);
        }
        catch (ArgumentException)
        {
            return Path.GetFullPath(outputFileName, arguments.OutputDirectory ?? arguments.BaseDirectory ?? Directory.GetCurrentDirectory());
        }
    }

    static DocumentInfo[] CreateDocumentInfos(
        ProjectId projectId,
        ImmutableArray<CommandLineSourceFile> files,
        string projectDirectory,
        Encoding? encoding,
        SourceHashAlgorithm checksumAlgorithm)
        => files
            .AsValueEnumerable()
            .Where(static file => !file.IsInputRedirected)
            .Select(file => CreateDocumentInfo(
                projectId,
                Path.GetFullPath(file.Path),
                projectDirectory,
                file.IsScript ? SourceCodeKind.Script : SourceCodeKind.Regular,
                encoding,
                checksumAlgorithm
            ))
            .ToArray();

    static DocumentInfo[] CreateAnalyzerConfigDocumentInfos(
        ProjectId projectId,
        ImmutableArray<string> paths,
        string projectDirectory,
        Encoding? encoding,
        SourceHashAlgorithm checksumAlgorithm)
        => paths
            .AsValueEnumerable()
            .Select(path => CreateDocumentInfo(
                projectId,
                Path.GetFullPath(path),
                projectDirectory,
                SourceCodeKind.Regular,
                encoding,
                checksumAlgorithm
            ))
            .ToArray();

    static DocumentInfo CreateDocumentInfo(
        ProjectId projectId,
        string filePath,
        string projectDirectory,
        SourceCodeKind sourceCodeKind,
        Encoding? encoding,
        SourceHashAlgorithm checksumAlgorithm)
        => DocumentInfo.Create(
            DocumentId.CreateNewId(projectId, filePath),
            Path.GetFileName(filePath),
            GetDocumentFolders(projectDirectory, filePath),
            sourceCodeKind,
            new SourceFileTextLoader(filePath, encoding, checksumAlgorithm),
            filePath
        );

    static string[] GetDocumentFolders(string projectDirectory, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return [];

        var relativePath = Path.GetRelativePath(projectDirectory, directory);
        if (relativePath is "." || relativePath.StartsWith("..", StringComparison.Ordinal))
            return [];

        return relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .AsValueEnumerable()
            .Where(static part => part is not ".")
            .ToArray();
    }

    static AnalyzerReference[] CreateAnalyzerReferences(CSharpCommandLineArguments arguments)
    {
        if (arguments.AnalyzerReferences.IsEmpty)
            return [];

        var loader = new SimpleAnalyzerAssemblyLoader();

        foreach (var analyzerReference in arguments.AnalyzerReferences)
            RegisterAnalyzerDependencies(loader, analyzerReference.FilePath);

        return arguments
            .ResolveAnalyzerReferences(loader)
            .ToArray();
    }

    static void RegisterAnalyzerDependencies(IAnalyzerAssemblyLoader loader, string analyzerPath)
    {
        if (string.IsNullOrWhiteSpace(analyzerPath) || !File.Exists(analyzerPath))
            return;

        var directory = Path.GetDirectoryName(analyzerPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
            loader.AddDependencyLocation(path);
    }

    static bool TryGetReferenceFullPath(MetadataReference reference, out string filePath)
    {
        var path = reference switch
        {
            PortableExecutableReference { FilePath: { } portablePath } => portablePath,
            UnresolvedMetadataReference { Reference: { } unresolvedPath } when LooksLikePath(unresolvedPath) => unresolvedPath,
            _ => null,
        };

        return TryGetFullPath(path, Directory.GetCurrentDirectory(), out filePath);
    }

    static bool TryGetFullPath(string? path, string baseDirectory, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, baseDirectory);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    static bool LooksLikePath(string value)
        => Path.IsPathRooted(value)
           || value.Contains(Path.DirectorySeparatorChar)
           || value.Contains(Path.AltDirectorySeparatorChar)
           || HasMetadataExtension(value);

    static bool HasMetadataExtension(string path)
        => Path.GetExtension(path) is { } extension
           && (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".netmodule", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".winmd", StringComparison.OrdinalIgnoreCase));

    static LegacyProjectReference[] ReadProjectReferences(ProjectInstance project, string projectDirectory)
    {
        var references = new List<LegacyProjectReference>();
        foreach (var item in project.GetItems("ProjectReference"))
        {
            if (IsFalse(item.GetMetadataValue("ReferenceOutputAssembly")))
                continue;

            var projectPath = item.GetMetadataValue("FullPath");
            if (string.IsNullOrWhiteSpace(projectPath))
                projectPath = Path.GetFullPath(item.EvaluatedInclude, projectDirectory);

            references.Add(
                new(
                    Path.GetFullPath(projectPath),
                    ParseAliases(item.GetMetadataValue("Aliases")),
                    IsTrue(item.GetMetadataValue("EmbedInteropTypes"))
                )
            );
        }

        return references.ToArray();
    }

    static string[] GetBuildTargets(ProjectInstance project)
    {
        var targets = new List<string>(3) { "Compile", "CoreCompile" };
        if (project.Targets.ContainsKey("DesignTimeMarkupCompilation"))
            targets.Add("DesignTimeMarkupCompilation");

        return targets.ToArray();
    }

    static string[] ReadCommandLineArgs(ProjectInstance project)
    {
        var result = new List<string>();
        foreach (var item in project.GetItems("CscCommandLineArgs"))
            if (!string.IsNullOrWhiteSpace(item.EvaluatedInclude))
                result.Add(item.EvaluatedInclude);

        return result.ToArray();
    }

    static void EnsureTargets(ProjectInstance project)
    {
        foreach (var target in new[] { "Compile", "CoreCompile" })
            if (!project.Targets.ContainsKey(target))
                throw new InvalidOperationException($"Legacy project '{project.FullPath}' does not contain the required '{target}' target.");
    }

    static ImmutableDictionary<string, string> CreateGlobalProperties(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
            return designTimeProperties;

        var solutionDirectory = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
            return designTimeProperties;

        return designTimeProperties.SetItem(
            "SolutionDir",
            solutionDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? solutionDirectory
                : solutionDirectory + Path.DirectorySeparatorChar
        );
    }

    static ImmutableArray<string> ParseAliases(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
            return ImmutableArray<string>.Empty;

        return value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .AsValueEnumerable()
            .Where(static alias => !string.Equals(alias, "global", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
    }

    static bool IsTrue(string value)
        => bool.TryParse(value, out var result) && result;

    static bool IsFalse(string value)
        => bool.TryParse(value, out var result) && !result;

    static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    static InvalidOperationException CreateBuildFailure(string projectPath, DesignTimeBuildLogger logger, Exception? exception)
        => new(
            $"Legacy design-time MSBuild failed for '{projectPath}'.{Environment.NewLine}{logger.FormatDiagnostics()}",
            exception
        );

    static InvalidOperationException CreateMissingCommandLineFailure(string projectPath, DesignTimeBuildLogger logger)
        => new(
            $"Legacy design-time MSBuild did not produce CscCommandLineArgs for '{projectPath}'.{Environment.NewLine}{logger.FormatDiagnostics()}"
        );

    sealed class DesignTimeBuildLogger : ILogger
    {
        readonly List<string> diagnostics = [];

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, args) =>
            {
                if (args.Message is { } message)
                    diagnostics.Add(message);
            };
            eventSource.WarningRaised += (_, args) =>
            {
                if (args.Message is { } message)
                    diagnostics.Add(message);
            };
        }

        public void Shutdown()
        {
        }

        public string FormatDiagnostics()
            => diagnostics.Count is 0
                ? "MSBuild did not report additional diagnostics."
                : string.Join(Environment.NewLine, diagnostics);
    }

    sealed class CommandLineMetadataReferenceResolver(string? baseDirectory, ImmutableArray<string> referencePaths)
        : MetadataReferenceResolver
    {
        readonly string baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(baseDirectory);

        readonly ImmutableArray<string> referencePaths = referencePaths.IsDefault
            ? []
            : referencePaths;

        public override bool ResolveMissingAssemblies => false;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(
            string reference,
            string? baseFilePath,
            MetadataReferenceProperties properties)
        {
            foreach (var path in EnumerateCandidatePaths(reference, baseFilePath))
                if (File.Exists(path))
                    return [MetadataReference.CreateFromFile(path, properties)];

            return [];
        }

        public override PortableExecutableReference? ResolveMissingAssembly(
            MetadataReference definition,
            AssemblyIdentity referenceIdentity)
            => null;

        public override bool Equals(object? other)
            => other is CommandLineMetadataReferenceResolver resolver
               && string.Equals(baseDirectory, resolver.baseDirectory, StringComparison.OrdinalIgnoreCase)
               && referencePaths.SequenceEqual(resolver.referencePaths, StringComparer.OrdinalIgnoreCase);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(baseDirectory, StringComparer.OrdinalIgnoreCase);
            foreach (var path in referencePaths)
                hash.Add(path, StringComparer.OrdinalIgnoreCase);

            return hash.ToHashCode();
        }

        IEnumerable<string> EnumerateCandidatePaths(string reference, string? baseFilePath)
        {
            if (string.IsNullOrWhiteSpace(reference))
                yield break;

            var localBaseDirectory = GetBaseDirectory(baseFilePath);
            if (TryGetFullPath(reference, localBaseDirectory, out var directPath))
                yield return directPath;

            foreach (var referencePath in referencePaths)
            {
                if (TryGetFullPath(reference, referencePath, out var candidatePath))
                    yield return candidatePath;

                if (!HasMetadataExtension(reference) && TryGetFullPath(reference + ".dll", referencePath, out var candidateAssemblyPath))
                    yield return candidateAssemblyPath;
            }

            if (!HasMetadataExtension(reference) && TryGetFullPath(reference + ".dll", localBaseDirectory, out var localAssemblyPath))
                yield return localAssemblyPath;
        }

        string GetBaseDirectory(string? baseFilePath)
        {
            if (string.IsNullOrWhiteSpace(baseFilePath))
                return baseDirectory;

            var directory = Path.GetDirectoryName(baseFilePath);
            return string.IsNullOrWhiteSpace(directory) ? baseDirectory : directory;
        }
    }

    sealed class SourceFileTextLoader(string filePath, Encoding? encoding, SourceHashAlgorithm checksumAlgorithm) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
            => Task.FromResult(LoadTextAndVersion(cancellationToken));

        TextAndVersion LoadTextAndVersion(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            var text = SourceText.From(stream, encoding, checksumAlgorithm, throwIfBinaryDetected: false, canBeEmbedded: true);
            return TextAndVersion.Create(text, VersionStamp.Create(File.GetLastWriteTimeUtc(filePath)), filePath);
        }
    }

    sealed class SimpleAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        readonly HashSet<string> dependencyLocations = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath)
        {
            if (!string.IsNullOrWhiteSpace(fullPath))
                dependencyLocations.Add(fullPath);
        }

        public Assembly LoadFromPath(string fullPath)
        {
            foreach (var dependency in dependencyLocations)
                if (PathsEqual(dependency, fullPath))
                    return Assembly.LoadFrom(dependency);

            return Assembly.LoadFrom(fullPath);
        }
    }
}

readonly record struct LegacyWorkspaceLoadResult(AdhocWorkspace Workspace, Solution Solution);

readonly record struct LegacyProjectLoadResult(
    Microsoft.CodeAnalysis.ProjectInfo ProjectInfo,
    LegacyProjectReference[] ProjectReferences);

readonly record struct LegacyProjectReference(
    string ProjectPath,
    ImmutableArray<string> Aliases,
    bool EmbedInteropTypes);
