using Microsoft.CodeAnalysis;

namespace RoslynQuery;

sealed class WorkspaceSymbolIndex
{
    readonly SymbolSearchEntry[] entries;
    readonly Dictionary<string, SymbolSearchEntry[]> byCanonical;
    readonly Dictionary<string, SymbolSearchEntry[]> byDisplay;
    readonly Dictionary<string, SymbolSearchEntry[]> bySimpleName;

    WorkspaceSymbolIndex(SymbolSearchEntry[] entries)
    {
        this.entries = entries;
        byCanonical = BuildLookup(entries, static entry => entry.CanonicalSignature);
        byDisplay = BuildLookup(entries, static entry => entry.DisplaySignature);
        bySimpleName = BuildLookup(entries, static entry => entry.ShortName);
    }

    public static async Task<WorkspaceSymbolIndex> BuildAsync(Solution solution, CancellationToken ct)
    {
        var entries = new List<SymbolSearchEntry>();

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            CollectNamespace(project, compilation.Assembly.GlobalNamespace, entries);
        }

        return new(entries
            .AsValueEnumerable()
            .OrderBy(static entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.DisplaySignature, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public SymbolResolution Resolve(string query, string? kindFilter = null)
    {
        var normalizedKind = kindFilter is null ? null : NormalizeKind(kindFilter);
        if (string.IsNullOrWhiteSpace(query))
            return SymbolResolution.NotFound("Symbol is required.");

        var normalizedQuery = QueryToTrimmedValue(query);
        var exactCanonical = FilterCandidates(byCanonical, normalizedQuery, normalizedKind);
        if (exactCanonical.Length == 1)
            return SymbolResolution.Resolved(exactCanonical[0]);
        if (exactCanonical.Length > 1)
            return SymbolResolution.Ambiguous($"'{query}' is ambiguous.", exactCanonical);

        var exactDisplay = FilterCandidates(byDisplay, normalizedQuery, normalizedKind);
        if (exactDisplay.Length == 1)
            return SymbolResolution.Resolved(exactDisplay[0]);
        if (exactDisplay.Length > 1)
            return SymbolResolution.Ambiguous($"'{query}' is ambiguous.", exactDisplay);

        var exactSimple = FilterCandidates(bySimpleName, normalizedQuery, normalizedKind);
        if (exactSimple.Length == 1)
            return SymbolResolution.Resolved(exactSimple[0]);
        if (exactSimple.Length > 1)
            return SymbolResolution.Ambiguous($"'{query}' is ambiguous.", exactSimple);

        var fuzzyMatches = Search(normalizedQuery, normalizedKind ?? "all", 10);
        if (fuzzyMatches.Length == 1)
            return SymbolResolution.Resolved(fuzzyMatches[0].Entry);

        if (fuzzyMatches.Length > 1 && fuzzyMatches[0].Score < fuzzyMatches[1].Score)
            return SymbolResolution.Resolved(fuzzyMatches[0].Entry);

        if (fuzzyMatches.Length > 1)
            return SymbolResolution.Ambiguous($"'{query}' is ambiguous.", fuzzyMatches.AsValueEnumerable().Select(static item => item.Entry).ToArray());

        return SymbolResolution.NotFound($"'{query}' did not resolve to a symbol.");
    }

    public SymbolSearchEntry? TryGetBySymbol(ISymbol symbol)
        => entries.AsValueEnumerable().FirstOrDefault(entry => SymbolEqualityComparer.Default.Equals(entry.Symbol, symbol));

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
            entries.Add(SymbolSearchEntry.Create(project, symbol));

        foreach (var namespaceMember in symbol.GetNamespaceMembers())
            CollectNamespace(project, namespaceMember, entries);

        foreach (var typeMember in symbol.GetTypeMembers())
            CollectType(project, typeMember, entries);
    }

    static void CollectType(Project project, INamedTypeSymbol symbol, List<SymbolSearchEntry> entries)
    {
        if (HasSourceLocation(symbol))
            entries.Add(SymbolSearchEntry.Create(project, symbol));

        foreach (var member in symbol.GetMembers())
        {
            if (member is INamedTypeSymbol nestedType)
            {
                CollectType(project, nestedType, entries);
                continue;
            }

            if (!ShouldIndexMember(member) || !HasSourceLocation(member))
                continue;

            entries.Add(SymbolSearchEntry.Create(project, member));
        }
    }

    static bool HasSourceLocation(ISymbol symbol)
        => symbol.Locations.AsValueEnumerable().Any(static location => location.IsInSource);

    static Dictionary<string, SymbolSearchEntry[]> BuildLookup(IEnumerable<SymbolSearchEntry> entries, Func<SymbolSearchEntry, string> keySelector)
    {
        var groups = new Dictionary<string, List<SymbolSearchEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = keySelector(entry);
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = [];
                groups.Add(key, bucket);
            }

            bucket.Add(entry);
        }

        var lookup = new Dictionary<string, SymbolSearchEntry[]>(groups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            lookup.Add(group.Key, [.. group.Value]);

        return lookup;
    }

