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
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

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

    readonly ExternalAssemblyInfo[] assemblies;
    readonly string[] namespaces;
    readonly Dictionary<string, string> assemblyPathsByIdentity;

    ExternalMetadataIndex(
        ExternalAssemblyInfo[] assemblies,
        string[] namespaces,
        Dictionary<string, string> assemblyPathsByIdentity)
    {
        this.assemblies = assemblies;
        this.namespaces = namespaces;
        this.assemblyPathsByIdentity = assemblyPathsByIdentity;
    }

    public string? TryGetAssemblyPath(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly;
        return assembly is not null && assemblyPathsByIdentity.TryGetValue(GetAssemblyIdentityKey(assembly), out var path)
            ? path
            : null;
    }

    public SymbolResolution Resolve(string query, string? kindFilter = null)
    {
        if (assemblies.Length is 0 || string.IsNullOrWhiteSpace(query))
            return SymbolResolution.NotFound($"'{query}' did not resolve to a symbol.");

        var parsed = ParseAssemblyFilter(query);
        if (parsed.SymbolQuery.Length is 0)
            return SymbolResolution.NotFound("Symbol is required.");

        var kind = string.IsNullOrWhiteSpace(kindFilter) ? "all" : kindFilter.Trim();
        var matches = ResolveCore(parsed.SymbolQuery, parsed.AssemblyFilter, kind)
            .AsValueEnumerable()
            .OrderBy(static entry => entry.CanonicalSignature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length switch
        {
            0 => SymbolResolution.NotFound($"'{query}' did not resolve to a symbol."),
            1 => SymbolResolution.Resolved(matches[0]),
            _ => SymbolResolution.Ambiguous($"'{query}' is ambiguous.", matches),
        };
    }

    SymbolSearchEntry[] ResolveCore(string query, string? assemblyFilter, string kind)
    {
        if (KindMatchesCategory(kind, "type"))
        {
            var typeMatches = ResolveTypes(query, assemblyFilter)
                .AsValueEnumerable()
                .Select(symbol => CreateEntry(symbol))
                .Where(entry => entry.MatchesKind(kind))
                .ToArray();

            if (typeMatches.Length > 0 || !KindMatchesCategory(kind, "member"))
                return typeMatches;
        }

        if (!KindMatchesCategory(kind, "member"))
            return [];

        return ResolveMembers(query, assemblyFilter, kind);
    }

    SymbolSearchEntry[] ResolveMembers(string query, string? assemblyFilter, string kind)
    {
        foreach (var splitIndex in EnumerateMemberSplitIndexes(query))
        {
            var typeQuery = query[..splitIndex].Trim();
            var memberQuery = query[(splitIndex + 1)..].Trim();
            if (typeQuery.Length is 0 || memberQuery.Length is 0)
                continue;

            var types = ResolveTypes(typeQuery, assemblyFilter);
            if (types.Length is 0)
                continue;

            var parsedMember = ParsedMemberQuery.Parse(memberQuery);
            if (parsedMember.Name.Length is 0)
                continue;

            var entries = new List<SymbolSearchEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            foreach (var member in type.GetMembers(parsedMember.Name))
            {
                if (!WorkspaceSymbolIndex.ShouldIndexMember(member) || !MemberMatches(member, parsedMember))
                    continue;

                var entry = CreateEntry(member);
                if (!entry.MatchesKind(kind))
                    continue;

                if (seen.Add(GetSymbolKey(member, entry)))
                    entries.Add(entry);
            }

            if (entries.Count > 0)
                return [.. entries];
        }

        return [];
    }

    INamedTypeSymbol[] ResolveTypes(string query, string? assemblyFilter)
    {
        var metadataNames = GetTypeMetadataNameCandidates(query);
        var typePathCandidates = GetNamespaceTypePathCandidates(query);
        if (metadataNames.Length is 0 && typePathCandidates.Length is 0)
            return [];

        var results = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            if (!assembly.MatchesFilter(assemblyFilter))
                continue;

            foreach (var metadataName in metadataNames)
            {
                var symbol = assembly.Symbol.GetTypeByMetadataName(metadataName);
                if (symbol is null && !metadataName.Contains('+'))
                    symbol = assembly.Symbol.ResolveForwardedType(metadataName);

                if (symbol is null)
                    continue;

                var key = assembly.IdentityKey + "\n" + SymbolText.GetDisplaySignature(symbol);
                if (seen.Add(key))
                    results.Add(symbol);
            }

            foreach (var symbol in ResolveTypesByNamespaceTraversal(assembly.Symbol, typePathCandidates))
            {
                var key = assembly.IdentityKey + "\n" + SymbolText.GetDisplaySignature(symbol);
                if (seen.Add(key))
                    results.Add(symbol);
            }
        }

        return [.. results];
    }

    static INamedTypeSymbol[] ResolveTypesByNamespaceTraversal(
        IAssemblySymbol assembly,
        (string Namespace, string TypePath)[] candidates)
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
        foreach (var @namespace in namespaces)
        {
            if (normalized.Length <= @namespace.Length || !normalized.StartsWith(@namespace + ".", Ordinal))
                continue;

            var typePath = normalized[(@namespace.Length + 1)..].Trim();
            if (typePath.Length is 0)
                continue;

            Add(@namespace, typePath);
        }

        return [.. results];

        void Add(string @namespace, string typePath)
        {
            var key = @namespace + "\n" + typePath;
            if (seen.Add(key))
                results.Add((@namespace, typePath));
        }
    }

    static void AddUngatedNamespaceTypePathCandidates(
        string normalized,
        List<(string Namespace, string TypePath)> results,
        HashSet<string> seen)
    {
        var separatorIndexes = GetTopLevelTypePathSeparatorIndexes(normalized);
        for (var i = separatorIndexes.Length - 1; i >= 0; i--)
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

        foreach (var @namespace in namespaces)
        {
            if (normalized.Length <= @namespace.Length || !normalized.StartsWith(@namespace + ".", Ordinal))
                continue;

            var typePath = normalized[(@namespace.Length + 1)..];
            var metadataTypePath = ToMetadataTypePath(typePath);
            if (metadataTypePath.Length is 0)
                continue;

            var metadataName = @namespace + "." + metadataTypePath;
            if (seen.Add(metadataName))
                results.Add(metadataName);
        }

        return [.. results];
    }

    static void AddUngatedTypeMetadataNameCandidates(string normalized, List<string> results, HashSet<string> seen)
    {
        if (normalized.Contains('(') || normalized.Contains(')'))
            return;

        var separatorIndexes = GetTopLevelTypePathSeparatorIndexes(normalized);
        if (separatorIndexes.Length is 0)
            return;

        for (var i = separatorIndexes.Length - 1; i >= 0; i--)
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
        for (var i = 0; i < value.Length; i++)
        {
            depth = value[i] switch
            {
                '<' => depth + 1,
                '>' when depth > 0 => depth - 1,
                _ => depth,
            };

            if (value[i] == '.' && depth is 0)
                indexes.Add(i);
        }

        return [.. indexes];
    }

    SymbolSearchEntry CreateEntry(ISymbol symbol)
        => SymbolSearchEntry.CreateMetadata(symbol, TryGetAssemblyPath(symbol));

    static IEnumerable<int> EnumerateMemberSplitIndexes(string query)
    {
        var end = query.IndexOf('(');
        if (end < 0)
            end = query.Length;

        var depth = 0;
        var indexes = new List<int>();
        for (var i = 0; i < end; i++)
        {
            depth = query[i] switch
            {
                '<' => depth + 1,
                '>' when depth > 0 => depth - 1,
                _ => depth,
            };

            if (query[i] == '.' && depth is 0)
                indexes.Add(i);
        }

        for (var i = indexes.Count - 1; i >= 0; i--)
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
            IMethodSymbol method => ParametersMatch(method.Parameters, query.Parameters),
            IPropertySymbol { IsIndexer: true } property => ParametersMatch(property.Parameters, query.Parameters),
            IPropertySymbol or IFieldSymbol or IEventSymbol => query.Parameters.Length is 0,
            _ => false,
        };
    }

    static bool ParametersMatch(ImmutableArray<IParameterSymbol> parameters, string[] queryParameters)
    {
        if (parameters.Length != queryParameters.Length)
            return false;

        for (var i = 0; i < parameters.Length; i++)
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
        for (var i = value.Length - 1; i >= 0; i--)
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
        for (var i = 0; i < segments.Length; i++)
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
        for (var i = 0; i < value.Length; i++)
        {
            depth = value[i] switch
            {
                '<' => depth + 1,
                '>' when depth > 0 => depth - 1,
                _ => depth,
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
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '<')
                return i;

        return -1;
    }

    static int FindMatchingGenericEnd(string value, int start)
    {
        var depth = 0;
        for (var i = start; i < value.Length; i++)
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
                '<' => depth + 1,
                '>' when depth > 0 => depth - 1,
                _ => depth,
            };

            if (character == ',' && depth is 0)
                count++;
        }

        return count;
    }

    static bool KindMatchesCategory(string kind, string category)
        => category switch
        {
            "type" => kind is "all" or "type" or "class" or "interface" or "struct" or "record" or "enum" or "delegate",
            "member" => kind is "all" or "member" or "method" or "property" or "indexer" or "field" or "event" or "operator" or "conversion",
            _ => false,
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

    sealed record ExternalAssemblyInfo(
        IAssemblySymbol Symbol,
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
        public MutableExternalAssemblyInfo(IAssemblySymbol symbol, string identityKey, string name, string? assemblyPath, string? display)
        {
            Symbol = symbol;
            IdentityKey = identityKey;
            Name = name;
            AssemblyPath = assemblyPath;
            Display = display;
        }

        public IAssemblySymbol Symbol { get; }

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
            => new(Symbol, IdentityKey, Name, AssemblyPath, Display);
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
                    : SplitParameters(parameters));
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
            for (var i = 0; i < value.Length; i++)
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

    internal sealed class Builder
    {
        readonly Dictionary<string, MutableExternalAssemblyInfo> assemblies = new(StringComparer.Ordinal);
        readonly HashSet<string> namespaces = new(StringComparer.Ordinal);

        internal void Add(Compilation compilation)
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

                assemblies.Add(identityKey, new(assembly, identityKey, assembly.Name, assemblyPath, reference.Display));
                CollectNamespaces(assembly.GlobalNamespace);
            }
        }

        internal ExternalMetadataIndex Build()
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

            return new(
                immutableAssemblies,
                namespaces
                    .AsValueEnumerable()
                    .OrderByDescending(static item => item.Length)
                    .ThenBy(static item => item, StringComparer.Ordinal)
                    .ToArray(),
                assemblyPathsByIdentity);
        }

        void CollectNamespaces(INamespaceSymbol symbol)
        {
            foreach (var namespaceMember in symbol.GetNamespaceMembers())
            {
                if (!namespaceMember.IsGlobalNamespace)
                    namespaces.Add(namespaceMember.ToDisplayString());

                CollectNamespaces(namespaceMember);
            }
        }
    }
}
