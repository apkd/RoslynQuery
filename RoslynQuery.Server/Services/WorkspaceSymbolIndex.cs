using System.Diagnostics;
using Microsoft.CodeAnalysis;
using static System.StringComparison;

namespace RoslynQuery;

sealed class WorkspaceSymbolIndex
{
    readonly SymbolSearchEntry[] entries;
    readonly Dictionary<ProjectId, SourceProjectInfo> sourceProjects;
    readonly Dictionary<string, int[]> byDisplay;
    readonly Dictionary<string, int[]> bySimpleName;

    WorkspaceSymbolIndex(
        SymbolSearchEntry[] entries,
        Dictionary<ProjectId, SourceProjectInfo> sourceProjects,
        Dictionary<string, int[]> byDisplay,
        Dictionary<string, int[]> bySimpleName,
        ExternalMetadataIndex externalMetadata,
        SourceIndexBenchmarkInfo buildMetrics
    )
    {
        this.entries = entries;
        this.sourceProjects = sourceProjects;
        this.byDisplay = byDisplay;
        this.bySimpleName = bySimpleName;
        ExternalMetadata = externalMetadata;
        BuildMetrics = buildMetrics;
    }

    public ExternalMetadataIndex ExternalMetadata { get; }

    public SourceIndexBenchmarkInfo BuildMetrics { get; }

    public static async Task<WorkspaceSymbolIndex> BuildAsync(Solution solution, CancellationToken ct)
    {
        var projects = solution.Projects
            .AsValueEnumerable()
            .Where(static project => project.Language == LanguageNames.CSharp)
            .ToArray();

        var metrics = new SourceIndexBuildMetrics(projects.Length);

        var entries = new List<SymbolSearchEntry>();
        var sourceProjects = new Dictionary<ProjectId, SourceProjectInfo>(projects.Length);

        foreach (var project in projects)
            sourceProjects[project.Id] = new(project.Name, project.FilePath);

        var dependencyPlanningStarted = Stopwatch.GetTimestamp();
        var levels = BuildProjectDependencyLevels(projects);
        metrics.DependencyPlanningTicks = GetElapsedTicks(dependencyPlanningStarted);
        metrics.DependencyLevelCount = levels.Length;
        metrics.LargestDependencyLevelProjectCount = levels.AsValueEnumerable().Select(static level => level.Length).DefaultIfEmpty().Max();

        foreach (var level in levels)
        {
            var results = await BuildProjectLevelAsync(level, ct);
            var mergeStarted = Stopwatch.GetTimestamp();
            foreach (var result in results)
            {
                entries.AddRange(result.Entries);
                metrics.AddProject(result.Stats);
            }

            metrics.EntryMergeTicks += GetElapsedTicks(mergeStarted);
        }

        var entryArray = entries.ToArray();
        var displayLookupStarted = Stopwatch.GetTimestamp();
        var byDisplay = BuildDisplayLookup(entryArray);
        metrics.DisplayLookupBuildTicks = GetElapsedTicks(displayLookupStarted);
        metrics.DisplayLookupKeyCount = byDisplay.Count;

        var simpleNameLookupStarted = Stopwatch.GetTimestamp();
        var bySimpleName = BuildSimpleNameLookup(entryArray);
        metrics.SimpleNameLookupBuildTicks = GetElapsedTicks(simpleNameLookupStarted);
        metrics.SimpleNameLookupKeyCount = bySimpleName.Count;

        metrics.EntryCount = entryArray.Length;
        return new(
            entryArray,
            sourceProjects,
            byDisplay,
            bySimpleName,
            new(),
            metrics.ToBenchmarkInfo()
        );
    }

