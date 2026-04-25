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
        ExternalMetadataIndex externalMetadata)
    {
        this.entries = entries;
        this.sourceProjects = sourceProjects;
        this.byDisplay = byDisplay;
        this.bySimpleName = bySimpleName;
        ExternalMetadata = externalMetadata;
    }

    public ExternalMetadataIndex ExternalMetadata { get; }

    public static async Task<WorkspaceSymbolIndex> BuildAsync(Solution solution, CancellationToken ct)
    {
        var entries = new List<SymbolSearchEntry>();
        var sourceProjects = new Dictionary<ProjectId, SourceProjectInfo>();

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;

            sourceProjects[project.Id] = new(project.Name, project.FilePath);
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            CollectNamespace(project, compilation.Assembly.GlobalNamespace, entries);
        }

        var entryArray = entries.ToArray();
        return new(
            entryArray,
            sourceProjects,
            BuildLookup(entryArray, static entry => entry.DisplaySignature),
            BuildLookup(entryArray, static entry => entry.ShortName),
            new ExternalMetadataIndex());
    }

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
            ReturnType = symbol is IMethodSymbol method && method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor)
                ? SymbolText.GetTypeDisplay(method.ReturnType)
                : null,
            ValueType = symbol switch
            {
                IPropertySymbol property => SymbolText.GetTypeDisplay(property.Type),
                IFieldSymbol field => SymbolText.GetTypeDisplay(field.Type),
                IEventSymbol @event => SymbolText.GetTypeDisplay(@event.Type),
                _ => null,
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
            _ => true,
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

    static void CollectNamespace(Project project, INamespaceSymbol symbol, List<SymbolSearchEntry> entries)
    {
        if (!symbol.IsGlobalNamespace && HasSourceLocation(symbol))
            entries.Add(SymbolSearchEntry.CreateSource(project, symbol));

        foreach (var namespaceMember in symbol.GetNamespaceMembers())
            CollectNamespace(project, namespaceMember, entries);

        foreach (var typeMember in symbol.GetTypeMembers())
            CollectType(project, typeMember, entries);
    }

    static void CollectType(Project project, INamedTypeSymbol symbol, List<SymbolSearchEntry> entries)
    {
        if (HasSourceLocation(symbol))
            entries.Add(SymbolSearchEntry.CreateSource(project, symbol));

        foreach (var member in symbol.GetMembers())
        {
            if (member is INamedTypeSymbol nestedType)
            {
                CollectType(project, nestedType, entries);
                continue;
            }

            if (!ShouldIndexMember(member) || !HasSourceLocation(member))
                continue;

            entries.Add(SymbolSearchEntry.CreateSource(project, member));
        }
    }

    static bool HasSourceLocation(ISymbol symbol)
        => symbol.Locations.AsValueEnumerable().Any(static location => location.IsInSource);

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
            kindFilter);
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

    static Dictionary<string, int[]> BuildLookup(SymbolSearchEntry[] entries, Func<SymbolSearchEntry, string> keySelector)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            var key = keySelector(entries[i]);
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = [];
                groups.Add(key, bucket);
            }

            bucket.Add(i);
        }

        var lookup = new Dictionary<string, int[]>(groups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            lookup.Add(group.Key, [.. group.Value]);

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
            entries.AsValueEnumerable().Select(GetCanonicalSignature).ToArray());
}

readonly record struct SourceProjectInfo(string Name, string? FilePath);

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
    public bool IsSource => ProjectId is not null;

    public bool IsMetadata => ProjectId is null;

    public string ShortName => HasShortNameRange
        ? DisplaySignature.Substring(ShortNameStart, ShortNameLength)
        : SymbolText.GetShortName(Symbol);

    public string Kind => GetKind(SearchKind);

    public string? TypeKind => IsTypeKind(SearchKind) ? Kind : null;

    public string Origin => IsSource ? "source" : "metadata";

    bool HasShortNameRange => ShortNameStart >= 0;

    public static SymbolSearchEntry CreateSource(Project project, ISymbol symbol)
    {
        var (displaySignature, shortNameStart, shortNameLength, kind) = BuildSearchInfo(symbol);
        return new(symbol, project.Id, -1, displaySignature, shortNameStart, shortNameLength, kind);
    }

    public static SymbolSearchEntry CreateMetadata(ISymbol symbol, int externalAssemblyIndex)
    {
        var (displaySignature, shortNameStart, shortNameLength, kind) = BuildSearchInfo(symbol);
        return new(symbol, null, externalAssemblyIndex, displaySignature, shortNameStart, shortNameLength, kind);
    }

    static (string DisplaySignature, int ShortNameStart, int ShortNameLength, SymbolSearchKind Kind) BuildSearchInfo(ISymbol symbol)
    {
        var displaySignature = SymbolText.GetDisplaySignature(symbol);
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
            "all" => true,
            "type" => IsTypeKind(SearchKind),
            "member" => SearchKind is not SymbolSearchKind.Namespace && !IsTypeKind(SearchKind),
            _ => string.Equals(kind, Kind, OrdinalIgnoreCase),
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
            INamespaceSymbol => SymbolSearchKind.Namespace,
            INamedTypeSymbol namedType when namedType.IsRecord => SymbolSearchKind.Record,
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                Microsoft.CodeAnalysis.TypeKind.Class => SymbolSearchKind.Class,
                Microsoft.CodeAnalysis.TypeKind.Interface => SymbolSearchKind.Interface,
                Microsoft.CodeAnalysis.TypeKind.Struct => SymbolSearchKind.Struct,
                Microsoft.CodeAnalysis.TypeKind.Enum => SymbolSearchKind.Enum,
                Microsoft.CodeAnalysis.TypeKind.Delegate => SymbolSearchKind.Delegate,
                _ => SymbolSearchKind.Type,
            },
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Constructor => SymbolSearchKind.Constructor,
                MethodKind.StaticConstructor => SymbolSearchKind.StaticConstructor,
                MethodKind.UserDefinedOperator => SymbolSearchKind.Operator,
                MethodKind.Conversion => SymbolSearchKind.Conversion,
                MethodKind.Destructor => SymbolSearchKind.Destructor,
                _ => SymbolSearchKind.Method,
            },
            IPropertySymbol property when property.IsIndexer => SymbolSearchKind.Indexer,
            IPropertySymbol => SymbolSearchKind.Property,
            IFieldSymbol => SymbolSearchKind.Field,
            IEventSymbol => SymbolSearchKind.Event,
            _ => SymbolSearchKind.Other,
        };

    static string GetKind(SymbolSearchKind kind)
        => kind switch
        {
            SymbolSearchKind.Namespace => "namespace",
            SymbolSearchKind.Class => "class",
            SymbolSearchKind.Interface => "interface",
            SymbolSearchKind.Struct => "struct",
            SymbolSearchKind.Record => "record",
            SymbolSearchKind.Enum => "enum",
            SymbolSearchKind.Delegate => "delegate",
            SymbolSearchKind.Type => "type",
            SymbolSearchKind.Constructor => "constructor",
            SymbolSearchKind.StaticConstructor => "static_constructor",
            SymbolSearchKind.Operator => "operator",
            SymbolSearchKind.Conversion => "conversion",
            SymbolSearchKind.Destructor => "destructor",
            SymbolSearchKind.Method => "method",
            SymbolSearchKind.Indexer => "indexer",
            SymbolSearchKind.Property => "property",
            SymbolSearchKind.Field => "field",
            SymbolSearchKind.Event => "event",
            _ => "symbol",
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
