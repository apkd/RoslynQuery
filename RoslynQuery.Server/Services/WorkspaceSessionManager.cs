using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace RoslynQuery;

public sealed class WorkspaceSessionManager
{
    static readonly string[] defaultRelations =
    [
        "base_types",
        "implemented_interfaces",
        "derived_types",
        "implementations",
        "overrides",
        "overridden_members",
        "containing_symbol",
    ];

    static readonly string[] describeRelations =
    [
        "base_types",
        "implemented_interfaces",
        "derived_types",
        "implementations",
        "overrides",
        "overridden_members",
    ];

    readonly SemaphoreSlim gate = new(1, 1);
    readonly ILogger<WorkspaceSessionManager> log;
    WorkspaceSession? activeSession;

    public WorkspaceSessionManager(ILogger<WorkspaceSessionManager> logger)
        => log = logger;

    public async Task<WorkspaceStatusResponse> StatusAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await BuildStatusAsync(activeSession, ct, includeDiagnosticCounts: true, includeProjects: false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ShowDiagnosticsResponse> ShowDiagnosticsAsync(string? verbosity, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            string normalizedVerbosity = verbosity?.Trim().ToLowerInvariant() switch
            {
                null or "" or "all" or "full" or "verbose" => "all",
                "warning" or "warnings" or "warn"          => "warnings",
                "error" or "errors"                        => "errors",
                var value                                  => value,
            };

            if (activeSession is null)
                return new() { Verbosity = normalizedVerbosity, Error = "No workspace is open." };

            if (normalizedVerbosity is not ("all" or "warnings" or "errors"))
            {
                return new()
                {
                    Verbosity = verbosity ?? "",
                    Error = "Invalid verbosity. Allowed values: all, warnings, errors.",
                };
            }

            var diagnostics = await activeSession.GetDiagnosticsAsync(ct);
            return new()
            {
                Success = true,
                TargetPath = WorkspacePathNormalizer.Format(activeSession.TargetPath, activeSession.PathStyle),
                Verbosity = normalizedVerbosity,
                Diagnostics = FilterDiagnostics(diagnostics, normalizedVerbosity),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OpenWorkspaceResponse> LoadAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new()
            {
                RequestedPath = path ?? "",
                Error = "Path is required.",
                Status = await BuildStatusAsync(activeSession, ct),
            };
        }

        await gate.WaitAsync(ct);
        try
        {
            var outputPathStyle = WorkspacePathNormalizer.DetectStyle(path);
            var resolution = WorkspaceTargetResolver.Resolve(path);
            if (!resolution.Success)
            {
                return new()
                {
                    RequestedPath = path,
                    Error = resolution.Error,
                    Candidates = FormatPaths(resolution.Candidates, outputPathStyle),
                    Status = await BuildStatusAsync(activeSession, ct),
                };
            }

            var session = await LoadSessionAsync(resolution.TargetPath!, resolution.TargetKind!, outputPathStyle, ct);
            activeSession?.Dispose();
            activeSession = session;

            var response = new OpenWorkspaceResponse
            {
                Success = true,
                RequestedPath = path,
                ResolvedPath = WorkspacePathNormalizer.Format(session.TargetPath, session.PathStyle),
                TargetKind = session.TargetKind,
                Status = await BuildStatusAsync(session, ct, includeDocuments: false, includeDiagnostics: false, includeProjects: false),
            };

            session.StartIndexBuild();
            return response;
        }
        catch (Exception exception)
        {
            log.ZLogError($"Failed to load workspace '{path}'.", exception);
            return new()
            {
                RequestedPath = path,
                Error = FormatWorkspaceLoadError(exception),
                Status = await BuildStatusAsync(activeSession, ct),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkspaceInitializationBenchmarkResponse> BenchmarkInitializationAsync(string path, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var load = await LoadAsync(path, ct);
        var loadElapsed = stopwatch.Elapsed;
        if (!load.Success)
        {
            stopwatch.Stop();
            return new()
            {
                Success = false,
                Error = load.Error,
                RequestedPath = path,
                ResolvedPath = load.ResolvedPath,
                TargetKind = load.TargetKind,
                ProjectCount = load.Status.ProjectCount,
                LoadDurationMs = loadElapsed.TotalMilliseconds,
                TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }

        WorkspaceSession? session;
        await gate.WaitAsync(ct);
        try
        {
            session = activeSession;
        }
        finally
        {
            gate.Release();
        }

        if (session is null)
        {
            stopwatch.Stop();
            return new()
            {
                Success = false,
                Error = "No workspace is open after benchmark load.",
                RequestedPath = path,
                ResolvedPath = load.ResolvedPath,
                TargetKind = load.TargetKind,
                ProjectCount = load.Status.ProjectCount,
                LoadDurationMs = loadElapsed.TotalMilliseconds,
                TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }

        var index = await session.GetIndexAsync(ct);
        stopwatch.Stop();

        return new()
        {
            Success = true,
            RequestedPath = path,
            ResolvedPath = load.ResolvedPath,
            TargetKind = load.TargetKind,
            ProjectCount = load.Status.ProjectCount,
            LoadDurationMs = loadElapsed.TotalMilliseconds,
            IndexWaitDurationMs = (stopwatch.Elapsed - loadElapsed).TotalMilliseconds,
            TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Index = index.BuildMetrics,
        };
    }

    public async Task<DescribeSymbolResponse> DescribeSymbolAsync(string symbol, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = await ResolveSymbolAsync(activeSession, index, symbol, kindFilter: null, ct);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var resolved = resolution.Resolved!.Value;
            var entry = resolved.Entry;
            var pathStyle = activeSession.PathStyle;
            var detail = SymbolFactory.ToDetail(resolved, index);
            var references = await CollectReferencesAsync(activeSession, resolved, pathStyle, ct);
            var related = await CollectRelatedSymbolsAsync(activeSession, index, resolved, describeRelations, pathStyle, ct);
            var overloads = CollectOverloads(index, activeSession.Solution, resolved, pathStyle);
            var diagnostics = await CollectSymbolDiagnosticsAsync(activeSession, index, resolved, pathStyle, ct);
            return new()
            {
                Success = true,
                Query = symbol,
                Symbol = ApplyPathStyle(in detail, pathStyle),
                Usages = SummarizeReferences(references),
                Relations = related,
                Overloads = overloads,
                TypeMembers = resolved.Symbol is INamedTypeSymbol typeSymbol ? CollectTypeMemberSummary(typeSymbol) : null,
                Diagnostics = diagnostics,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ListTypeMembersResponse> ListTypeMembersAsync(string symbol, bool includeInherited, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = await ResolveSymbolAsync(activeSession, index, symbol, kindFilter: "type", ct);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var resolved = resolution.Resolved!.Value;
            if (resolved.Symbol is not INamedTypeSymbol typeSymbol)
                return new() { Query = symbol, Error = "The resolved symbol is not a type." };

            var pathStyle = activeSession.PathStyle;
            var typeSummary = index.ToSummary(resolved);
            var members = CollectTypeMembers(index, activeSession.Solution, typeSymbol, includeInherited)
                .AsValueEnumerable()
                .Select(item =>
                    {
                        var summary = index.ToSummary(item);
                        return ApplyPathStyle(in summary, pathStyle);
                    }
                )
                .ToArray();

            return new()
            {
                Success = true,
                Query = symbol,
                Symbol = ApplyPathStyle(in typeSummary, pathStyle),
                Members = members,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FindUsagesResponse> FindUsagesAsync(string symbol, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = await ResolveSymbolAsync(activeSession, index, symbol, kindFilter: null, ct);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var resolved = resolution.Resolved!.Value;
            var references = await CollectReferencesAsync(activeSession, resolved, activeSession.PathStyle, ct);
            var symbolSummary = index.ToSummary(resolved);
            return new()
            {
                Success = true,
                Query = symbol,
                Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                References = references,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FindRelatedSymbolsResponse> FindRelatedSymbolsAsync(string symbol, string[]? relations, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = await ResolveSymbolAsync(activeSession, index, symbol, kindFilter: null, ct);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var resolved = resolution.Resolved!.Value;
            var related = await CollectRelatedSymbolsAsync(activeSession, index, resolved, relations, activeSession.PathStyle, ct);
            var symbolSummary = index.ToSummary(resolved);
            return new()
            {
                Success = true,
                Query = symbol,
                Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                Relations = related,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ViewIlResponse> ViewIlAsync(string symbol, CancellationToken ct, bool compact = true)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = await ResolveSymbolAsync(activeSession, index, symbol, kindFilter: null, ct);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var resolved = resolution.Resolved!.Value;
            var entry = resolved.Entry;
            var symbolSummary = index.ToSummary(resolved);
            if (resolved.Symbol is not IMethodSymbol and not IPropertySymbol)
            {
                return new()
                {
                    Query = symbol,
                    Error = "IL can only be viewed for methods and properties.",
                    Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                };
            }

            if (string.Equals(entry.Origin, "metadata", StringComparison.Ordinal))
            {
                var metadataResult = IlViewer.ViewMetadata(resolved, index.GetAssemblyPath(entry), compact, ct, LogViewIlException);
                return new()
                {
                    Success = metadataResult.Success,
                    Error = metadataResult.Error,
                    Message = metadataResult.Message,
                    Query = symbol,
                    Candidates = metadataResult.Candidates,
                    Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                    EmitDiagnostics = metadataResult.EmitDiagnostics,
                    Methods = metadataResult.Methods,
                };
            }

            var project = index.GetProject(activeSession.Solution, entry);
            if (project is null)
            {
                return new()
                {
                    Query = symbol,
                    Error = "The symbol's project is no longer available in the active workspace.",
                    Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                };
            }

            var result = await IlViewer.ViewAsync(project, resolved.Symbol, compact, ct, LogViewIlException);
            return new()
            {
                Success = result.Success,
                Error = result.Error,
                Message = result.Message,
                Query = symbol,
                Candidates = result.Candidates,
                Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                EmitDiagnostics = result.EmitDiagnostics,
                Methods = result.Methods,
            };

            void LogViewIlException(Exception exception)
                => log.ZLogError($"view_il failed while processing '{symbol}'.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<WorkspaceSession> LoadSessionAsync(string path, string targetKind, WorkspacePathStyle pathStyle, CancellationToken ct)
    {
        MsBuildBootstrapper.EnsureRegistered();

        var workspace = MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = true;
        var messages = new WorkspaceMessageBuffer();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, args) => messages.Add(args.Diagnostic);
#pragma warning restore CS0618

        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var loadTarget = WorkspaceProjectFilter.Create(path, targetKind);

        Solution solution;
        if (string.Equals(targetKind, "project", StringComparison.Ordinal))
            solution = (await workspace.OpenProjectAsync(path, cancellationToken: ct)).Solution;
        else
            solution = await workspace.OpenSolutionAsync(loadTarget.LoadPath, cancellationToken: ct);

        solution = loadTarget.ApplyFilter(solution);

        stopwatch.Stop();

        return new(
            workspace,
            solution,
            path,
            targetKind,
            pathStyle,
            startedAtUtc,
            stopwatch.Elapsed,
            loadTarget.ExcludedProjectCount,
            messages
        );
    }

    static async Task<WorkspaceStatusResponse> BuildStatusAsync(
        WorkspaceSession? session,
        CancellationToken ct,
        bool includeDocuments = true,
        bool includeDiagnostics = false,
        bool includeDiagnosticCounts = false,
        bool includeProjects = true
    )
    {
        if (session is null)
            return new() { IsBound = false };

        var projectCount = CountCSharpProjects(session);
        var projects = includeProjects ? BuildProjectInfos(session, includeDocuments) : [];
        var diagnostics = includeDiagnostics || includeDiagnosticCounts
            ? await session.GetDiagnosticsAsync(ct)
            : [];
        var diagnosticCounts = CountDiagnostics(diagnostics);
        return new()
        {
            IsBound = true,
            TargetPath = WorkspacePathNormalizer.Format(session.TargetPath, session.PathStyle),
            TargetKind = session.TargetKind,
            ProjectCount = projectCount,
            ExcludedProjectCount = session.ExcludedProjectCount,
            ErrorCount = diagnosticCounts.Errors,
            WarningCount = diagnosticCounts.Warnings,
            OtherDiagnosticCount = diagnosticCounts.Other,
            LoadedAtUtc = session.LoadedAtUtc,
            LastLoadDurationMs = session.LoadDuration.TotalMilliseconds,
            Messages = session.Messages.ToArray(),
            Projects = projects,
            Diagnostics = includeDiagnostics ? diagnostics : [],
        };
    }

    static (int Errors, int Warnings, int Other) CountDiagnostics(DiagnosticInfo[] diagnostics)
    {
        var errors = 0;
        var warnings = 0;
        var other = 0;
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity)
            {
                case "error":
                    errors++;
                    break;
                case "warning":
                    warnings++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return (errors, warnings, other);
    }

    static int CountCSharpProjects(WorkspaceSession session)
        => session.Solution.Projects
            .AsValueEnumerable()
            .Count(static project => project.Language == LanguageNames.CSharp);

    static ProjectInfo[] BuildProjectInfos(WorkspaceSession session, bool includeDocuments)
    {
        var projects = session.Solution.Projects
            .AsValueEnumerable()
            .Where(static project => project.Language == LanguageNames.CSharp)
            .ToArray();

        Array.Sort(projects, static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

        var result = new ProjectInfo[projects.Length];
        for (int i = 0; i < projects.Length; i++)
        {
            var project = projects[i];
            var documents = includeDocuments
                ? EnumerateBrowsableDocuments(project)
                    .AsValueEnumerable()
                    .Select(document => WorkspacePathNormalizer.Format(document.FilePath!, session.PathStyle)!)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

            var documentCount = includeDocuments
                ? documents.Length
                : EnumerateBrowsableDocuments(project).Length;

            result[i] = new()
            {
                Name = project.Name,
                ProjectPath = WorkspacePathNormalizer.Format(project.FilePath, session.PathStyle),
                Language = project.Language,
                TargetFramework = ResolveTargetFramework(project),
                DocumentCount = documentCount,
                Documents = documents,
            };
        }

        return result;
    }

    static async Task<ResolvedSymbolResolution> ResolveSymbolAsync(
        WorkspaceSession session,
        WorkspaceSymbolIndex index,
        string symbol,
        string? kindFilter,
        CancellationToken ct
    )
    {
        var sourceResolution = index.Resolve(symbol, kindFilter);
        if (sourceResolution.Success)
        {
            var resolved = await index.ResolveAsync(session.Solution, sourceResolution.Entry!.Value, ct);
            return resolved is { } value
                ? ResolvedSymbolResolution.Found(value)
                : ResolvedSymbolResolution.NotFound($"'{symbol}' resolved to a source symbol, but the declaration is no longer available.");
        }

        if (sourceResolution.Candidates.Length > 0)
            return ResolvedSymbolResolution.Ambiguous(sourceResolution.Error ?? $"'{symbol}' is ambiguous.", sourceResolution.Candidates);

        var metadataResolution = await index.ExternalMetadata.ResolveAsync(session.Solution, symbol, kindFilter, ct);
        return metadataResolution.Success || metadataResolution.Candidates.Length > 0
            ? metadataResolution
            : ResolvedSymbolResolution.NotFound(sourceResolution.Error ?? $"'{symbol}' did not resolve to a symbol.");
    }

    static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(left, right, comparison);
    }

    static string FormatWorkspaceLoadError(Exception exception)
        => exception.ToString().Contains("BuildHost-net472", StringComparison.OrdinalIgnoreCase)
            ? "Legacy .NET Framework project loading failed because the bundled BuildHost-net472 files were unavailable."
            : exception.Message;

    static DiagnosticInfo[] FilterDiagnostics(DiagnosticInfo[] diagnostics, string verbosity)
        => verbosity switch
        {
            "errors" => diagnostics
                .AsValueEnumerable()
                .Where(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                .ToArray(),
            "warnings" => diagnostics
                .AsValueEnumerable()
                .Where(static diagnostic =>
                    string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal)
                    || string.Equals(diagnostic.Severity, "warning", StringComparison.Ordinal)
                )
                .ToArray(),
            _ => diagnostics,
        };

    internal static Document[] EnumerateBrowsableDocuments(Project project)
        => project.Documents
            .AsValueEnumerable()
            .Where(static document =>
                document is { SourceCodeKind: SourceCodeKind.Regular, FilePath: not null }
                && IsBrowsablePath(document.FilePath)
            )
            .ToArray();

    static bool IsBrowsablePath(string path)
        => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .AsValueEnumerable()
            .Any(static segment => segment is ".git" or "bin" or "obj" or "node_modules" or ".nuget" or "packages");

    internal static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    static string? ResolveTargetFramework(Project project)
    {
        var inferred = InferTargetFramework(project.ParseOptions?.PreprocessorSymbolNames);
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return inferred;

        try
        {
            var root = XDocument.Load(project.FilePath).Root;
            if (root is null)
                return inferred;

            var targetFramework = FindProjectProperty(root, "TargetFramework");
            if (targetFramework is { } rawTargetFramework)
            {
                var trimmedTargetFramework = rawTargetFramework.AsSpan().Trim();
                if (trimmedTargetFramework.Length > 0)
                    return trimmedTargetFramework.Length == rawTargetFramework.Length ? rawTargetFramework : trimmedTargetFramework.ToString();
            }

            var targetFrameworks = FindProjectProperty(root, "TargetFrameworks");
            if (string.IsNullOrWhiteSpace(targetFrameworks))
                return inferred;

            var candidates = targetFrameworks.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (inferred is not null)
                return candidates.AsValueEnumerable().FirstOrDefault(candidate => string.Equals(candidate, inferred, StringComparison.OrdinalIgnoreCase)) ?? inferred;

            return candidates.AsValueEnumerable().FirstOrDefault();
        }
        catch
        {
            return inferred;
        }
    }

    static string? InferTargetFramework(IEnumerable<string>? symbols)
    {
        if (symbols is null)
            return null;

        foreach (var symbol in symbols)
        {
            var framework = TryConvertFrameworkSymbol(symbol);
            if (framework is not null)
                return framework;
        }

        return null;
    }

    static string? TryConvertFrameworkSymbol(string symbol)
    {
        if (symbol.EndsWith("_OR_GREATER", StringComparison.OrdinalIgnoreCase))
            return null;

        return symbol.ToUpperInvariant() switch
        {
            _ when symbol.StartsWith("NETSTANDARD", StringComparison.OrdinalIgnoreCase) => ConvertFrameworkSymbol(symbol, "NETSTANDARD", "netstandard"),
            _ when symbol.StartsWith("NETCOREAPP", StringComparison.OrdinalIgnoreCase)  => ConvertFrameworkSymbol(symbol, "NETCOREAPP", "netcoreapp"),
            _ when symbol.StartsWith("NET", StringComparison.OrdinalIgnoreCase)         => ConvertFrameworkSymbol(symbol, "NET", "net"),
            _                                                                           => null,
        };
    }

    static string? ConvertFrameworkSymbol(string symbol, string symbolPrefix, string tfmPrefix)
    {
        var suffix = symbol[symbolPrefix.Length..];
        if (suffix.Length == 0)
            return null;

        if (!suffix.Contains('_'))
            return IsDigits(suffix) ? $"{tfmPrefix}{suffix.ToLowerInvariant()}" : null;

        var parts = suffix.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsDigits(parts[0]) || !IsDigits(parts[1]))
            return null;

        var framework = $"{tfmPrefix}{parts[0]}.{parts[1]}";
        if (parts.Length == 2)
            return framework;

        var platform = parts[2].ToLowerInvariant();
        var platformVersion = string.Join('.', parts[3..]);
        return platformVersion.Length == 0
            ? $"{framework}-{platform}"
            : $"{framework}-{platform}{platformVersion}";
    }

    internal static string NormalizeSeverity(DiagnosticSeverity severity)
        => severity switch
        {
            DiagnosticSeverity.Hidden  => "hidden",
            DiagnosticSeverity.Info    => "info",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Error   => "error",
            _                          => severity.ToString().ToLowerInvariant(),
        };

    static ResolvedSymbol[] CollectTypeMembers(WorkspaceSymbolIndex index, Solution solution, INamedTypeSymbol typeSymbol, bool includeInherited)
    {
        var memberSymbols = new List<ISymbol>();
        AddDeclared(typeSymbol);

        if (includeInherited)
        {
            for (var current = typeSymbol.BaseType; current is not null; current = current.BaseType)
                AddDeclared(current);

            foreach (var interfaceType in typeSymbol.AllInterfaces)
                AddDeclared(interfaceType);
        }

        return memberSymbols
            .AsValueEnumerable()
            .Distinct(SymbolEqualityComparer.Default)
            .Select(symbol => index.CreateResolvedSymbol(solution, symbol))
            .OrderBy(static resolved => resolved.Entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static resolved => resolved.Entry.DisplaySignature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void AddDeclared(INamedTypeSymbol namedType)
        {
            foreach (var member in namedType.GetMembers())
            {
                if (!WorkspaceSymbolIndex.ShouldIndexMember(member))
                    continue;

                memberSymbols.Add(member);
            }
        }
    }

    static TypeMemberSummary CollectTypeMemberSummary(INamedTypeSymbol typeSymbol)
    {
        var totalCount = 0;
        var publicCount = 0;
        var constructorCount = 0;
        var methodCount = 0;
        var propertyCount = 0;
        var fieldCount = 0;
        var eventCount = 0;
        var nestedTypeCount = 0;
        var otherCount = 0;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            var counted = true;
            switch (member)
            {
                case INamedTypeSymbol:
                    nestedTypeCount++;
                    break;

                case IMethodSymbol method:
                    switch (method.MethodKind)
                    {
                        case MethodKind.Constructor or MethodKind.StaticConstructor:
                            constructorCount++;
                            break;
                        case MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise:
                            counted = false;
                            break;
                        default:
                            methodCount++;
                            break;
                    }

                    break;

                case IPropertySymbol:
                    propertyCount++;
                    break;

                case IFieldSymbol:
                    fieldCount++;
                    break;

                case IEventSymbol:
                    eventCount++;
                    break;

                default:
                    otherCount++;
                    break;
            }

            if (!counted)
                continue;

            totalCount++;
            if (member.DeclaredAccessibility == Accessibility.Public)
                publicCount++;
        }

        return new()
        {
            TotalCount = totalCount,
            PublicCount = publicCount,
            ConstructorCount = constructorCount,
            MethodCount = methodCount,
            PropertyCount = propertyCount,
            FieldCount = fieldCount,
            EventCount = eventCount,
            NestedTypeCount = nestedTypeCount,
            OtherCount = otherCount,
        };
    }

    static SymbolSummary[] CollectOverloads(WorkspaceSymbolIndex index, Solution solution, ResolvedSymbol resolved, WorkspacePathStyle pathStyle)
    {
        if (resolved.Symbol is not IMethodSymbol { ContainingType: not null } method)
            return [];

        var overloads = method.ContainingType
            .GetMembers(method.Name)
            .AsValueEnumerable()
            .OfType<IMethodSymbol>()
            .Where(candidate =>
                candidate.MethodKind == method.MethodKind
                && !candidate.IsImplicitlyDeclared
                && WorkspaceSymbolIndex.ShouldIndexMember(candidate)
            )
            .Cast<ISymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Select(symbol => index.CreateResolvedSymbol(solution, symbol))
            .OrderBy(static overload => overload.Entry.DisplaySignature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (overloads.Length <= 1)
            return [];

        return overloads
            .AsValueEnumerable()
            .Select(overload =>
                {
                    var summary = index.ToSummary(overload);
                    return ApplyPathStyle(in summary, pathStyle);
                }
            )
            .ToArray();
    }

    static ReferenceSummary SummarizeReferences(ReferenceInfo[] references)
        => new()
        {
            Count = references.Length,
            Projects = references
                .AsValueEnumerable()
                .GroupBy(static reference => reference.Project)
                .Select(static group => new ProjectReferenceCount
                    {
                        Project = group.Key,
                        Count = group.Count(),
                    }
                )
                .OrderBy(static item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Examples = references.AsValueEnumerable().Take(3).ToArray(),
        };

    static async Task<ReferenceInfo[]> CollectReferencesAsync(WorkspaceSession session, ResolvedSymbol resolved, WorkspacePathStyle pathStyle, CancellationToken ct)
    {
        var references = await SymbolFinder.FindReferencesAsync(resolved.Symbol, session.Solution, cancellationToken: ct);
        var textCache = new Dictionary<DocumentId, SourceText>();
        var items = new List<ReferenceInfo>();

        foreach (var referencedSymbol in references)
        foreach (var location in referencedSymbol.Locations)
        {
            if (!location.Location.IsInSource || location.Document is null)
                continue;

            if (!textCache.TryGetValue(location.Document.Id, out var text))
            {
                text = await location.Document.GetTextAsync(ct);
                textCache[location.Document.Id] = text;
            }

            var lineSpan = location.Location.GetLineSpan();
            var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().AsSpan().Trim();
            var trimmedLineText = lineText.Length == 0 ? string.Empty : lineText.ToString();
            items.Add(
                new()
                {
                    Project = location.Document.Project.Name,
                    DocumentPath = WorkspacePathNormalizer.Format(location.Document.FilePath, pathStyle),
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    EndColumn = lineSpan.EndLinePosition.Character + 1,
                    LineText = trimmedLineText,
                }
            );
        }

        return items
            .AsValueEnumerable()
            .OrderBy(static item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Line)
            .ThenBy(static item => item.Column)
            .ToArray();
    }

    static async Task<DiagnosticInfo[]> CollectSymbolDiagnosticsAsync(WorkspaceSession session, WorkspaceSymbolIndex index, ResolvedSymbol resolved, WorkspacePathStyle pathStyle, CancellationToken ct)
    {
        if (resolved.Symbol.DeclaringSyntaxReferences.Length is 0)
            return [];

        var project = index.GetProject(session.Solution, resolved.Entry);
        if (project is null)
            return [];

        var declaringSpans = new List<(SyntaxTree Tree, TextSpan Span)>();
        foreach (var syntaxReference in resolved.Symbol.DeclaringSyntaxReferences)
        {
            var syntax = await syntaxReference.GetSyntaxAsync(ct);
            declaringSpans.Add((syntaxReference.SyntaxTree, syntax.FullSpan));
        }

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return [];

        var diagnostics = new List<DiagnosticInfo>();
        foreach (var diagnostic in compilation.GetDiagnostics(ct))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden || !diagnostic.Location.IsInSource)
                continue;

            if (!IsInsideDeclaringSyntax(diagnostic.Location, declaringSpans))
                continue;

            var location = SymbolFactory.ToLocation(diagnostic.Location, lineText: null);
            diagnostics.Add(
                new()
                {
                    Project = project.Name,
                    Id = diagnostic.Id,
                    Severity = NormalizeSeverity(diagnostic.Severity),
                    Message = diagnostic.GetMessage(),
                    Location = ApplyPathStyle(in location, pathStyle),
                }
            );
        }

        return diagnostics
            .AsValueEnumerable()
            .OrderBy(static item => item.Location?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Location?.Line ?? int.MaxValue)
            .ThenBy(static item => item.Location?.Column ?? int.MaxValue)
            .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static bool IsInsideDeclaringSyntax(Location location, List<(SyntaxTree Tree, TextSpan Span)> declaringSpans)
    {
        var locationTree = location.SourceTree;
        if (locationTree is null)
            return false;

        var locationSpan = location.SourceSpan;
        foreach (var (tree, span) in declaringSpans)
        {
            if (!SyntaxTreesMatch(locationTree, tree))
                continue;

            if (span.IntersectsWith(locationSpan) || span.Start <= locationSpan.Start && locationSpan.Start <= span.End)
                return true;
        }

        return false;
    }

    static bool SyntaxTreesMatch(SyntaxTree left, SyntaxTree right)
    {
        if (ReferenceEquals(left, right))
            return true;

        var leftPath = TryGetFullPath(left.FilePath);
        var rightPath = TryGetFullPath(right.FilePath);
        return leftPath is not null && rightPath is not null && PathsEqual(leftPath, rightPath);
    }

    async Task<RelatedSymbolInfo[]> CollectRelatedSymbolsAsync(
        WorkspaceSession session,
        WorkspaceSymbolIndex index,
        ResolvedSymbol resolved,
        string[]? requestedRelations,
        WorkspacePathStyle pathStyle,
        CancellationToken ct
    )
    {
        var relations = new List<string>();
        var seenRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in requestedRelations is { Length: > 0 } ? requestedRelations : defaultRelations)
        {
            var normalizedRelation = relation.AsSpan().Trim();
            if (normalizedRelation.IsEmpty)
                continue;

            var trimmedRelation = normalizedRelation.Length == relation.Length ? relation : normalizedRelation.ToString();
            if (!seenRelations.Add(trimmedRelation))
                continue;

            relations.Add(trimmedRelation);
        }

        var results = new List<RelatedSymbolInfo>();
        var seen = new HashSet<RelatedSymbolKey>();

        foreach (var relation in relations)
        {
            switch (true)
            {
                case true when relation.Equals("base_types", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol is INamedTypeSymbol { BaseType: { SpecialType: not SpecialType.System_Object } baseType })
                        Add(relation, baseType);

                    break;
                }
                case true when relation.Equals("implemented_interfaces", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol is INamedTypeSymbol interfaceOwner)
                        foreach (var item in interfaceOwner.Interfaces)
                            Add(relation, item);

                    break;
                }
                case true when relation.Equals("derived_types", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol is INamedTypeSymbol derivableType)
                    {
                        if (derivableType.TypeKind == TypeKind.Interface)
                        {
                            foreach (var item in await SymbolFinder.FindDerivedInterfacesAsync(derivableType, session.Solution, cancellationToken: ct))
                                Add(relation, item);
                        }
                        else
                        {
                            foreach (var item in await SymbolFinder.FindDerivedClassesAsync(derivableType, session.Solution, cancellationToken: ct))
                                Add(relation, item);
                        }
                    }

                    break;
                }
                case true when relation.Equals("implementations", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IEventSymbol)
                        foreach (var item in await SymbolFinder.FindImplementationsAsync(resolved.Symbol, session.Solution, cancellationToken: ct))
                            Add(relation, item);

                    break;
                }
                case true when relation.Equals("overrides", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
                        foreach (var item in await SymbolFinder.FindOverridesAsync(resolved.Symbol, session.Solution, cancellationToken: ct))
                            Add(relation, item);

                    break;
                }
                case true when relation.Equals("overridden_members", StringComparison.OrdinalIgnoreCase):
                {
                    switch (resolved.Symbol)
                    {
                        case IMethodSymbol { OverriddenMethod: not null } method:
                        {
                            Add(relation, method.OverriddenMethod);
                            break;
                        }
                        case IPropertySymbol { OverriddenProperty: not null } property:
                        {
                            Add(relation, property.OverriddenProperty);
                            break;
                        }
                        case IEventSymbol { OverriddenEvent: not null } @event:
                        {
                            Add(relation, @event.OverriddenEvent);
                            break;
                        }
                    }

                    break;
                }
                case true when relation.Equals("containing_symbol", StringComparison.OrdinalIgnoreCase):
                {
                    if (resolved.Symbol.ContainingType is not null)
                        Add(relation, resolved.Symbol.ContainingType);
                    else if (resolved.Symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
                        Add(relation, containingNamespace);

                    break;
                }
            }
        }

        return results.ToArray();

        void Add(string relation, ISymbol symbol)
        {
            var related = index.CreateResolvedSymbol(session.Solution, symbol);
            var key = new RelatedSymbolKey(relation, index.GetCanonicalSignature(related.Entry));
            if (!seen.Add(key))
                return;

            var summary = index.ToSummary(related);
            results.Add(
                new()
                {
                    Relation = relation,
                    Symbol = ApplyPathStyle(in summary, pathStyle),
                }
            );
        }
    }

    static string[] FormatPaths(IEnumerable<string> paths, WorkspacePathStyle pathStyle)
        => paths.AsValueEnumerable().Select(path => WorkspacePathNormalizer.Format(path, pathStyle) ?? path).ToArray();

    static SymbolSummary ApplyPathStyle(in SymbolSummary summary, WorkspacePathStyle pathStyle)
        => new()
        {
            CanonicalSignature = summary.CanonicalSignature,
            DisplaySignature = summary.DisplaySignature,
            ShortName = summary.ShortName,
            Kind = summary.Kind,
            TypeKind = summary.TypeKind,
            Origin = summary.Origin,
            Project = summary.Project,
            ProjectPath = WorkspacePathNormalizer.Format(summary.ProjectPath, pathStyle),
            AssemblyPath = WorkspacePathNormalizer.Format(summary.AssemblyPath, pathStyle),
            ContainingNamespace = summary.ContainingNamespace,
            ContainingType = summary.ContainingType,
            ReturnType = summary.ReturnType,
            ValueType = summary.ValueType,
            Locations = summary.Locations.AsValueEnumerable().Select(location => ApplyPathStyle(in location, pathStyle)).ToArray(),
        };

    static SymbolDetail ApplyPathStyle(in SymbolDetail detail, WorkspacePathStyle pathStyle)
        => new()
        {
            CanonicalSignature = detail.CanonicalSignature,
            DisplaySignature = detail.DisplaySignature,
            ShortName = detail.ShortName,
            Kind = detail.Kind,
            TypeKind = detail.TypeKind,
            Origin = detail.Origin,
            Project = detail.Project,
            ProjectPath = WorkspacePathNormalizer.Format(detail.ProjectPath, pathStyle),
            AssemblyPath = WorkspacePathNormalizer.Format(detail.AssemblyPath, pathStyle),
            ContainingNamespace = detail.ContainingNamespace,
            ContainingType = detail.ContainingType,
            Accessibility = detail.Accessibility,
            IsStatic = detail.IsStatic,
            IsAbstract = detail.IsAbstract,
            IsVirtual = detail.IsVirtual,
            IsOverride = detail.IsOverride,
            IsSealed = detail.IsSealed,
            ReturnType = detail.ReturnType,
            ReturnRefKind = detail.ReturnRefKind,
            ReturnNullableAnnotation = detail.ReturnNullableAnnotation,
            ValueType = detail.ValueType,
            ValueRefKind = detail.ValueRefKind,
            ValueNullableAnnotation = detail.ValueNullableAnnotation,
            Attributes = detail.Attributes,
            ReturnAttributes = detail.ReturnAttributes,
            TypeParameters = detail.TypeParameters,
            Parameters = detail.Parameters,
            Accessors = detail.Accessors,
            Characteristics = detail.Characteristics,
            ConstantValue = detail.ConstantValue,
            Locations = detail.Locations.AsValueEnumerable().Select(location => ApplyPathStyle(in location, pathStyle)).ToArray(),
            Documentation = detail.Documentation,
        };

    internal static SourceLocationInfo ApplyPathStyle(in SourceLocationInfo location, WorkspacePathStyle pathStyle)
        => new()
        {
            FilePath = WorkspacePathNormalizer.Format(location.FilePath, pathStyle) ?? location.FilePath,
            Line = location.Line,
            Column = location.Column,
            EndLine = location.EndLine,
            EndColumn = location.EndColumn,
            LineText = location.LineText,
        };

    static string? FindProjectProperty(XElement root, string localName)
    {
        foreach (var element in root.Descendants())
            if (element.Name.LocalName == localName)
                return element.Value;

        return null;
    }

    static bool IsDigits(string value)
    {
        foreach (var character in value)
            if (!char.IsDigit(character))
                return false;

        return value.Length > 0;
    }
}

readonly record struct RelatedSymbolKey(string Relation, string CanonicalSignature);

sealed class WorkspaceSession(
    MSBuildWorkspace workspace,
    Solution solution,
    string targetPath,
    string targetKind,
    WorkspacePathStyle pathStyle,
    DateTimeOffset loadedAtUtc,
    TimeSpan loadDuration,
    int excludedProjectCount,
    WorkspaceMessageBuffer messages)
    : IDisposable
{
    readonly CancellationTokenSource backgroundWorkSource = new();
    readonly Lock diagnosticsGate = new();
    readonly Lock indexGate = new();
    Task<DiagnosticInfo[]>? diagnosticsTask;
    Task<WorkspaceSymbolIndex>? indexTask;

    public MSBuildWorkspace Workspace { get; } = workspace;

    public Solution Solution { get; } = solution;

    public string TargetPath { get; } = targetPath;

    public string TargetKind { get; } = targetKind;

    public WorkspacePathStyle PathStyle { get; } = pathStyle;

    public DateTimeOffset LoadedAtUtc { get; } = loadedAtUtc;

    public TimeSpan LoadDuration { get; } = loadDuration;

    public int ExcludedProjectCount { get; } = excludedProjectCount;

    public WorkspaceMessageBuffer Messages { get; } = messages;

    public Task<WorkspaceSymbolIndex> GetIndexAsync(CancellationToken ct)
        => GetOrCreateIndexTask().WaitAsync(ct);

    public Task<DiagnosticInfo[]> GetDiagnosticsAsync(CancellationToken ct)
        => GetOrCreateDiagnosticsTask().WaitAsync(ct);

    public void StartIndexBuild()
        => _ = GetOrCreateIndexTask();

    public void Dispose()
    {
        backgroundWorkSource.Cancel();
        Workspace.Dispose();
    }

    Task<WorkspaceSymbolIndex> GetOrCreateIndexTask()
    {
        lock (indexGate)
            return indexTask ??= Task.Run(BuildIndexAsync);

        async Task<WorkspaceSymbolIndex> BuildIndexAsync()
            => await WorkspaceSymbolIndex.BuildAsync(Solution, backgroundWorkSource.Token);
    }

    Task<DiagnosticInfo[]> GetOrCreateDiagnosticsTask()
    {
        lock (diagnosticsGate)
            return diagnosticsTask ??= CollectDiagnosticsAsync();
    }

    async Task<DiagnosticInfo[]> CollectDiagnosticsAsync()
    {
        backgroundWorkSource.Token.ThrowIfCancellationRequested();

        var visibleSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Solution.Projects)
        {
            backgroundWorkSource.Token.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp)
                continue;

            foreach (var document in WorkspaceSessionManager.EnumerateBrowsableDocuments(project))
            {
                var sourcePath = WorkspaceSessionManager.TryGetFullPath(document.FilePath);
                if (sourcePath is not null)
                    visibleSourcePaths.Add(sourcePath);
            }
        }

        var diagnostics = new List<DiagnosticInfo>();

        foreach (var project in Solution.Projects)
        {
            backgroundWorkSource.Token.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp)
                continue;

            var compilation = await project.GetCompilationAsync(backgroundWorkSource.Token);
            if (compilation is null)
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics(backgroundWorkSource.Token))
            {
                backgroundWorkSource.Token.ThrowIfCancellationRequested();

                if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                    continue;

                if (diagnostic.Location.IsInSource)
                {
                    var sourcePath = WorkspaceSessionManager.TryGetFullPath(diagnostic.Location.GetLineSpan().Path);
                    if (sourcePath is null || !visibleSourcePaths.Contains(sourcePath))
                        continue;
                }

                SourceLocationInfo? styledLocation = null;
                if (diagnostic.Location.IsInSource)
                {
                    var inSourceLocation = SymbolFactory.ToLocation(diagnostic.Location, lineText: null);
                    styledLocation = WorkspaceSessionManager.ApplyPathStyle(in inSourceLocation, PathStyle);
                }

                diagnostics.Add(
                    new()
                    {
                        Project = project.Name,
                        Id = diagnostic.Id,
                        Severity = WorkspaceSessionManager.NormalizeSeverity(diagnostic.Severity),
                        Message = diagnostic.GetMessage(),
                        Location = styledLocation,
                    }
                );
            }
        }

        return diagnostics
            .AsValueEnumerable()
            .OrderBy(static item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Location?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Location?.Line ?? int.MaxValue)
            .ThenBy(static item => item.Location?.Column ?? int.MaxValue)
            .ToArray();
    }
}