    static async Task<ProjectIndexBuildResult[]> BuildProjectLevelAsync(Project[] projects, CancellationToken ct)
    {
        if (projects.Length is 1)
            return [await BuildProjectIndexAsync(projects[0], ct)];

        var tasks = projects
            .AsValueEnumerable()
            .Select(project => Task.Run(() => BuildProjectIndexAsync(project, ct), ct))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    static async Task<ProjectIndexBuildResult> BuildProjectIndexAsync(Project project, CancellationToken ct)
    {
        var stats = new ProjectIndexBuildStats(project.Name);
        var compilationStarted = Stopwatch.GetTimestamp();
        var compilation = await project.GetCompilationAsync(ct);
        stats.CompilationTicks += GetElapsedTicks(compilationStarted);
        if (compilation is null)
            return new([], stats);

        var entries = new List<SymbolSearchEntry>();
        var traversalStarted = Stopwatch.GetTimestamp();
        CollectNamespace(project, compilation.Assembly.GlobalNamespace, entries, stats);
        stats.SymbolTraversalTicks += GetElapsedTicks(traversalStarted);
        stats.EntryCount = entries.Count;
        return new([.. entries], stats);
    }

    static Project[][] BuildProjectDependencyLevels(Project[] projects)
    {
        var projectIndexes = new Dictionary<ProjectId, int>(projects.Length);
        for (int i = 0; i < projects.Length; i++)
            projectIndexes[projects[i].Id] = i;

        var dependencyCounts = new int[projects.Length];
        var dependents = new List<int>?[projects.Length];
        for (int projectIndex = 0; projectIndex < projects.Length; projectIndex++)
        {
            foreach (var reference in projects[projectIndex].ProjectReferences)
            {
                if (!projectIndexes.TryGetValue(reference.ProjectId, out var dependencyIndex))
                    continue;

                dependencyCounts[projectIndex]++;
                (dependents[dependencyIndex] ??= []).Add(projectIndex);
            }
        }

        var ready = new List<int>();
        for (int i = 0; i < dependencyCounts.Length; i++)
            if (dependencyCounts[i] is 0)
                ready.Add(i);

        var remaining = projects.Length;
        var levels = new List<Project[]>();
        while (ready.Count > 0)
        {
            ready.Sort();
            var level = new Project[ready.Count];
            for (int i = 0; i < ready.Count; i++)
                level[i] = projects[ready[i]];

            levels.Add(level);

            var next = new List<int>();
            foreach (var projectIndex in ready)
            {
                remaining--;
                if (dependents[projectIndex] is not { } dependentProjects)
                    continue;

                foreach (var dependentIndex in dependentProjects)
                {
                    dependencyCounts[dependentIndex]--;
                    if (dependencyCounts[dependentIndex] is 0)
                        next.Add(dependentIndex);
                }
            }

            ready = next;
        }

        if (remaining > 0)
        {
            var cyclicProjects = new List<Project>(remaining);
            for (int i = 0; i < dependencyCounts.Length; i++)
                if (dependencyCounts[i] > 0)
                    cyclicProjects.Add(projects[i]);

            levels.Add([.. cyclicProjects]);
        }

        return [.. levels];
    }

    static long GetElapsedTicks(long started)
        => Stopwatch.GetElapsedTime(started).Ticks;

    public SymbolResolution Resolve(string query, string? kindFilter = null)
    {
        var normalizedKind = kindFilter is null ? null : NormalizeKind(kindFilter);
        if (string.IsNullOrWhiteSpace(query))
            return SymbolResolution.NotFound("Symbol is required.");

        var normalizedQuery = QueryToTrimmedValue(query);
        if (normalizedQuery.Contains("::", Ordinal))
        {
            var exactCanonical = FilterCanonicalCandidates(normalizedQuery, normalizedKind);
            if (exactCanonical.Length == 1)
                return SymbolResolution.Resolved(exactCanonical[0]);

            if (exactCanonical.Length > 1)
                return Ambiguous(query, exactCanonical);
        }

        if (MightBeDisplayQuery(normalizedQuery))
        {
            var exactDisplay = FilterCandidates(byDisplay, normalizedQuery, normalizedKind);
            if (exactDisplay.Length == 1)
                return SymbolResolution.Resolved(exactDisplay[0]);

            if (exactDisplay.Length > 1)
                return Ambiguous(query, exactDisplay);
        }

        var exactSimple = FilterCandidates(bySimpleName, normalizedQuery, normalizedKind);
        if (exactSimple.Length == 1)
            return SymbolResolution.Resolved(exactSimple[0]);

        if (exactSimple.Length > 1)
            return Ambiguous(query, exactSimple);

        var fuzzyMatches = Search(normalizedQuery, normalizedKind ?? "all", 10);
        if (fuzzyMatches.Length == 1)
            return SymbolResolution.Resolved(fuzzyMatches[0].Entry);

        if (fuzzyMatches.Length > 1 && fuzzyMatches[0].Score < fuzzyMatches[1].Score)
            return SymbolResolution.Resolved(fuzzyMatches[0].Entry);

        if (fuzzyMatches.Length > 1)
            return Ambiguous(query, fuzzyMatches.AsValueEnumerable().Select(static item => item.Entry).ToArray());

        return SymbolResolution.NotFound($"'{query}' did not resolve to a symbol.");
    }

    public Task<ResolvedSymbol?> ResolveAsync(Solution solution, SymbolSearchEntry entry, CancellationToken ct)
        => Task.FromResult<ResolvedSymbol?>(new ResolvedSymbol(entry, entry.Symbol));

    public ResolvedSymbol CreateResolvedSymbol(Solution solution, ISymbol symbol)
        => new(TryGetBySymbol(symbol) ?? SymbolSearchEntry.CreateMetadata(symbol, ExternalMetadata.TryGetAssemblyIndex(symbol)), symbol);

    public SymbolSummary ToSummary(ResolvedSymbol resolved)
    {
        var entry = resolved.Entry;
        var symbol = resolved.Symbol;
        return new()
        {
            CanonicalSignature = GetCanonicalSignature(entry),
            DisplaySignature = entry.DisplaySignature,
            ShortName = entry.ShortName,
            Kind = entry.Kind,
            TypeKind = entry.TypeKind,
            Origin = entry.Origin,
            Project = GetOwnerName(entry),
            ProjectPath = GetProjectPath(entry),
            AssemblyPath = GetAssemblyPath(entry),
            ContainingNamespace = SymbolText.GetContainingNamespace(symbol),
            ContainingType = SymbolText.GetContainingType(symbol),
            ReturnType = symbol is IMethodSymbol { MethodKind: not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor) } method
                ? SymbolText.GetTypeDisplay(method.ReturnType)
                : null,
            ValueType = symbol switch
            {
                IPropertySymbol property => SymbolText.GetTypeDisplay(property.Type),
                IFieldSymbol field       => SymbolText.GetTypeDisplay(field.Type),
                IEventSymbol @event      => SymbolText.GetTypeDisplay(@event.Type),
                _                        => null,
            },
            Locations = GetLocations(resolved),
        };
    }

