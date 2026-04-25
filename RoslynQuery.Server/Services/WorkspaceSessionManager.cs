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
            return await BuildStatusAsync(activeSession, ct);
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

    public async Task<DescribeSymbolResponse> DescribeSymbolAsync(string symbol, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeSession is null)
                return new() { Query = symbol, Error = "No workspace is open." };

            var index = await activeSession.GetIndexAsync(ct);
            var resolution = index.Resolve(symbol);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var entry = resolution.Entry!;
            var pathStyle = activeSession.PathStyle;
            var detail = SymbolFactory.ToDetail(entry);
            var references = await CollectReferencesAsync(activeSession, entry, pathStyle, ct);
            var related = await CollectRelatedSymbolsAsync(activeSession, index, entry, describeRelations, pathStyle, ct);
            var overloads = CollectOverloads(index, entry, pathStyle);
            var diagnostics = await CollectSymbolDiagnosticsAsync(activeSession, entry, pathStyle, ct);
            return new()
            {
                Success = true,
                Query = symbol,
                Symbol = ApplyPathStyle(in detail, pathStyle),
                Usages = SummarizeReferences(references),
                Relations = related,
                Overloads = overloads,
                TypeMembers = entry.Symbol is INamedTypeSymbol typeSymbol ? CollectTypeMemberSummary(typeSymbol) : null,
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
            var resolution = index.Resolve(symbol, kindFilter: "type");
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            if (resolution.Entry!.Symbol is not INamedTypeSymbol typeSymbol)
                return new() { Query = symbol, Error = "The resolved symbol is not a type." };

            var pathStyle = activeSession.PathStyle;
            var typeSummary = resolution.Entry.ToSummary();
            var members = CollectTypeMembers(index, typeSymbol, includeInherited)
                .AsValueEnumerable()
                .Select(item =>
                {
                    var summary = item.ToSummary();
                    return ApplyPathStyle(in summary, pathStyle);
                })
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
            var resolution = index.Resolve(symbol);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var references = await CollectReferencesAsync(activeSession, resolution.Entry!, activeSession.PathStyle, ct);
            var symbolSummary = resolution.Entry!.ToSummary();
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
            var resolution = index.Resolve(symbol);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var related = await CollectRelatedSymbolsAsync(activeSession, index, resolution.Entry!, relations, activeSession.PathStyle, ct);
            var symbolSummary = resolution.Entry!.ToSummary();
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
            var resolution = index.Resolve(symbol);
            if (!resolution.Success)
                return new() { Query = symbol, Error = resolution.Error, Candidates = resolution.Candidates };

            var entry = resolution.Entry!;
            var symbolSummary = entry.ToSummary();
            if (entry.Symbol is not IMethodSymbol and not IPropertySymbol)
            {
                return new()
                {
                    Query = symbol,
                    Error = "IL can only be viewed for methods and properties.",
                    Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                };
            }

            var project = FindProject(activeSession, entry);
            if (project is null)
            {
                return new()
                {
                    Query = symbol,
                    Error = "The symbol's project is no longer available in the active workspace.",
                    Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                };
            }

            var result = await IlViewer.ViewAsync(project, entry.Symbol, compact, ct);
            return new()
            {
                Success = result.Success,
                Error = result.Error,
                Query = symbol,
                Candidates = result.Candidates,
                Symbol = ApplyPathStyle(in symbolSummary, activeSession.PathStyle),
                EmitDiagnostics = result.EmitDiagnostics,
                Methods = result.Methods,
            };
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

        Solution solution;
        if (string.Equals(targetKind, "project", StringComparison.Ordinal))
            solution = (await workspace.OpenProjectAsync(path, cancellationToken: ct)).Solution;
        else
            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct);

        stopwatch.Stop();

        return new(
            workspace,
            solution,
            path,
            targetKind,
            pathStyle,
            startedAtUtc,
            stopwatch.Elapsed,
            messages);
    }

    static async Task<WorkspaceStatusResponse> BuildStatusAsync(
        WorkspaceSession? session,
        CancellationToken ct,
        bool includeDocuments = true,
        bool includeDiagnostics = true,
        bool includeProjects = true)
    {
        if (session is null)
            return new() { IsBound = false };

        var projectCount = CountCSharpProjects(session);
        var projects = includeProjects ? BuildProjectInfos(session, includeDocuments) : [];
        return new()
        {
            IsBound = true,
            TargetPath = WorkspacePathNormalizer.Format(session.TargetPath, session.PathStyle),
            TargetKind = session.TargetKind,
            ProjectCount = projectCount,
            LoadedAtUtc = session.LoadedAtUtc,
            LastLoadDurationMs = session.LoadDuration.TotalMilliseconds,
            Messages = session.Messages.ToArray(),
            Projects = projects,
            Diagnostics = includeDiagnostics
                ? await session.GetDiagnosticsAsync(ct)
                : [],
        };
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
        for (var i = 0; i < projects.Length; i++)
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

    static Project? FindProject(WorkspaceSession session, SymbolSearchEntry entry)
    {
        foreach (var project in session.Solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;

            if (!string.Equals(project.Name, entry.Project, StringComparison.Ordinal))
                continue;

            if (entry.ProjectPath is null || project.FilePath is null || PathsEqual(project.FilePath, entry.ProjectPath))
                return project;
        }

        return null;
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

    internal static Document[] EnumerateBrowsableDocuments(Project project)
        => project.Documents
            .AsValueEnumerable()
            .Where(static document =>
            document.SourceCodeKind == SourceCodeKind.Regular
            && document.FilePath is not null
            && IsBrowsablePath(document.FilePath))
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
            _ when symbol.StartsWith("NETCOREAPP", StringComparison.OrdinalIgnoreCase) => ConvertFrameworkSymbol(symbol, "NETCOREAPP", "netcoreapp"),
            _ when symbol.StartsWith("NET", StringComparison.OrdinalIgnoreCase) => ConvertFrameworkSymbol(symbol, "NET", "net"),
            _ => null,
        };
    }

    static string? ConvertFrameworkSymbol(string symbol, string symbolPrefix, string tfmPrefix)
    {
        var suffix = symbol[symbolPrefix.Length..];
        if (suffix.Length == 0)
            return null;

        if (!suffix.Contains('_'))
            return IsDigits(suffix) ? tfmPrefix + suffix.ToLowerInvariant() : null;

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
            DiagnosticSeverity.Hidden => "hidden",
            DiagnosticSeverity.Info => "info",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Error => "error",
            _ => severity.ToString().ToLowerInvariant(),
        };

    static IEnumerable<SymbolSearchEntry> CollectTypeMembers(WorkspaceSymbolIndex index, INamedTypeSymbol typeSymbol, bool includeInherited)
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
            .Select(symbol => index.TryGetBySymbol(symbol) ?? SymbolSearchEntry.CreateMetadata(symbol))
            .OrderBy(static entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.DisplaySignature, StringComparer.OrdinalIgnoreCase)
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

    static SymbolSummary[] CollectOverloads(WorkspaceSymbolIndex index, SymbolSearchEntry entry, WorkspacePathStyle pathStyle)
    {
        if (entry.Symbol is not IMethodSymbol { ContainingType: not null } method)
            return [];

        var overloads = method.ContainingType
            .GetMembers(method.Name)
            .AsValueEnumerable()
            .OfType<IMethodSymbol>()
            .Where(candidate =>
                candidate.MethodKind == method.MethodKind
                && !candidate.IsImplicitlyDeclared
                && WorkspaceSymbolIndex.ShouldIndexMember(candidate))
            .Cast<ISymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Select(symbol => index.TryGetBySymbol(symbol) ?? SymbolSearchEntry.CreateMetadata(symbol))
            .OrderBy(static overload => overload.DisplaySignature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (overloads.Length <= 1)
            return [];

        return overloads
            .AsValueEnumerable()
            .Select(overload =>
            {
                var summary = overload.ToSummary();
                return ApplyPathStyle(in summary, pathStyle);
            })
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
                })
                .OrderBy(static item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Examples = references.AsValueEnumerable().Take(3).ToArray(),
        };

    async Task<ReferenceInfo[]> CollectReferencesAsync(WorkspaceSession session, SymbolSearchEntry entry, WorkspacePathStyle pathStyle, CancellationToken ct)
    {
        var references = await SymbolFinder.FindReferencesAsync(entry.Symbol, session.Solution, cancellationToken: ct);
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
            items.Add(new()
            {
                Project = location.Document.Project.Name,
                DocumentPath = WorkspacePathNormalizer.Format(location.Document.FilePath, pathStyle),
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1,
                LineText = trimmedLineText,
            });
        }

        return items
            .AsValueEnumerable()
            .OrderBy(static item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Line)
            .ThenBy(static item => item.Column)
            .ToArray();
    }

    static async Task<DiagnosticInfo[]> CollectSymbolDiagnosticsAsync(WorkspaceSession session, SymbolSearchEntry entry, WorkspacePathStyle pathStyle, CancellationToken ct)
    {
        if (entry.Symbol.DeclaringSyntaxReferences.Length is 0)
            return [];

        var project = FindProject(session, entry);
        if (project is null)
            return [];

        var declaringSpans = new List<(SyntaxTree Tree, TextSpan Span)>();
        foreach (var syntaxReference in entry.Symbol.DeclaringSyntaxReferences)
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
            diagnostics.Add(new()
            {
                Project = project.Name,
                Id = diagnostic.Id,
                Severity = NormalizeSeverity(diagnostic.Severity),
                Message = diagnostic.GetMessage(),
                Location = ApplyPathStyle(in location, pathStyle),
            });
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
        SymbolSearchEntry entry,
        string[]? requestedRelations,
        WorkspacePathStyle pathStyle,
        CancellationToken ct)
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in relations)
        {
            switch (true)
            {
                case true when relation.Equals("base_types", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol is INamedTypeSymbol namedType && namedType.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                        Add(relation, baseType);
                    break;

                case true when relation.Equals("implemented_interfaces", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol is INamedTypeSymbol interfaceOwner)
                        foreach (var item in interfaceOwner.Interfaces)
                            Add(relation, item);
                    break;

                case true when relation.Equals("derived_types", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol is INamedTypeSymbol derivableType)
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

                case true when relation.Equals("implementations", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IEventSymbol)
                        foreach (var item in await SymbolFinder.FindImplementationsAsync(entry.Symbol, session.Solution, cancellationToken: ct))
                            Add(relation, item);
                    break;

                case true when relation.Equals("overrides", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
                        foreach (var item in await SymbolFinder.FindOverridesAsync(entry.Symbol, session.Solution, cancellationToken: ct))
                            Add(relation, item);
                    break;

                case true when relation.Equals("overridden_members", StringComparison.OrdinalIgnoreCase):
                    switch (entry.Symbol)
                    {
                        case IMethodSymbol { OverriddenMethod: not null } method:
                            Add(relation, method.OverriddenMethod);
                            break;
                        case IPropertySymbol { OverriddenProperty: not null } property:
                            Add(relation, property.OverriddenProperty);
                            break;
                        case IEventSymbol { OverriddenEvent: not null } @event:
                            Add(relation, @event.OverriddenEvent);
                            break;
                    }
                    break;

                case true when relation.Equals("containing_symbol", StringComparison.OrdinalIgnoreCase):
                    if (entry.Symbol.ContainingType is not null)
                        Add(relation, entry.Symbol.ContainingType);
                    else if (entry.Symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
                        Add(relation, containingNamespace);
                    break;
            }
        }

        return results.ToArray();

        void Add(string relation, ISymbol symbol)
        {
            var resolved = index.TryGetBySymbol(symbol) ?? SymbolSearchEntry.CreateMetadata(symbol);
            var key = relation + "\n" + resolved.CanonicalSignature;
            if (!seen.Add(key))
                return;

            var summary = resolved.ToSummary();
            results.Add(new()
            {
                Relation = relation,
                Symbol = ApplyPathStyle(in summary, pathStyle),
            });
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
            Project = summary.Project,
            ProjectPath = WorkspacePathNormalizer.Format(summary.ProjectPath, pathStyle),
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
            Project = detail.Project,
            ProjectPath = WorkspacePathNormalizer.Format(detail.ProjectPath, pathStyle),
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

sealed class WorkspaceSession(
    MSBuildWorkspace workspace,
    Solution solution,
    string targetPath,
    string targetKind,
    WorkspacePathStyle pathStyle,
    DateTimeOffset loadedAtUtc,
    TimeSpan loadDuration,
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

                diagnostics.Add(new()
                {
                    Project = project.Name,
                    Id = diagnostic.Id,
                    Severity = WorkspaceSessionManager.NormalizeSeverity(diagnostic.Severity),
                    Message = diagnostic.GetMessage(),
                    Location = styledLocation,
                });
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