    static SymbolSearchEntry[] FilterCandidates(Dictionary<string, SymbolSearchEntry[]> source, string query, string? kindFilter)
    {
        if (!source.TryGetValue(query, out var candidates))
            return [];

        return kindFilter is null
            ? candidates
            : candidates.AsValueEnumerable().Where(candidate => candidate.MatchesKind(kindFilter)).ToArray();
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
}

sealed class SymbolSearchEntry
{
    SymbolSearchEntry(
        ISymbol symbol,
        string canonicalSignature,
        string displaySignature,
        string shortName,
        string kind,
        string? typeKind,
        string project,
        string? projectPath,
        string? containingNamespace,
        string? containingType,
        SourceLocationInfo[] locations)
    {
        Symbol = symbol;
        CanonicalSignature = canonicalSignature;
        DisplaySignature = displaySignature;
        ShortName = shortName;
        Kind = kind;
        TypeKind = typeKind;
        Project = project;
        ProjectPath = projectPath;
        ContainingNamespace = containingNamespace;
        ContainingType = containingType;
        Locations = locations;
    }

    public ISymbol Symbol { get; }

    public string CanonicalSignature { get; }

    public string DisplaySignature { get; }

    public string ShortName { get; }

    public string Kind { get; }

    public string? TypeKind { get; }

    public string Project { get; }

    public string? ProjectPath { get; }

    public string? ContainingNamespace { get; }

    public string? ContainingType { get; }

    public SourceLocationInfo[] Locations { get; }

    public static SymbolSearchEntry Create(Project project, ISymbol symbol)
    {
        var displaySignature = SymbolText.GetDisplaySignature(symbol);
        return new(
            symbol,
            $"{project.Name}::{displaySignature}",
            displaySignature,
            SymbolText.GetShortName(symbol),
            SymbolText.GetKind(symbol),
            SymbolText.GetTypeKind(symbol),
            project.Name,
            project.FilePath,
            SymbolText.GetContainingNamespace(symbol),
            SymbolText.GetContainingType(symbol),
            symbol.Locations
                .AsValueEnumerable()
                .Where(static location => location.IsInSource)
                .Select(location => SymbolFactory.ToLocation(location, lineText: null))
                .ToArray());
    }

    public static SymbolSearchEntry CreateMetadata(ISymbol symbol)
    {
        var displaySignature = SymbolText.GetDisplaySignature(symbol);
        return new(
            symbol,
            $"{symbol.ContainingAssembly?.Name ?? "metadata"}::{displaySignature}",
            displaySignature,
            SymbolText.GetShortName(symbol),
            SymbolText.GetKind(symbol),
            SymbolText.GetTypeKind(symbol),
            symbol.ContainingAssembly?.Name ?? "metadata",
            null,
            SymbolText.GetContainingNamespace(symbol),
            SymbolText.GetContainingType(symbol),
            []);
    }

    public int GetMatchScore(string query, string kind)
    {
        if (!MatchesKind(kind))
            return -1;

        if (string.Equals(ShortName, query, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(DisplaySignature, query, StringComparison.OrdinalIgnoreCase) || DisplaySignature.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (ShortName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (DisplaySignature.StartsWith(query, StringComparison.OrdinalIgnoreCase) || DisplaySignature.Contains("." + query, StringComparison.OrdinalIgnoreCase))
            return 3;

        if (ShortName.Contains(query, StringComparison.OrdinalIgnoreCase) || DisplaySignature.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;

        return -1;
    }

    public bool MatchesKind(string kind)
        => kind switch
        {
            "all" => true,
            "type" => Symbol is INamedTypeSymbol,
            "member" => Symbol is not INamespaceSymbol and not INamedTypeSymbol,
            _ => string.Equals(kind, Kind, StringComparison.OrdinalIgnoreCase) || string.Equals(kind, TypeKind, StringComparison.OrdinalIgnoreCase),
        };

    public SymbolSummary ToSummary()
        => new()
        {
            CanonicalSignature = CanonicalSignature,
            DisplaySignature = DisplaySignature,
            ShortName = ShortName,
            Kind = Kind,
            TypeKind = TypeKind,
            Project = Project,
            ProjectPath = ProjectPath,
            ContainingNamespace = ContainingNamespace,
            ContainingType = ContainingType,
            ReturnType = Symbol is IMethodSymbol method && method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor)
                ? SymbolText.GetTypeDisplay(method.ReturnType)
                : null,
            ValueType = Symbol switch
            {
                IPropertySymbol property => SymbolText.GetTypeDisplay(property.Type),
                IFieldSymbol field => SymbolText.GetTypeDisplay(field.Type),
                IEventSymbol @event => SymbolText.GetTypeDisplay(@event.Type),
                _ => null,
            },
            Locations = Locations,
        };
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

    public static SymbolResolution Ambiguous(string error, IEnumerable<SymbolSearchEntry> entries)
        => new(false, error, null, entries.AsValueEnumerable().Select(static entry => entry.CanonicalSignature).OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray());
}