    public string GetCanonicalSignature(SymbolSearchEntry entry)
        => $"{GetOwnerName(entry)}::{entry.DisplaySignature}";

    public string GetOwnerName(SymbolSearchEntry entry)
        => entry.ProjectId is { } projectId && sourceProjects.TryGetValue(projectId, out var project)
            ? project.Name
            : ExternalMetadata.GetAssemblyName(entry.ExternalAssemblyIndex) ?? entry.Symbol.ContainingAssembly?.Name ?? "metadata";

    public string? GetProjectPath(SymbolSearchEntry entry)
        => entry.ProjectId is { } projectId && sourceProjects.TryGetValue(projectId, out var project)
            ? project.FilePath
            : null;

    public string? GetAssemblyPath(SymbolSearchEntry entry)
        => entry.IsMetadata ? ExternalMetadata.GetAssemblyPath(entry.ExternalAssemblyIndex) ?? ExternalMetadata.TryGetAssemblyPath(entry.Symbol) : null;

    public Project? GetProject(Solution solution, SymbolSearchEntry entry)
        => entry.ProjectId is { } projectId ? solution.GetProject(projectId) : null;

    public SourceLocationInfo[] GetLocations(ResolvedSymbol resolved)
        => resolved.Symbol.Locations
            .AsValueEnumerable()
            .Where(static location => location.IsInSource)
            .Select(location => SymbolFactory.ToLocation(location, lineText: null))
            .ToArray();

    public static bool ShouldIndexMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
            return false;

