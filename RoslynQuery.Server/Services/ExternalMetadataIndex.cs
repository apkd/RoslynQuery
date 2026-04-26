using System.Collections.Immutable;
using System.Globalization;
using Cysharp.Text;
using Microsoft.CodeAnalysis;
using static System.StringComparison;

namespace RoslynQuery;

sealed class ExternalMetadataIndex
{
    static readonly SymbolDisplayFormat fullTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    static readonly Dictionary<string, string> specialTypeMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["char"] = "System.Char",
        ["decimal"] = "System.Decimal",
        ["double"] = "System.Double",
        ["float"] = "System.Single",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["object"] = "System.Object",
        ["string"] = "System.String",
        ["void"] = "System.Void",
    };

    readonly Lock buildGate = new();
    Task<ExternalMetadataSnapshot>? snapshotTask;
    ExternalMetadataSnapshot? snapshot;

    public string? GetAssemblyName(int assemblyIndex)
    {
        var current = snapshot;
        return current is not null && (uint)assemblyIndex < (uint)current.Assemblies.Length
            ? current.Assemblies[assemblyIndex].Name
            : null;
    }

    public string? GetAssemblyPath(int assemblyIndex)
    {
        var current = snapshot;
        return current is not null && (uint)assemblyIndex < (uint)current.Assemblies.Length
            ? current.Assemblies[assemblyIndex].AssemblyPath
            : null;
    }

    public int TryGetAssemblyIndex(ISymbol symbol)
    {
        var current = snapshot;
        var assembly = symbol.ContainingAssembly;
        return current is not null
            && assembly is not null
            && current.AssemblyIndexesByIdentity.TryGetValue(GetAssemblyIdentityKey(assembly), out var index)
            ? index
            : -1;
    }

    public string? TryGetAssemblyPath(ISymbol symbol)
    {
        var current = snapshot;
        var assembly = symbol.ContainingAssembly;
        return current is not null
            && assembly is not null
            && current.AssemblyPathsByIdentity.TryGetValue(GetAssemblyIdentityKey(assembly), out var path)
            ? path
            : null;
    }

    public async Task<ResolvedSymbolResolution> ResolveAsync(Solution solution, string query, string? kindFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ResolvedSymbolResolution.NotFound($"'{query}' did not resolve to a symbol.");

        var parsed = ParseAssemblyFilter(query);
        if (parsed.SymbolQuery.Length is 0)
            return ResolvedSymbolResolution.NotFound("Symbol is required.");

        var current = await GetSnapshotAsync(solution, ct);
        if (current.Assemblies.Length is 0)
            return ResolvedSymbolResolution.NotFound($"'{query}' did not resolve to a symbol.");

        var kind = string.IsNullOrWhiteSpace(kindFilter) ? "all" : kindFilter.Trim();
        var matches = (await ResolveCoreAsync(current, solution, parsed.SymbolQuery, parsed.AssemblyFilter, kind, ct))
            .AsValueEnumerable()
            .OrderBy(match => GetCanonicalSignature(current, match.Entry), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length switch
        {
            0 => ResolvedSymbolResolution.NotFound($"'{query}' did not resolve to a symbol."),
            1 => ResolvedSymbolResolution.Found(matches[0]),
            _ => ResolvedSymbolResolution.Ambiguous(
                $"'{query}' is ambiguous.",
                matches.AsValueEnumerable().Select(match => GetCanonicalSignature(current, match.Entry)).ToArray()
            ),
        };
    }

    public async Task<ResolvedSymbol?> ResolveAsync(Solution solution, SymbolSearchEntry entry, CancellationToken ct)
    {
        if (!entry.IsMetadata)
            return null;

        var current = await GetSnapshotAsync(solution, ct);
        if ((uint)entry.ExternalAssemblyIndex >= (uint)current.Assemblies.Length)
            return null;

        var matches = await ResolveCoreAsync(
            current,
            solution,
            entry.DisplaySignature,
            current.Assemblies[entry.ExternalAssemblyIndex].IdentityKey,
            "all",
            ct);
        foreach (var match in matches)
            if (string.Equals(match.Entry.DisplaySignature, entry.DisplaySignature, Ordinal))
                return match;

        return null;
    }

    async Task<ResolvedSymbol[]> ResolveCoreAsync(
        ExternalMetadataSnapshot current,
        Solution solution,
        string query,
        string? assemblyFilter,
        string kind,
        CancellationToken ct)
    {
        if (KindMatchesCategory(kind, "type"))
        {
            var typeMatches = (await ResolveTypesAsync(current, solution, query, assemblyFilter, ct))
                .AsValueEnumerable()
                .Select(type => CreateResolvedSymbol(type.Symbol, type.AssemblyIndex))
                .Where(resolved => resolved.Entry.MatchesKind(kind))
                .ToArray();

            if (typeMatches.Length > 0 || !KindMatchesCategory(kind, "member"))
                return typeMatches;
        }

        if (!KindMatchesCategory(kind, "member"))
            return [];

        return await ResolveMembersAsync(current, solution, query, assemblyFilter, kind, ct);
    }

    async Task<ResolvedSymbol[]> ResolveMembersAsync(
        ExternalMetadataSnapshot current,
        Solution solution,
        string query,
        string? assemblyFilter,
        string kind,
        CancellationToken ct)
    {
        foreach (var splitIndex in EnumerateMemberSplitIndexes(query))
        {
            var typeQuery = query[..splitIndex].Trim();
            var memberQuery = query[(splitIndex + 1)..].Trim();
            if (typeQuery.Length is 0 || memberQuery.Length is 0)
                continue;

            var types = await ResolveTypesAsync(current, solution, typeQuery, assemblyFilter, ct);
            if (types.Length is 0)
                continue;

            var parsedMember = ParsedMemberQuery.Parse(memberQuery);
            if (parsedMember.Name.Length is 0)
                continue;

            var entries = new List<ResolvedSymbol>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            foreach (var member in type.Symbol.GetMembers(parsedMember.Name))
            {
                if (!WorkspaceSymbolIndex.ShouldIndexMember(member) || !MemberMatches(member, parsedMember))
                    continue;

                var resolved = CreateResolvedSymbol(member, type.AssemblyIndex);
                if (!resolved.Entry.MatchesKind(kind))
                    continue;

                if (seen.Add(GetSymbolKey(member, resolved.Entry)))
                    entries.Add(resolved);
            }

            if (entries.Count > 0)
                return [.. entries];
        }

        return [];
    }

    async Task<(INamedTypeSymbol Symbol, int AssemblyIndex)[]> ResolveTypesAsync(
        ExternalMetadataSnapshot current,
        Solution solution,
        string query,
        string? assemblyFilter,
        CancellationToken ct)
    {
        var metadataNames = GetTypeMetadataNameCandidates(query);
        var typePathCandidates = GetNamespaceTypePathCandidates(query);
        if (metadataNames.Length is 0 && typePathCandidates.Length is 0)
            return [];

        var results = new List<(INamedTypeSymbol Symbol, int AssemblyIndex)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compilationCache = new Dictionary<ProjectId, Compilation?>();
        for (var assemblyIndex = 0; assemblyIndex < current.Assemblies.Length; assemblyIndex++)
        {
            var descriptor = current.Assemblies[assemblyIndex];
            if (!descriptor.MatchesFilter(assemblyFilter))
                continue;

            var assembly = await GetAssemblySymbolAsync(current, solution, assemblyIndex, compilationCache, ct);
            if (assembly is null)
                continue;

            foreach (var metadataName in metadataNames)
            {
                var symbol = assembly.GetTypeByMetadataName(metadataName);
                if (symbol is null && !metadataName.Contains('+'))
                    symbol = assembly.ResolveForwardedType(metadataName);

                if (symbol is null)
                    continue;

                var key = descriptor.IdentityKey + "\n" + SymbolText.GetDisplaySignature(symbol);
                if (seen.Add(key))
                    results.Add((symbol, assemblyIndex));
            }

            foreach (var symbol in ResolveTypesByNamespaceTraversal(assembly, typePathCandidates))
            {
                var key = descriptor.IdentityKey + "\n" + SymbolText.GetDisplaySignature(symbol);
                if (seen.Add(key))
                    results.Add((symbol, assemblyIndex));
            }
        }

        return [.. results];
    }

    static INamedTypeSymbol[] ResolveTypesByNamespaceTraversal(
        IAssemblySymbol assembly,
        (string Namespace, string TypePath)[] candidates
    )
    {
        if (candidates.Length is 0)
            return [];

        var results = new List<INamedTypeSymbol>();
        foreach (var candidate in candidates)
        {
            var @namespace = FindNamespace(assembly.GlobalNamespace, candidate.Namespace);
            if (@namespace is null)
                continue;

            AddMatchingTypes(@namespace, candidate.TypePath, results);
        }

        return [.. results];
    }

    (string Namespace, string TypePath)[] GetNamespaceTypePathCandidates(string query)
    {
        var normalized = NormalizeTypeQuery(query);
        if (normalized.Length is 0 || normalized.Contains('(') || normalized.Contains(')'))
            return [];

        if (specialTypeMetadataNames.ContainsKey(normalized))
            return [];

        var results = new List<(string Namespace, string TypePath)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddUngatedNamespaceTypePathCandidates(normalized, results, seen);
        return [.. results];
    }

    static void AddUngatedNamespaceTypePathCandidates(
        string normalized,
        List<(string Namespace, string TypePath)> results,
        HashSet<string> seen
    )
    {
        var separatorIndexes = GetTopLevelTypePathSeparatorIndexes(normalized);
        for (int i = separatorIndexes.Length - 1; i >= 0; i--)
        {
            var separatorIndex = separatorIndexes[i];
            var namespacePart = normalized[..separatorIndex].Trim();
            var typePath = normalized[(separatorIndex + 1)..].Trim();
            if (namespacePart.Length is 0 || typePath.Length is 0)
                continue;

            var key = namespacePart + "\n" + typePath;
            if (seen.Add(key))
                results.Add((namespacePart, typePath));
        }
    }

    static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string namespaceName)
    {
        var current = root;
        foreach (var part in namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            INamespaceSymbol? next = null;
            foreach (var namespaceMember in current.GetNamespaceMembers())
            {
                if (!string.Equals(namespaceMember.Name, part, Ordinal))
                    continue;

                next = namespaceMember;
                break;
            }

            if (next is null)
                return null;

            current = next;
        }

        return current;
    }

    static void AddMatchingTypes(INamespaceOrTypeSymbol container, string typePath, List<INamedTypeSymbol> results)
    {
        var segments = SplitTypePath(typePath);
        if (segments.Length is 0)
            return;

        AddMatchingTypes(container, segments, segmentIndex: 0, results);
    }

    static void AddMatchingTypes(INamespaceOrTypeSymbol container, string[] segments, int segmentIndex, List<INamedTypeSymbol> results)
    {
        var (name, arity) = ParseTypeLookupSegment(segments[segmentIndex]);
        if (name.Length is 0)
            return;

        foreach (var type in container.GetTypeMembers(name, arity))
        {
            if (segmentIndex == segments.Length - 1)
            {
                results.Add(type);
                continue;
            }

            AddMatchingTypes(type, segments, segmentIndex + 1, results);
        }
    }

    static (string Name, int Arity) ParseTypeLookupSegment(string segment)
    {
        var genericStart = FindTopLevelGenericStart(segment);
        if (genericStart >= 0)
        {
            var genericEnd = FindMatchingGenericEnd(segment, genericStart);
            return genericEnd < 0
                ? (segment[..genericStart].Trim(), 0)
                : (segment[..genericStart].Trim(), CountGenericArguments(segment[(genericStart + 1)..genericEnd]));
        }

        var aritySeparatorIndex = segment.IndexOf('`');
        if (aritySeparatorIndex < 0)
            return (segment.Trim(), 0);

        var name = segment[..aritySeparatorIndex].Trim();
        return int.TryParse(segment[(aritySeparatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var arity)
            ? (name, arity)
            : (name, 0);
    }

    string[] GetTypeMetadataNameCandidates(string query)
    {
        var normalized = NormalizeTypeQuery(query);
        if (normalized.Length is 0)
            return [];

        if (specialTypeMetadataNames.TryGetValue(normalized, out var specialType))
            return [specialType];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddUngatedTypeMetadataNameCandidates(normalized, results, seen);
        return [.. results];
    }

    static void AddUngatedTypeMetadataNameCandidates(string normalized, List<string> results, HashSet<string> seen)
    {
        if (normalized.Contains('(') || normalized.Contains(')'))
            return;

        var separatorIndexes = GetTopLevelTypePathSeparatorIndexes(normalized);
        if (separatorIndexes.Length is 0)
            return;

        for (int i = separatorIndexes.Length - 1; i >= 0; i--)
        {
            var separatorIndex = separatorIndexes[i];
            var namespacePart = normalized[..separatorIndex].Trim();
            var typePath = normalized[(separatorIndex + 1)..].Trim();
            if (namespacePart.Length is 0 || typePath.Length is 0)
                continue;

            var metadataTypePath = ToMetadataTypePath(typePath);
            if (metadataTypePath.Length is 0)
                continue;

            var metadataName = namespacePart + "." + metadataTypePath;
            if (seen.Add(metadataName))
                results.Add(metadataName);
        }
    }

    static int[] GetTopLevelTypePathSeparatorIndexes(string value)
    {
        var indexes = new List<int>();
        var depth = 0;
        for (int i = 0; i < value.Length; i++)
        {
            depth = value[i] switch
            {
                '<'                => depth + 1,
                '>' when depth > 0 => depth - 1,
                _                  => depth,
            };

            if (value[i] == '.' && depth is 0)
                indexes.Add(i);
        }

        return [.. indexes];
    }

    ResolvedSymbol CreateResolvedSymbol(ISymbol symbol, int assemblyIndex)
        => new(SymbolSearchEntry.CreateMetadata(symbol, assemblyIndex), symbol);

    static string GetCanonicalSignature(ExternalMetadataSnapshot current, SymbolSearchEntry entry)
        => $"{GetAssemblyName(current, entry.ExternalAssemblyIndex) ?? "metadata"}::{entry.DisplaySignature}";

    static string? GetAssemblyName(ExternalMetadataSnapshot current, int assemblyIndex)
        => (uint)assemblyIndex < (uint)current.Assemblies.Length ? current.Assemblies[assemblyIndex].Name : null;

    static IEnumerable<int> EnumerateMemberSplitIndexes(string query)
    {
        var end = query.IndexOf('(');
        if (end < 0)
            end = query.Length;

        var depth = 0;
        var indexes = new List<int>();
        for (int i = 0; i < end; i++)
        {
            depth = query[i] switch
            {
                '<'                => depth + 1,
                '>' when depth > 0 => depth - 1,
                _                  => depth,
            };

            if (query[i] == '.' && depth is 0)
                indexes.Add(i);
        }

        for (int i = indexes.Count - 1; i >= 0; i--)
            yield return indexes[i];
    }

    static bool MemberMatches(ISymbol member, ParsedMemberQuery query)
    {
        if (query.GenericArity is { } genericArity)
        {
            if (member is not IMethodSymbol method || method.TypeParameters.Length != genericArity)
                return false;
        }

        if (query.Parameters is null)
            return true;

        return member switch
        {
            IMethodSymbol method                            => ParametersMatch(method.Parameters, query.Parameters),
            IPropertySymbol { IsIndexer: true } property    => ParametersMatch(property.Parameters, query.Parameters),
            IPropertySymbol or IFieldSymbol or IEventSymbol => query.Parameters.Length is 0,
            _                                               => false,
        };
    }

    static bool ParametersMatch(ImmutableArray<IParameterSymbol> parameters, string[] queryParameters)
    {
        if (parameters.Length != queryParameters.Length)
            return false;

        for (int i = 0; i < parameters.Length; i++)
            if (!ParameterTypeMatches(queryParameters[i], parameters[i].Type))
                return false;

        return true;
    }

    static bool ParameterTypeMatches(string queryParameter, ITypeSymbol parameterType)
    {
        var queryType = NormalizeTypeForComparison(RemoveParameterName(queryParameter));
        var displayType = NormalizeTypeForComparison(SymbolText.GetTypeDisplay(parameterType));
        if (string.Equals(queryType, displayType, OrdinalIgnoreCase))
            return true;

        var fullType = NormalizeTypeForComparison(parameterType.ToDisplayString(fullTypeFormat));
        return string.Equals(queryType, fullType, OrdinalIgnoreCase);
    }

    static string RemoveParameterName(string parameter)
    {
        var value = StripLeadingParameterModifiers(parameter.Trim());
        var splitIndex = FindLastTopLevelSpace(value);
        if (splitIndex < 0)
            return value;

        var possibleName = value[(splitIndex + 1)..].Trim();
        return IsIdentifier(possibleName)
            ? value[..splitIndex].Trim()
            : value;
    }

    static string StripLeadingParameterModifiers(string value)
    {
        while (true)
        {
            var splitIndex = value.IndexOf(' ');
            if (splitIndex < 0)
                return value;

            var token = value[..splitIndex];
            if (token is not ("ref" or "out" or "in" or "params" or "scoped"))
                return value;

            value = value[(splitIndex + 1)..].TrimStart();
        }
    }

    static int FindLastTopLevelSpace(string value)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            switch (value[i])
            {
                case '>':
                    angleDepth++;
                    break;
                case '<' when angleDepth > 0:
                    angleDepth--;
                    break;
                case ')':
                    parenDepth++;
                    break;
                case '(' when parenDepth > 0:
                    parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case ' ' when angleDepth is 0 && parenDepth is 0 && bracketDepth is 0:
                    return i;
            }
        }

        return -1;
    }

    static bool IsIdentifier(string value)
    {
        if (value.StartsWith('@'))
            value = value[1..];

        if (value.Length is 0 || !IsIdentifierStart(value[0]))
            return false;

        foreach (var character in value.AsSpan()[1..])
            if (!IsIdentifierPart(character))
                return false;

        return true;
    }

    static bool IsIdentifierStart(char value)
        => value == '_' || char.IsLetter(value);

    static bool IsIdentifierPart(char value)
        => value == '_' || char.IsLetterOrDigit(value);

    static string NormalizeTypeForComparison(string value)
    {
        var normalized = RemoveWhitespace(NormalizeTypeQuery(value));
        return specialTypeMetadataNames.TryGetValue(normalized, out var specialType)
            ? specialType
            : normalized;
    }

    static string NormalizeTypeQuery(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("global::", Ordinal))
            trimmed = trimmed["global::".Length..];

        while (trimmed.EndsWith('?'))
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed;
    }

    static string RemoveWhitespace(string value)
    {
        if (value.AsSpan().IndexOfAny([' ', '\t', '\r', '\n']) < 0)
            return value;

        var builder = ZString.CreateStringBuilder();
        try
        {
            foreach (var character in value)
                if (!char.IsWhiteSpace(character))
                    builder.Append(character);

            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string ToMetadataTypePath(string typePath)
    {
        var segments = SplitTypePath(typePath);
        if (segments.Length is 0)
            return string.Empty;

        var metadataSegments = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = ToMetadataTypeSegment(segments[i]);
            if (segment.Length is 0)
                return string.Empty;

            metadataSegments[i] = segment;
        }

        return string.Join('+', metadataSegments);
    }

    static string[] SplitTypePath(string value)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            depth = value[i] switch
            {
                '<'                => depth + 1,
                '>' when depth > 0 => depth - 1,
                _                  => depth,
            };

            if (depth is not 0 || value[i] is not ('.' or '+'))
                continue;

            AddSegment(value[start..i]);
            start = i + 1;
        }

        AddSegment(value[start..]);
        return [.. segments];

        void AddSegment(string segment)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length > 0)
                segments.Add(trimmed);
        }
    }

    static string ToMetadataTypeSegment(string segment)
    {
        var genericStart = FindTopLevelGenericStart(segment);
        if (genericStart < 0)
            return segment.Trim();

        var name = segment[..genericStart].Trim();
        if (name.Length is 0)
            return string.Empty;

        var genericEnd = FindMatchingGenericEnd(segment, genericStart);
        if (genericEnd < 0)
            return string.Empty;

        var arity = CountGenericArguments(segment[(genericStart + 1)..genericEnd]);
        return arity <= 0 ? string.Empty : $"{name}`{arity.ToString(CultureInfo.InvariantCulture)}";
    }

    static int FindTopLevelGenericStart(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '<')
                return i;

        return -1;
    }

    static int FindMatchingGenericEnd(string value, int start)
    {
        var depth = 0;
        for (int i = start; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth is 0)
                        return i;

                    break;
            }
        }

        return -1;
    }

    static int CountGenericArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var depth = 0;
        var count = 1;
        foreach (var character in value)
        {
            depth = character switch
            {
                '<'                => depth + 1,
                '>' when depth > 0 => depth - 1,
                _                  => depth,
            };

            if (character == ',' && depth is 0)
                count++;
        }

        return count;
    }

    static bool KindMatchesCategory(string kind, string category)
        => category switch
        {
            "type"   => kind is "all" or "type" or "class" or "interface" or "struct" or "record" or "enum" or "delegate",
            "member" => kind is "all" or "member" or "method" or "property" or "indexer" or "field" or "event" or "operator" or "conversion",
            _        => false,
        };

    static (string? AssemblyFilter, string SymbolQuery) ParseAssemblyFilter(string query)
    {
        var trimmed = query.Trim();
        var separatorIndex = trimmed.IndexOf("::", Ordinal);
        return separatorIndex < 0
            ? (null, trimmed)
            : (trimmed[..separatorIndex].Trim(), trimmed[(separatorIndex + 2)..].Trim());
    }

    static string GetSymbolKey(ISymbol symbol, SymbolSearchEntry entry)
        => GetAssemblyIdentityKey(symbol.ContainingAssembly) + "\n" + entry.DisplaySignature;

    static string GetAssemblyIdentityKey(IAssemblySymbol? assembly)
        => assembly?.Identity.ToString() ?? "";

    async Task<IAssemblySymbol?> GetAssemblySymbolAsync(
        ExternalMetadataSnapshot current,
        Solution solution,
        int assemblyIndex,
        Dictionary<ProjectId, Compilation?> compilationCache,
        CancellationToken ct)
    {
        if ((uint)assemblyIndex >= (uint)current.Assemblies.Length)
            return null;

        var descriptor = current.Assemblies[assemblyIndex];
        if (descriptor.Symbol is not null)
            return descriptor.Symbol;

        if (!compilationCache.TryGetValue(descriptor.ProjectId, out var compilation))
        {
            var project = solution.GetProject(descriptor.ProjectId);
            compilation = project is null ? null : await project.GetCompilationAsync(ct);
            compilationCache.Add(descriptor.ProjectId, compilation);
        }

        if (compilation is null)
            return null;

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference portableReference)
                continue;

            if (!ReferenceLooksLikeDescriptor(portableReference, descriptor))
                continue;

            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
                && string.Equals(GetAssemblyIdentityKey(assembly), descriptor.IdentityKey, Ordinal))
                return assembly;
        }

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference)
                continue;

            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
                && string.Equals(GetAssemblyIdentityKey(assembly), descriptor.IdentityKey, Ordinal))
                return assembly;
        }

        return null;
    }

    static bool ReferenceLooksLikeDescriptor(PortableExecutableReference reference, ExternalAssemblyInfo descriptor)
        => !string.IsNullOrWhiteSpace(descriptor.AssemblyPath) && PathsEqual(reference.FilePath, descriptor.AssemblyPath)
           || !string.IsNullOrWhiteSpace(descriptor.Display) && string.Equals(reference.Display, descriptor.Display, Ordinal);

    static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? OrdinalIgnoreCase
            : Ordinal;
        return string.Equals(left, right, comparison);
    }

    async Task<ExternalMetadataSnapshot> GetSnapshotAsync(Solution solution, CancellationToken ct)
    {
        if (snapshot is { } current)
            return current;

        Task<ExternalMetadataSnapshot> task;
        lock (buildGate)
        {
            if (snapshot is { } initialized)
                return initialized;

            task = snapshotTask ??= BuildSnapshotAsync(solution, ct);
        }

        try
        {
            current = await task;
        }
        catch
        {
            lock (buildGate)
            {
                if (ReferenceEquals(snapshotTask, task))
                    snapshotTask = null;
            }

            throw;
        }

        lock (buildGate)
        {
            snapshot = current;
            if (ReferenceEquals(snapshotTask, task))
                snapshotTask = null;

            return current;
        }
    }

    static async Task<ExternalMetadataSnapshot> BuildSnapshotAsync(Solution solution, CancellationToken ct)
    {
        var builder = new Builder();
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;

            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null)
                builder.Add(compilation, project.Id);
        }

        return builder.BuildSnapshot();
    }

    sealed record ExternalMetadataSnapshot(
        ExternalAssemblyInfo[] Assemblies,
        Dictionary<string, string> AssemblyPathsByIdentity,
        Dictionary<string, int> AssemblyIndexesByIdentity);

    sealed record ExternalAssemblyInfo(
        IAssemblySymbol? Symbol,
        ProjectId ProjectId,
        string IdentityKey,
        string Name,
        string? AssemblyPath,
        string? Display)
    {
        public bool MatchesFilter(string? filter)
            => string.IsNullOrWhiteSpace(filter)
               || string.Equals(Name, filter, OrdinalIgnoreCase)
               || string.Equals(IdentityKey, filter, OrdinalIgnoreCase)
               || string.Equals(Display, filter, OrdinalIgnoreCase)
               || string.Equals(AssemblyPath, filter, OrdinalIgnoreCase);
    }

    sealed class MutableExternalAssemblyInfo
    {
        public MutableExternalAssemblyInfo(IAssemblySymbol symbol, ProjectId projectId, string identityKey, string name, string? assemblyPath, string? display)
        {
            Symbol = symbol;
            ProjectId = projectId;
            IdentityKey = identityKey;
            Name = name;
            AssemblyPath = assemblyPath;
            Display = display;
        }

        public IAssemblySymbol Symbol { get; }

        public ProjectId ProjectId { get; }

        public string IdentityKey { get; }

        public string Name { get; }

        public string? AssemblyPath { get; private set; }

        public string? Display { get; private set; }

        public void AddReferenceInfo(string? assemblyPath, string? display)
        {
            if (string.IsNullOrWhiteSpace(AssemblyPath) && !string.IsNullOrWhiteSpace(assemblyPath))
                AssemblyPath = assemblyPath;

            if (string.IsNullOrWhiteSpace(Display) && !string.IsNullOrWhiteSpace(display))
                Display = display;
        }

        public ExternalAssemblyInfo ToImmutable()
            => new(Symbol, ProjectId, IdentityKey, Name, AssemblyPath, Display);
    }

    readonly record struct ParsedMemberQuery(string Name, int? GenericArity, string[]? Parameters)
    {
        public static ParsedMemberQuery Parse(string value)
        {
            var openParenIndex = value.IndexOf('(');
            var rawName = openParenIndex < 0 ? value.Trim() : value[..openParenIndex].Trim();
            var genericArity = ParseGenericArity(rawName, out var name);
            if (openParenIndex < 0)
                return new(name, genericArity, null);

            var closeParenIndex = value.LastIndexOf(')');
            if (closeParenIndex < openParenIndex)
                return new(name, genericArity, []);

            var parameters = value[(openParenIndex + 1)..closeParenIndex].Trim();
            return new(
                name,
                genericArity,
                parameters.Length is 0
                    ? []
                    : SplitParameters(parameters)
            );
        }

        static int? ParseGenericArity(string rawName, out string name)
        {
            var genericStart = FindTopLevelGenericStart(rawName);
            if (genericStart < 0)
            {
                name = rawName;
                return null;
            }

            name = rawName[..genericStart].Trim();
            var genericEnd = FindMatchingGenericEnd(rawName, genericStart);
            return genericEnd < 0
                ? null
                : CountGenericArguments(rawName[(genericStart + 1)..genericEnd]);
        }

        static string[] SplitParameters(string value)
        {
            var parameters = new List<string>();
            var angleDepth = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '<':
                        angleDepth++;
                        break;
                    case '>' when angleDepth > 0:
                        angleDepth--;
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case ',' when angleDepth is 0 && parenDepth is 0 && bracketDepth is 0:
                        AddParameter(value[start..i]);
                        start = i + 1;
                        break;
                }
            }

            AddParameter(value[start..]);
            return [.. parameters];

            void AddParameter(string parameter)
            {
                var trimmed = parameter.Trim();
                if (trimmed.Length > 0)
                    parameters.Add(trimmed);
            }
        }
    }

    sealed class Builder
    {
        readonly Dictionary<string, MutableExternalAssemblyInfo> assemblies = new(StringComparer.Ordinal);

        internal void Add(Compilation compilation, ProjectId projectId)
        {
            foreach (var reference in compilation.References)
            {
                if (reference is not PortableExecutableReference portableReference)
                    continue;

                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                    continue;

                var assemblyPath = portableReference.FilePath;
                var identityKey = GetAssemblyIdentityKey(assembly);
                if (assemblies.TryGetValue(identityKey, out var existing))
                {
                    existing.AddReferenceInfo(assemblyPath, reference.Display);
                    continue;
                }

                assemblies.Add(identityKey, new(assembly, projectId, identityKey, assembly.Name, assemblyPath, reference.Display));
            }
        }

        internal ExternalMetadataSnapshot BuildSnapshot()
        {
            var immutableAssemblies = assemblies
                .Values
                .AsValueEnumerable()
                .Select(static item => item.ToImmutable())
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var assemblyPathsByIdentity = immutableAssemblies
                .AsValueEnumerable()
                .Where(static item => !string.IsNullOrWhiteSpace(item.AssemblyPath))
                .ToDictionary(static item => item.IdentityKey, static item => item.AssemblyPath!, StringComparer.Ordinal);

            var assemblyIndexesByIdentity = new Dictionary<string, int>(immutableAssemblies.Length, StringComparer.Ordinal);
            for (int i = 0; i < immutableAssemblies.Length; i++)
                assemblyIndexesByIdentity[immutableAssemblies[i].IdentityKey] = i;

            return new(
                immutableAssemblies,
                assemblyPathsByIdentity,
                assemblyIndexesByIdentity
            );
        }
    }
}