        return symbol switch
        {
            IMethodSymbol method => method.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor),
            _                    => true,
        };
    }

    SymbolSearchEntry? TryGetBySymbol(ISymbol symbol)
    {
        foreach (var entry in entries)
        {
            if (SymbolEqualityComparer.Default.Equals(entry.Symbol, symbol))
                return entry;
        }

        return null;
    }

    static string NormalizeKind(string kind)
    {
        var normalized = kind.AsSpan().Trim();
        return normalized.Length is 0
            ? string.Empty
            : normalized.Length == kind.Length ? kind : normalized.ToString();
    }

    static void CollectNamespace(Project project, INamespaceSymbol symbol, List<SymbolSearchEntry> entries, ProjectIndexBuildStats stats)
    {
        if (!symbol.IsGlobalNamespace && HasSourceDeclaration(symbol, stats))
            entries.Add(SymbolSearchEntry.CreateSource(project, symbol, stats));

        foreach (var namespaceMember in symbol.GetNamespaceMembers())
            CollectNamespace(project, namespaceMember, entries, stats);

        foreach (var typeMember in symbol.GetTypeMembers())
            CollectType(project, typeMember, entries, stats);
    }

    static void CollectType(Project project, INamedTypeSymbol symbol, List<SymbolSearchEntry> entries, ProjectIndexBuildStats stats)
    {
        if (HasSourceDeclaration(symbol, stats))
            entries.Add(SymbolSearchEntry.CreateSource(project, symbol, stats));

        foreach (var member in symbol.GetMembers())
        {
            if (member is INamedTypeSymbol nestedType)
            {
                CollectType(project, nestedType, entries, stats);
                continue;
            }

            if (!ShouldIndexMember(member) || !HasSourceDeclaration(member, stats))
                continue;

            entries.Add(SymbolSearchEntry.CreateSource(project, member, stats));
        }
    }

    static bool HasSourceDeclaration(ISymbol symbol, ProjectIndexBuildStats stats)
    {
        var started = Stopwatch.GetTimestamp();
        var result = symbol.DeclaringSyntaxReferences.Length > 0;
        stats.SourceDeclarationCheckTicks += GetElapsedTicks(started);
        return result;
    }

    SymbolSearchEntry[] FilterCanonicalCandidates(string query, string? kindFilter)
    {
        var separatorIndex = query.IndexOf("::", Ordinal);
        if (separatorIndex < 0)
            return [];

        var owner = query[..separatorIndex].Trim();
        var display = query[(separatorIndex + 2)..].Trim();
        if (owner.Length is 0 || display.Length is 0)
            return [];

        if (!byDisplay.TryGetValue(display, out var candidateIndexes))
            return [];

        return FilterCandidates(
            candidateIndexes,
            entry => string.Equals(GetOwnerName(entry), owner, OrdinalIgnoreCase),
            kindFilter
        );
    }

    SymbolSearchEntry[] FilterCandidates(Dictionary<string, int[]> lookup, string query, string? kindFilter)
        => lookup.TryGetValue(query, out var candidateIndexes)
            ? FilterCandidates(candidateIndexes, static _ => true, kindFilter)
            : [];

    SymbolSearchEntry[] FilterCandidates(int[] candidateIndexes, Func<SymbolSearchEntry, bool> predicate, string? kindFilter)
    {
        var results = new List<SymbolSearchEntry>(candidateIndexes.Length);
        foreach (var index in candidateIndexes)
        {
            var entry = entries[index];
            if (predicate(entry) && (kindFilter is null || entry.MatchesKind(kindFilter)))
                results.Add(entry);
        }

        return [.. results];
    }

    static Dictionary<string, int[]> BuildDisplayLookup(SymbolSearchEntry[] entries)
    {
        var buckets = new Dictionary<string, LookupBuildBucket>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            var key = entries[i].DisplaySignature;
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new();
                buckets.Add(key, bucket);
            }

            bucket.Count++;
        }

        foreach (var bucket in buckets.Values)
            bucket.Indexes = new int[bucket.Count];

        for (var i = 0; i < entries.Length; i++)
        {
            var bucket = buckets[entries[i].DisplaySignature];
            bucket.Indexes[bucket.Position++] = i;
        }

        var lookup = new Dictionary<string, int[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
            lookup.Add(bucket.Key, bucket.Value.Indexes);

        return lookup;
    }

    static Dictionary<string, int[]> BuildSimpleNameLookup(SymbolSearchEntry[] entries)
    {
        var buckets = new Dictionary<ShortNameKey, LookupBuildBucket>(ShortNameKeyComparer.Instance);
        for (var i = 0; i < entries.Length; i++)
        {
            var key = entries[i].GetShortNameKey();
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new();
                buckets.Add(key, bucket);
            }

            bucket.Count++;
        }

        foreach (var bucket in buckets.Values)
            bucket.Indexes = new int[bucket.Count];

        for (var i = 0; i < entries.Length; i++)
        {
            var bucket = buckets[entries[i].GetShortNameKey()];
            bucket.Indexes[bucket.Position++] = i;
        }

        var lookup = new Dictionary<string, int[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
            lookup.Add(bucket.Key.ToString(), bucket.Value.Indexes);

        return lookup;
    }

    (SymbolSearchEntry Entry, int Score)[] Search(string query, string kind, int limit)
    {
        var normalizedQuery = QueryToTrimmedValue(query);
        var normalizedKind = NormalizeKind(kind);
        var boundedLimit = Math.Clamp(limit, 1, 200);

        return entries
            .AsValueEnumerable()
            .Select(entry => (Entry: entry, Score: entry.GetMatchScore(normalizedQuery, normalizedKind)))
            .Where(static item => item.Score >= 0)
            .OrderBy(static item => item.Score)
            .ThenBy(static item => item.Entry.DisplaySignature, StringComparer.OrdinalIgnoreCase)
            .Take(boundedLimit)
            .ToArray();
    }

    static string QueryToTrimmedValue(string value)
    {
        var trimmed = value.AsSpan().Trim();
        return trimmed.Length == value.Length ? value : trimmed.ToString();
    }

    static bool MightBeDisplayQuery(string query)
        => query.AsSpan().IndexOfAny(['.', '(', '<', '>', ',']) >= 0;

    SymbolResolution Ambiguous(string query, IEnumerable<SymbolSearchEntry> entries)
        => SymbolResolution.Ambiguous(
            $"'{query}' is ambiguous.",
            entries.AsValueEnumerable().Select(GetCanonicalSignature).ToArray()
        );
}

readonly record struct SourceProjectInfo(string Name, string? FilePath);

readonly record struct ProjectIndexBuildResult(SymbolSearchEntry[] Entries, ProjectIndexBuildStats Stats);

sealed class SourceIndexBuildMetrics(int projectCount)
{
    readonly List<ProjectIndexBuildStats> projects = new(projectCount);

    public int ProjectCount { get; } = projectCount;

    public int DependencyLevelCount { get; set; }

    public int LargestDependencyLevelProjectCount { get; set; }

    public int EntryCount { get; set; }

    public int DisplayLookupKeyCount { get; set; }

    public int SimpleNameLookupKeyCount { get; set; }

    public long DependencyPlanningTicks { get; set; }

    public long EntryMergeTicks { get; set; }

    public long DisplayLookupBuildTicks { get; set; }

    public long SimpleNameLookupBuildTicks { get; set; }

    public void AddProject(ProjectIndexBuildStats stats)
        => projects.Add(stats);

    public SourceIndexBenchmarkInfo ToBenchmarkInfo()
    {
        var compilationTicks = projects.AsValueEnumerable().Sum(static project => project.CompilationTicks);
        var traversalTicks = projects.AsValueEnumerable().Sum(static project => project.SymbolTraversalTicks);
        var sourceDeclarationCheckTicks = projects.AsValueEnumerable().Sum(static project => project.SourceDeclarationCheckTicks);
        var displaySignatureTicks = projects.AsValueEnumerable().Sum(static project => project.DisplaySignatureTicks);
        var traversalExclusiveTicks = Math.Max(0, traversalTicks - sourceDeclarationCheckTicks - displaySignatureTicks);
        return new()
        {
            ProjectCount = ProjectCount,
            DependencyLevelCount = DependencyLevelCount,
            LargestDependencyLevelProjectCount = LargestDependencyLevelProjectCount,
            EntryCount = EntryCount,
            DisplayLookupKeyCount = DisplayLookupKeyCount,
            SimpleNameLookupKeyCount = SimpleNameLookupKeyCount,
            DependencyPlanningDurationMs = TicksToMilliseconds(DependencyPlanningTicks),
            CompilationDurationMs = TicksToMilliseconds(compilationTicks),
            SymbolTraversalDurationMs = TicksToMilliseconds(traversalTicks),
            SymbolTraversalExclusiveDurationMs = TicksToMilliseconds(traversalExclusiveTicks),
            SourceDeclarationCheckDurationMs = TicksToMilliseconds(sourceDeclarationCheckTicks),
            DisplaySignatureDurationMs = TicksToMilliseconds(displaySignatureTicks),
            EntryMergeDurationMs = TicksToMilliseconds(EntryMergeTicks),
            LookupBuildDurationMs = TicksToMilliseconds(DisplayLookupBuildTicks + SimpleNameLookupBuildTicks),
            DisplayLookupBuildDurationMs = TicksToMilliseconds(DisplayLookupBuildTicks),
            SimpleNameLookupBuildDurationMs = TicksToMilliseconds(SimpleNameLookupBuildTicks),
            SlowestProjects = projects
                .AsValueEnumerable()
                .OrderByDescending(static project => project.CompilationTicks + project.SymbolTraversalTicks)
                .Take(10)
                .Select(static project => project.ToBenchmarkInfo())
                .ToArray(),
        };
    }

    static double TicksToMilliseconds(long ticks)
        => TimeSpan.FromTicks(ticks).TotalMilliseconds;
}

sealed class ProjectIndexBuildStats(string projectName)
{
    public string ProjectName { get; } = projectName;

    public int EntryCount { get; set; }

    public long CompilationTicks { get; set; }

    public long SymbolTraversalTicks { get; set; }

    public long SourceDeclarationCheckTicks { get; set; }

    public long DisplaySignatureTicks { get; set; }

    public SourceIndexProjectBenchmarkInfo ToBenchmarkInfo()
        => new()
        {
            Project = ProjectName,
            EntryCount = EntryCount,
            CompilationDurationMs = TimeSpan.FromTicks(CompilationTicks).TotalMilliseconds,
            SymbolTraversalDurationMs = TimeSpan.FromTicks(SymbolTraversalTicks).TotalMilliseconds,
            DisplaySignatureDurationMs = TimeSpan.FromTicks(DisplaySignatureTicks).TotalMilliseconds,
        };
}

sealed class LookupBuildBucket
{
    public int Count { get; set; }

    public int Position { get; set; }

    public int[] Indexes { get; set; } = [];
}

readonly record struct ShortNameKey(string Value, int Start, int Length)
{
    public ReadOnlySpan<char> Span
        => Value.AsSpan(Start, Length);

    public override string ToString()
        => Start is 0 && Length == Value.Length ? Value : Value.Substring(Start, Length);
}

sealed class ShortNameKeyComparer : IEqualityComparer<ShortNameKey>
{
    public static ShortNameKeyComparer Instance { get; } = new();

    public bool Equals(ShortNameKey x, ShortNameKey y)
        => x.Span.Equals(y.Span, OrdinalIgnoreCase);

    public int GetHashCode(ShortNameKey obj)
        => string.GetHashCode(obj.Span, OrdinalIgnoreCase);
}

readonly record struct ResolvedSymbol(SymbolSearchEntry Entry, ISymbol Symbol);

readonly record struct SymbolSearchEntry(
    ISymbol Symbol,
    ProjectId? ProjectId,
    int ExternalAssemblyIndex,
    string DisplaySignature,
    int ShortNameStart,
    int ShortNameLength,
    SymbolSearchKind SearchKind)
{
    public bool IsSource
        => ProjectId is not null;

    public bool IsMetadata
        => ProjectId is null;

    public string ShortName => HasShortNameRange
        ? DisplaySignature.Substring(ShortNameStart, ShortNameLength)
        : SymbolText.GetShortName(Symbol);

    public string Kind
        => GetKind(SearchKind);

    public string? TypeKind
        => IsTypeKind(SearchKind) ? Kind : null;

    public string Origin
        => IsSource ? "source" : "metadata";

    bool HasShortNameRange
        => ShortNameStart >= 0;

    public ShortNameKey GetShortNameKey()
    {
        if (HasShortNameRange)
            return new(DisplaySignature, ShortNameStart, ShortNameLength);

        var shortName = SymbolText.GetShortName(Symbol);
        return new(shortName, 0, shortName.Length);
    }

    public static SymbolSearchEntry CreateSource(Project project, ISymbol symbol, ProjectIndexBuildStats stats)
    {
        var (displaySignature, shortNameStart, shortNameLength, kind) = BuildSearchInfo(symbol, stats);
        return new(symbol, project.Id, -1, displaySignature, shortNameStart, shortNameLength, kind);
    }

    public static SymbolSearchEntry CreateMetadata(ISymbol symbol, int externalAssemblyIndex)
    {
        var (displaySignature, shortNameStart, shortNameLength, kind) = BuildSearchInfo(symbol);
        return new(symbol, null, externalAssemblyIndex, displaySignature, shortNameStart, shortNameLength, kind);
    }

    static (string DisplaySignature, int ShortNameStart, int ShortNameLength, SymbolSearchKind Kind) BuildSearchInfo(
        ISymbol symbol,
        ProjectIndexBuildStats? stats = null
    )
    {
        var displayStarted = Stopwatch.GetTimestamp();
        var displaySignature = SymbolText.GetDisplaySignature(symbol);
        if (stats is not null)
            stats.DisplaySignatureTicks += Stopwatch.GetElapsedTime(displayStarted).Ticks;

        var (shortNameStart, shortNameLength) = FindShortNameRange(symbol, displaySignature);
        return (displaySignature, shortNameStart, shortNameLength, GetSearchKind(symbol));
    }

    public int GetMatchScore(string query, string kind)
    {
        if (!MatchesKind(kind))
            return -1;

        var querySpan = query.AsSpan();
        if (ShortNameEquals(querySpan))
            return 0;

        var displaySignature = DisplaySignature.AsSpan();
        if (displaySignature.Equals(querySpan, OrdinalIgnoreCase) || EndsWithDottedQuery(displaySignature, querySpan))
            return 1;

        if (ShortNameStartsWith(querySpan))
            return 2;

        if (displaySignature.StartsWith(querySpan, OrdinalIgnoreCase) || ContainsDottedQuery(displaySignature, querySpan))
            return 3;

        if (ShortNameContains(querySpan) || displaySignature.Contains(querySpan, OrdinalIgnoreCase))
            return 4;

        return -1;
    }

    public bool MatchesKind(string kind)
        => kind switch
        {
            "all"    => true,
            "type"   => IsTypeKind(SearchKind),
            "member" => SearchKind is not SymbolSearchKind.Namespace && !IsTypeKind(SearchKind),
            _        => string.Equals(kind, Kind, OrdinalIgnoreCase),
        };

    bool ShortNameEquals(ReadOnlySpan<char> query)
        => HasShortNameRange
            ? DisplaySignature.AsSpan(ShortNameStart, ShortNameLength).Equals(query, OrdinalIgnoreCase)
            : SymbolText.GetShortName(Symbol).AsSpan().Equals(query, OrdinalIgnoreCase);

    bool ShortNameStartsWith(ReadOnlySpan<char> query)
        => HasShortNameRange
            ? DisplaySignature.AsSpan(ShortNameStart, ShortNameLength).StartsWith(query, OrdinalIgnoreCase)
            : SymbolText.GetShortName(Symbol).AsSpan().StartsWith(query, OrdinalIgnoreCase);

    bool ShortNameContains(ReadOnlySpan<char> query)
        => HasShortNameRange
            ? DisplaySignature.AsSpan(ShortNameStart, ShortNameLength).Contains(query, OrdinalIgnoreCase)
            : SymbolText.GetShortName(Symbol).AsSpan().Contains(query, OrdinalIgnoreCase);

    static bool EndsWithDottedQuery(ReadOnlySpan<char> value, ReadOnlySpan<char> query)
        => value.Length > query.Length
           && value[^query.Length..].Equals(query, OrdinalIgnoreCase)
           && value[value.Length - query.Length - 1] == '.';

    static bool ContainsDottedQuery(ReadOnlySpan<char> value, ReadOnlySpan<char> query)
    {
        var search = value;
        var offset = 0;
        while (true)
        {
            var index = search.IndexOf(query, OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var absoluteIndex = offset + index;
            if (absoluteIndex > 0 && value[absoluteIndex - 1] == '.')
                return true;

            offset = absoluteIndex + 1;
            search = value[offset..];
        }
    }

    static (int Start, int Length) FindShortNameRange(ISymbol symbol, string displaySignature)
    {
        var shortName = SymbolText.GetShortName(symbol);
        if (shortName.Length is 0)
            return (-1, 0);

        var searchEnd = displaySignature.IndexOf('(');
        if (searchEnd < 0)
            searchEnd = displaySignature.Length;

        var index = displaySignature.AsSpan(0, searchEnd).LastIndexOf(shortName.AsSpan(), Ordinal);
        return index < 0 ? (-1, 0) : (index, shortName.Length);
    }

    static SymbolSearchKind GetSearchKind(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol                    => SymbolSearchKind.Namespace,
            INamedTypeSymbol { IsRecord: true } => SymbolSearchKind.Record,
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                Microsoft.CodeAnalysis.TypeKind.Class     => SymbolSearchKind.Class,
                Microsoft.CodeAnalysis.TypeKind.Interface => SymbolSearchKind.Interface,
                Microsoft.CodeAnalysis.TypeKind.Struct    => SymbolSearchKind.Struct,
                Microsoft.CodeAnalysis.TypeKind.Enum      => SymbolSearchKind.Enum,
                Microsoft.CodeAnalysis.TypeKind.Delegate  => SymbolSearchKind.Delegate,
                _                                         => SymbolSearchKind.Type,
            },
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Constructor         => SymbolSearchKind.Constructor,
                MethodKind.StaticConstructor   => SymbolSearchKind.StaticConstructor,
                MethodKind.UserDefinedOperator => SymbolSearchKind.Operator,
                MethodKind.Conversion          => SymbolSearchKind.Conversion,
                MethodKind.Destructor          => SymbolSearchKind.Destructor,
                _                              => SymbolSearchKind.Method,
            },
            IPropertySymbol { IsIndexer: true } => SymbolSearchKind.Indexer,
            IPropertySymbol                     => SymbolSearchKind.Property,
            IFieldSymbol                        => SymbolSearchKind.Field,
            IEventSymbol                        => SymbolSearchKind.Event,
            _                                   => SymbolSearchKind.Other,
        };

    static string GetKind(SymbolSearchKind kind)
        => kind switch
        {
            SymbolSearchKind.Namespace         => "namespace",
            SymbolSearchKind.Class             => "class",
            SymbolSearchKind.Interface         => "interface",
            SymbolSearchKind.Struct            => "struct",
            SymbolSearchKind.Record            => "record",
            SymbolSearchKind.Enum              => "enum",
            SymbolSearchKind.Delegate          => "delegate",
            SymbolSearchKind.Type              => "type",
            SymbolSearchKind.Constructor       => "constructor",
            SymbolSearchKind.StaticConstructor => "static_constructor",
            SymbolSearchKind.Operator          => "operator",
            SymbolSearchKind.Conversion        => "conversion",
            SymbolSearchKind.Destructor        => "destructor",
            SymbolSearchKind.Method            => "method",
            SymbolSearchKind.Indexer           => "indexer",
            SymbolSearchKind.Property          => "property",
            SymbolSearchKind.Field             => "field",
            SymbolSearchKind.Event             => "event",
            _                                  => "symbol",
        };

    static bool IsTypeKind(SymbolSearchKind kind)
        => kind is SymbolSearchKind.Class
            or SymbolSearchKind.Interface
            or SymbolSearchKind.Struct
            or SymbolSearchKind.Record
            or SymbolSearchKind.Enum
            or SymbolSearchKind.Delegate
            or SymbolSearchKind.Type;
}

enum SymbolSearchKind : byte
{
    Other,
    Namespace,
    Class,
    Interface,
    Struct,
    Record,
    Enum,
    Delegate,
    Type,
    Constructor,
    StaticConstructor,
    Operator,
    Conversion,
    Destructor,
    Method,
    Indexer,
    Property,
    Field,
    Event,
}

sealed class SymbolResolution
{
    SymbolResolution(bool success, string? error, SymbolSearchEntry? entry, string[] candidates)
    {
        Success = success;
        Error = error;
        Entry = entry;
        Candidates = candidates;
    }

    public bool Success { get; }

    public string? Error { get; }

    public SymbolSearchEntry? Entry { get; }

    public string[] Candidates { get; }

    public static SymbolResolution Resolved(SymbolSearchEntry entry)
        => new(true, null, entry, []);

    public static SymbolResolution NotFound(string error)
        => new(false, error, null, []);

    public static SymbolResolution Ambiguous(string error, IEnumerable<string> candidates)
        => new(false, error, null, candidates.AsValueEnumerable().OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray());
}

sealed class ResolvedSymbolResolution
{
    ResolvedSymbolResolution(bool success, string? error, ResolvedSymbol? resolved, string[] candidates)
    {
        Success = success;
        Error = error;
        Resolved = resolved;
        Candidates = candidates;
    }

    public bool Success { get; }

    public string? Error { get; }

    public ResolvedSymbol? Resolved { get; }

    public string[] Candidates { get; }

    public static ResolvedSymbolResolution Found(ResolvedSymbol resolved)
        => new(true, null, resolved, []);

    public static ResolvedSymbolResolution NotFound(string error)
        => new(false, error, null, []);

    public static ResolvedSymbolResolution Ambiguous(string error, IEnumerable<string> candidates)
        => new(false, error, null, candidates.AsValueEnumerable().OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray());
}
