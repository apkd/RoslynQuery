using System.Globalization;
using Cysharp.Text;

namespace RoslynQuery;

static class ToolTextFormatter
{
    public static string FormatStatus(in WorkspaceStatusResponse response)
    {
        if (!response.IsBound)
            return "No workspace open.";

        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Workspace: ");
            builder.Append(response.TargetKind ?? "unknown");
            builder.Append('\n');
            if (response.TargetPath is { Length: > 0 })
            {
                builder.Append("Path: ");
                builder.Append(response.TargetPath);
                builder.Append('\n');
            }

            AppendWorkspaceMessages(ref builder, response.Messages);
            AppendProjectCount(ref builder, response.ProjectCount, response.ExcludedProjectCount);
            AppendDiagnosticCounts(ref builder, response.ErrorCount, response.WarningCount, response.OtherDiagnosticCount);
            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatShowDiagnostics(in ShowDiagnosticsResponse response)
    {
        if (!response.Success)
            return FormatFailure(response.Error, []);

        var builder = ZString.CreateStringBuilder();
        try
        {
            var workspaceRoot = GetDirectoryPath(response.TargetPath);

            builder.Append("Diagnostics");
            if (!string.IsNullOrWhiteSpace(response.Verbosity))
            {
                builder.Append(" (");
                builder.Append(response.Verbosity);
                builder.Append(')');
            }

            builder.Append(':');
            builder.Append('\n');

            if (response.Diagnostics.Length is 0)
            {
                builder.Append("- none");
                builder.Append('\n');
                return Finish(ref builder);
            }

            AppendDiagnostics(ref builder, response.Diagnostics, workspaceRoot, includeHeader: false);
            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatLoadWorkspace(in OpenWorkspaceResponse response)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            var status = response.Status;
            if (!response.Success)
            {
                builder.Append("Error: ");
                builder.Append(response.Error ?? "The workspace operation failed.");
                builder.Append('\n');
                AppendCandidates(ref builder, response.Candidates);
                if (status.IsBound)
                {
                    builder.Append('\n');
                    builder.Append("Current Workspace:");
                    builder.Append('\n');
                    builder.Append(FormatStatus(in status));
                    builder.Append('\n');
                }

                return Finish(ref builder);
            }

            builder.Append("Loaded");
            builder.Append(' ');
            builder.Append(response.TargetKind ?? "workspace");
            builder.Append(".");
            builder.Append('\n');

            var path = response.ResolvedPath ?? response.Status.TargetPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                builder.Append("Path: ");
                builder.Append(path);
                builder.Append('\n');
            }

            AppendProjectCount(ref builder, status.ProjectCount, status.ExcludedProjectCount);

            if (status.LastLoadDurationMs is { } lastLoadDurationMs)
            {
                builder.Append("Load: ");
                builder.Append(lastLoadDurationMs.ToString("0.##", CultureInfo.InvariantCulture));
                builder.Append(" ms");
                builder.Append('\n');
            }

            AppendWorkspaceMessages(ref builder, status.Messages);
            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatDescribeSymbol(in DescribeSymbolResponse response)
    {
        if (!response.Success || response.Symbol is not { } symbol)
            return FormatFailure(response.Error, response.Candidates);

        var builder = ZString.CreateStringBuilder();
        try
        {
            var projectRoot = GetDirectoryPath(symbol.ProjectPath ?? symbol.AssemblyPath);

            builder.Append(symbol.CanonicalSignature);
            builder.Append('\n');
            AppendSymbolOwner(ref builder, in symbol);

            AppendContract(ref builder, in symbol);
            AppendAttributes(ref builder, "Attributes", symbol.Attributes);
            AppendAttributes(ref builder, "Return Attributes", symbol.ReturnAttributes);
            AppendTypeParameters(ref builder, symbol.TypeParameters);
            AppendParameterDetails(ref builder, symbol.Parameters);
            AppendAccessors(ref builder, symbol.Accessors);
            AppendTypeMemberSummary(ref builder, response.TypeMembers);
            AppendOverloads(ref builder, response.Overloads);
            AppendRelationSummary(ref builder, response.Relations);
            AppendUsageSummary(ref builder, response.Usages);
            AppendLocations(ref builder, symbol.Locations, projectRoot);
            AppendSymbolDiagnostics(ref builder, response.Diagnostics, projectRoot);
            AppendDocumentation(ref builder, symbol.Documentation);
            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static void AppendSymbolOwner(ref Utf16ValueStringBuilder builder, in SymbolDetail symbol)
    {
        if (string.Equals(symbol.Origin, "metadata", StringComparison.Ordinal))
        {
            builder.Append("Assembly: ");
            builder.Append(symbol.Project);
            builder.Append('\n');
            if (!string.IsNullOrWhiteSpace(symbol.AssemblyPath))
            {
                builder.Append("Assembly Path: ");
                builder.Append(symbol.AssemblyPath);
                builder.Append('\n');
            }

            return;
        }

        builder.Append("Project: ");
        builder.Append(symbol.Project);
        builder.Append('\n');
    }

    static void AppendContract(ref Utf16ValueStringBuilder builder, in SymbolDetail symbol)
    {
        if (!HasContract(in symbol))
            return;

        builder.Append('\n');
        builder.Append("Contract:");
        builder.Append('\n');

        AppendContractLine(ref builder, "Accessibility", symbol.Accessibility);
        AppendModifierContractLine(ref builder, in symbol);
        AppendContractLine(ref builder, "Characteristics", symbol.Characteristics);
        AppendTypeContractLine(ref builder, "Return", symbol.ReturnType, symbol.ReturnRefKind, symbol.ReturnNullableAnnotation, null);
        AppendTypeContractLine(ref builder, "Type", symbol.ValueType, symbol.ValueRefKind, symbol.ValueNullableAnnotation, symbol.ConstantValue);
    }

    static bool HasContract(in SymbolDetail symbol)
        => !string.IsNullOrWhiteSpace(symbol.Accessibility)
           || HasModifiers(in symbol)
           || symbol.Characteristics.Length > 0
           || !string.IsNullOrWhiteSpace(symbol.ReturnType)
           || !string.IsNullOrWhiteSpace(symbol.ValueType)
           || !string.IsNullOrWhiteSpace(symbol.ConstantValue);

    static void AppendContractLine(ref Utf16ValueStringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("- ");
        builder.Append(label);
        builder.Append(": ");
        builder.Append(value);
        builder.Append('\n');
    }

    static void AppendContractLine(ref Utf16ValueStringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count is 0)
            return;

        builder.Append("- ");
        builder.Append(label);
        builder.Append(": ");
        AppendDelimited(ref builder, values);
        builder.Append('\n');
    }

    static void AppendModifierContractLine(ref Utf16ValueStringBuilder builder, in SymbolDetail symbol)
    {
        if (!HasModifiers(in symbol))
            return;

        builder.Append("- Modifiers: ");
        var appendedAny = false;
        AppendModifierValue(ref builder, ref appendedAny, symbol.IsStatic, "static");
        AppendModifierValue(ref builder, ref appendedAny, symbol.IsAbstract, "abstract");
        AppendModifierValue(ref builder, ref appendedAny, symbol.IsVirtual, "virtual");
        AppendModifierValue(ref builder, ref appendedAny, symbol.IsOverride, "override");
        AppendModifierValue(ref builder, ref appendedAny, symbol.IsSealed, "sealed");
        builder.Append('\n');
    }

    static bool HasModifiers(in SymbolDetail symbol)
        => symbol.IsStatic || symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride || symbol.IsSealed;

    static void AppendModifierValue(ref Utf16ValueStringBuilder builder, ref bool appendedAny, bool include, string value)
    {
        if (!include)
            return;

        if (appendedAny)
            builder.Append(", ");

        builder.Append(value);
        appendedAny = true;
    }

    static void AppendTypeContractLine(
        ref Utf16ValueStringBuilder builder,
        string label,
        string? type,
        string? refKind,
        string? nullableAnnotation,
        string? constantValue
    )
    {
        if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(constantValue))
            return;

        builder.Append("- ");
        builder.Append(label);
        builder.Append(": ");
        if (!string.IsNullOrWhiteSpace(refKind))
        {
            builder.Append(refKind);
            builder.Append(' ');
        }

        builder.Append(type ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(nullableAnnotation))
        {
            builder.Append(" [");
            builder.Append(nullableAnnotation);
            builder.Append(']');
        }

        if (!string.IsNullOrWhiteSpace(constantValue))
        {
            builder.Append("; constant = ");
            builder.Append(constantValue);
        }

        builder.Append('\n');
    }

    static void AppendAttributes(ref Utf16ValueStringBuilder builder, string title, IReadOnlyList<AttributeInfo> attributes)
    {
        if (attributes.Count is 0)
            return;

        builder.Append('\n');
        builder.Append(title);
        builder.Append(':');
        builder.Append('\n');
        foreach (var attribute in attributes)
        {
            builder.Append("- ");
            builder.Append(attribute.Text);
            builder.Append('\n');
        }
    }

    static void AppendTypeParameters(ref Utf16ValueStringBuilder builder, IReadOnlyList<TypeParameterInfo> typeParameters)
    {
        if (!typeParameters.AsValueEnumerable().Any(static parameter => HasTypeParameterDetails(in parameter)))
            return;

        builder.Append('\n');
        builder.Append("Type Parameters:");
        builder.Append('\n');
        foreach (var typeParameter in typeParameters)
        {
            if (!HasTypeParameterDetails(in typeParameter))
                continue;

            builder.Append("- ");
            if (!string.IsNullOrWhiteSpace(typeParameter.Variance))
            {
                builder.Append(typeParameter.Variance);
                builder.Append(' ');
            }

            builder.Append(typeParameter.Name);
            if (typeParameter.Constraints.Length > 0)
            {
                builder.Append(": ");
                AppendDelimited(ref builder, typeParameter.Constraints);
            }

            AppendInlineAttributes(ref builder, typeParameter.Attributes);
            builder.Append('\n');
        }
    }

    static bool HasTypeParameterDetails(in TypeParameterInfo parameter)
        => !string.IsNullOrWhiteSpace(parameter.Variance)
           || parameter.Constraints.Length > 0
           || parameter.Attributes.Length > 0;

    static void AppendParameterDetails(ref Utf16ValueStringBuilder builder, IReadOnlyList<ParameterInfo> parameters)
    {
        if (!parameters.AsValueEnumerable().Any(static parameter => HasParameterDetails(in parameter)))
            return;

        builder.Append('\n');
        builder.Append("Parameter Details:");
        builder.Append('\n');
        foreach (var parameter in parameters)
        {
            if (!HasParameterDetails(in parameter))
                continue;

            builder.Append("- ");
            if (parameter.IsParams)
                builder.Append("params ");

            if (!string.IsNullOrWhiteSpace(parameter.RefKind))
            {
                builder.Append(parameter.RefKind);
                builder.Append(' ');
            }

            builder.Append(parameter.Type);
            builder.Append(' ');
            builder.Append(parameter.Name);

            var appendedDetail = false;
            AppendParameterFlag(ref builder, ref appendedDetail, parameter.IsThis, "extension this");
            AppendParameterFlag(ref builder, ref appendedDetail, parameter.IsParamsCollection, "params collection");
            AppendParameterValue(ref builder, ref appendedDetail, "scoped", parameter.ScopedKind);
            AppendParameterValue(ref builder, ref appendedDetail, "nullability", parameter.NullableAnnotation);

            if (parameter.IsOptional)
            {
                AppendParameterSeparator(ref builder, ref appendedDetail);
                builder.Append("optional");
                if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
                {
                    builder.Append(" = ");
                    builder.Append(parameter.DefaultValue);
                }
            }

            if (appendedDetail)
                builder.Append(')');

            AppendInlineAttributes(ref builder, parameter.Attributes);
            builder.Append('\n');
        }
    }

    static bool HasParameterDetails(in ParameterInfo parameter)
        => parameter.Attributes.Length > 0
           || parameter.IsOptional
           || parameter.IsParamsCollection
           || !string.IsNullOrWhiteSpace(parameter.ScopedKind)
           || string.Equals(parameter.NullableAnnotation, "oblivious", StringComparison.Ordinal);

    static void AppendParameterFlag(ref Utf16ValueStringBuilder builder, ref bool appendedDetail, bool include, string value)
    {
        if (!include)
            return;

        AppendParameterSeparator(ref builder, ref appendedDetail);
        builder.Append(value);
    }

    static void AppendParameterValue(ref Utf16ValueStringBuilder builder, ref bool appendedDetail, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        AppendParameterSeparator(ref builder, ref appendedDetail);
        builder.Append(label);
        builder.Append(": ");
        builder.Append(value);
    }

    static void AppendParameterSeparator(ref Utf16ValueStringBuilder builder, ref bool appendedDetail)
    {
        builder.Append(appendedDetail ? "; " : " (");
        appendedDetail = true;
    }

    static void AppendInlineAttributes(ref Utf16ValueStringBuilder builder, IReadOnlyList<AttributeInfo> attributes)
    {
        if (attributes.Count is 0)
            return;

        builder.Append("; attributes: ");
        for (int i = 0; i < attributes.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append(attributes[i].Text);
        }
    }

    static void AppendAccessors(ref Utf16ValueStringBuilder builder, IReadOnlyList<AccessorInfo> accessors)
    {
        if (accessors.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Accessors:");
        builder.Append('\n');
        foreach (var accessor in accessors)
        {
            builder.Append("- ");
            builder.Append(accessor.Kind);
            if (!string.IsNullOrWhiteSpace(accessor.Accessibility))
            {
                builder.Append(' ');
                builder.Append(accessor.Accessibility);
            }

            if (accessor.Characteristics.Length > 0)
            {
                builder.Append("; ");
                AppendDelimited(ref builder, accessor.Characteristics);
            }

            AppendInlineAttributes(ref builder, accessor.Attributes);
            builder.Append('\n');
        }
    }

    static void AppendTypeMemberSummary(ref Utf16ValueStringBuilder builder, TypeMemberSummary? summary)
    {
        if (summary is not { } value)
            return;

        builder.Append('\n');
        builder.Append("Type Members: ");
        if (value.TotalCount is 0)
        {
            builder.Append("none");
            builder.Append('\n');
            return;
        }

        builder.Append(value.TotalCount);
        builder.Append(" declared, ");
        builder.Append(value.PublicCount);
        builder.Append(" public");

        var appendedAny = false;
        AppendMemberCount(ref builder, ref appendedAny, value.ConstructorCount, "constructors");
        AppendMemberCount(ref builder, ref appendedAny, value.MethodCount, "methods");
        AppendMemberCount(ref builder, ref appendedAny, value.PropertyCount, "properties");
        AppendMemberCount(ref builder, ref appendedAny, value.FieldCount, "fields");
        AppendMemberCount(ref builder, ref appendedAny, value.EventCount, "events");
        AppendMemberCount(ref builder, ref appendedAny, value.NestedTypeCount, "nested types");
        AppendMemberCount(ref builder, ref appendedAny, value.OtherCount, "other");
        if (appendedAny)
            builder.Append(')');

        builder.Append('\n');
    }

    static void AppendMemberCount(ref Utf16ValueStringBuilder builder, ref bool appendedAny, int count, string label)
    {
        if (count is 0)
            return;

        builder.Append(appendedAny ? ", " : " (");
        builder.Append(count);
        builder.Append(' ');
        builder.Append(label);
        appendedAny = true;
    }

    static void AppendOverloads(ref Utf16ValueStringBuilder builder, IReadOnlyList<SymbolSummary> overloads)
    {
        if (overloads.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Overloads (");
        builder.Append(overloads.Count);
        builder.Append("):");
        builder.Append('\n');
        foreach (var overload in overloads)
        {
            builder.Append("- ");
            builder.Append(GetSimpleMemberSignature(in overload));
            builder.Append('\n');
        }
    }

    static void AppendRelationSummary(ref Utf16ValueStringBuilder builder, IReadOnlyList<RelatedSymbolInfo> relations)
    {
        if (relations.Count is 0)
            return;

        var relationsByKind = new Dictionary<string, List<RelatedSymbolInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in relations)
        {
            if (!relationsByKind.TryGetValue(relation.Relation, out var bucket))
            {
                bucket = [];
                relationsByKind.Add(relation.Relation, bucket);
            }

            bucket.Add(relation);
        }

        var relationKeys = new string[relationsByKind.Count];
        relationsByKind.Keys.CopyTo(relationKeys, 0);
        Array.Sort(relationKeys, StringComparer.OrdinalIgnoreCase);

        builder.Append('\n');
        builder.Append("Relationships:");
        builder.Append('\n');
        foreach (var relationKey in relationKeys)
        {
            var bucket = relationsByKind[relationKey]
                .AsValueEnumerable()
                .OrderBy(static relation => relation.Symbol.DisplaySignature, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            builder.Append("- ");
            AppendHumanized(ref builder, relationKey);
            if (bucket.Length > 1)
            {
                builder.Append(" (");
                builder.Append(bucket.Length);
                builder.Append(')');
            }

            builder.Append(": ");
            var sampleCount = Math.Min(bucket.Length, 3);
            for (int i = 0; i < sampleCount; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(bucket[i].Symbol.DisplaySignature);
            }

            if (bucket.Length > sampleCount)
            {
                builder.Append(", +");
                builder.Append(bucket.Length - sampleCount);
                builder.Append(" more");
            }

            builder.Append('\n');
        }
    }

    static void AppendUsageSummary(ref Utf16ValueStringBuilder builder, ReferenceSummary usages)
    {
        builder.Append('\n');
        builder.Append("Usages: ");
        if (usages.Count is 0)
        {
            builder.Append("none");
            builder.Append('\n');
            return;
        }

        builder.Append(usages.Count);
        builder.Append(usages.Count is 1 ? " reference" : " references");
        builder.Append(" across ");
        builder.Append(usages.Projects.Length);
        builder.Append(usages.Projects.Length is 1 ? " project" : " projects");

        if (usages.Projects.Length > 0)
        {
            builder.Append(" (");
            for (int i = 0; i < usages.Projects.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(usages.Projects[i].Project);
                builder.Append(' ');
                builder.Append(usages.Projects[i].Count);
            }

            builder.Append(')');
        }

        builder.Append('\n');

        if (usages.Examples.Length is 0)
            return;

        builder.Append("Usage Examples:");
        builder.Append('\n');
        foreach (var reference in usages.Examples)
        {
            builder.Append("- ");
            builder.Append(reference.Project);
            builder.Append(' ');
            AppendReferenceLocation(ref builder, in reference);
            if (!string.IsNullOrWhiteSpace(reference.LineText))
            {
                builder.Append("  ");
                builder.Append(reference.LineText);
            }

            builder.Append('\n');
        }
    }

    static void AppendSymbolDiagnostics(ref Utf16ValueStringBuilder builder, IReadOnlyList<DiagnosticInfo> diagnostics, string? projectRoot)
    {
        if (diagnostics.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Diagnostics:");
        builder.Append('\n');

        foreach (var diagnostic in diagnostics)
        {
            builder.Append("- ");
            builder.Append(diagnostic.Severity);
            builder.Append(' ');
            builder.Append(diagnostic.Id);
            if (diagnostic.Location is { } location)
            {
                builder.Append(' ');
                AppendLocation(ref builder, in location, projectRoot);
            }

            builder.Append("  ");
            builder.Append(diagnostic.Message);
            builder.Append('\n');
        }
    }

    static void AppendDocumentation(ref Utf16ValueStringBuilder builder, DocumentationInfo documentation)
    {
        if (documentation.Sections.Length is 0)
            return;

        builder.Append('\n');
        builder.Append("Documentation:");
        builder.Append('\n');
        foreach (var section in documentation.Sections)
        {
            builder.Append("- ");
            builder.Append(section.Name);
            if (section.Attributes.Length > 0)
            {
                builder.Append(" (");
                AppendDelimited(ref builder, section.Attributes);
                builder.Append(')');
            }

            if (!string.IsNullOrWhiteSpace(section.Text))
            {
                builder.Append(": ");
                builder.Append(section.Text);
            }

            builder.Append('\n');
        }
    }

    public static string FormatListTypeMembers(in ListTypeMembersResponse response)
    {
        if (!response.Success)
            return FormatFailure(response.Error, response.Candidates);

        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Members of ");
            builder.Append(response.Symbol?.CanonicalSignature ?? response.Query);
            builder.Append(" (");
            builder.Append(response.Members.Length);
            builder.Append("):");
            builder.Append('\n');

            if (response.Members.Length is 0)
            {
                builder.Append("- none");
                builder.Append('\n');
            }
            else
            {
                var previousCategory = string.Empty;
                foreach (var member in response.Members
                             .AsValueEnumerable()
                             .OrderBy(static member => GetMemberCategoryOrder(member.Kind))
                             .ThenBy(static member => GetMemberCategory(member.Kind), StringComparer.OrdinalIgnoreCase)
                             .ThenBy(static member => GetTypeMemberSignature(in member), StringComparer.OrdinalIgnoreCase))
                {
                    var category = GetMemberCategory(member.Kind);
                    if (!string.Equals(category, previousCategory, StringComparison.Ordinal))
                    {
                        builder.Append('\n');
                        builder.Append(category);
                        builder.Append(':');
                        builder.Append('\n');
                        previousCategory = category;
                    }

                    builder.Append("- ");
                    builder.Append(GetTypeMemberSignature(in member));

                    if (member.Locations.Length > 0)
                    {
                        builder.Append(" @ ");
                        var location = member.Locations[0];
                        AppendFileLocation(ref builder, in location);
                    }

                    builder.Append('\n');
                }
            }

            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string GetMemberCategory(string kind)
        => kind switch
        {
            "constructor"                                                                    => "Constructors",
            "static_constructor"                                                             => "Static constructors",
            "method"                                                                         => "Methods",
            "operator"                                                                       => "Operators",
            "conversion"                                                                     => "Conversions",
            "destructor"                                                                     => "Destructors",
            "property"                                                                       => "Properties",
            "indexer"                                                                        => "Indexers",
            "field"                                                                          => "Fields",
            "event"                                                                          => "Events",
            "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "type" => "Types",
            _                                                                                => "Other members",
        };

    static int GetMemberCategoryOrder(string kind)
        => kind switch
        {
            "constructor" or "static_constructor"                                            => 0,
            "method"                                                                         => 1,
            "operator" or "conversion"                                                       => 2,
            "destructor"                                                                     => 3,
            "property" or "indexer"                                                          => 4,
            "field"                                                                          => 5,
            "event"                                                                          => 6,
            "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "type" => 7,
            _                                                                                => 8,
        };

    static string GetSimpleMemberSignature(in SymbolSummary member)
    {
        var signature = member.DisplaySignature;
        if (!string.IsNullOrWhiteSpace(member.ContainingType))
        {
            var containingType = member.ContainingType.AsSpan();
            var signatureSpan = signature.AsSpan();
            if (signatureSpan.StartsWith(containingType, StringComparison.Ordinal)
                && signatureSpan.Length > containingType.Length
                && signatureSpan[containingType.Length] == '.')
                signature = signature[(containingType.Length + 1)..];
        }

        var builder = ZString.CreateStringBuilder();
        try
        {
            AppendWithoutNamespaceQualifiers(ref builder, signature);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string GetTypeMemberSignature(in SymbolSummary member)
    {
        var signature = GetSimpleMemberSignature(in member);
        var type = member.Kind switch
        {
            "method" or "operator" or "conversion" => member.ReturnType,
            "property" or "indexer"                => member.ValueType,
            _                                      => null,
        };

        if (string.IsNullOrWhiteSpace(type))
            return signature;

        var builder = ZString.CreateStringBuilder();
        try
        {
            AppendWithoutNamespaceQualifiers(ref builder, type);
            builder.Append(' ');
            builder.Append(signature);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatFindUsages(in FindUsagesResponse response)
    {
        if (!response.Success)
            return FormatFailure(response.Error, response.Candidates);

        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Usages of ");
            builder.Append(response.Symbol?.DisplaySignature ?? response.Query);
            builder.Append(" (");
            builder.Append(response.References.Length);
            builder.Append("):");
            builder.Append('\n');

            if (response.References.Length is 0)
            {
                builder.Append("- none");
                builder.Append('\n');
            }
            else
            {
                foreach (var reference in response.References)
                {
                    builder.Append("- ");
                    builder.Append(reference.Project);
                    builder.Append(' ');
                    AppendReferenceLocation(ref builder, in reference);
                    if (!string.IsNullOrWhiteSpace(reference.LineText))
                    {
                        builder.Append("  ");
                        builder.Append(reference.LineText);
                    }

                    builder.Append('\n');
                }
            }

            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatFindRelatedSymbols(in FindRelatedSymbolsResponse response)
    {
        if (!response.Success)
            return FormatFailure(response.Error, response.Candidates);

        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Related symbols for ");
            builder.Append(response.Symbol?.DisplaySignature ?? response.Query);
            builder.Append('\n');

            if (response.Relations.Length is 0)
            {
                builder.Append('\n');
                builder.Append("- none");
                builder.Append('\n');
                return Finish(ref builder);
            }

            var relationsByKind = new Dictionary<string, List<RelatedSymbolInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var relation in response.Relations)
            {
                if (!relationsByKind.TryGetValue(relation.Relation, out var bucket))
                {
                    bucket = [];
                    relationsByKind.Add(relation.Relation, bucket);
                }

                bucket.Add(relation);
            }

            var relationKeys = new string[relationsByKind.Count];
            relationsByKind.Keys.CopyTo(relationKeys, 0);
            Array.Sort(relationKeys, StringComparer.OrdinalIgnoreCase);

            foreach (var relationKey in relationKeys)
            {
                builder.Append('\n');
                AppendHumanized(ref builder, relationKey);
                builder.Append(":");
                builder.Append('\n');
                foreach (var item in relationsByKind[relationKey])
                {
                    builder.Append("- ");
                    builder.Append(item.Symbol.DisplaySignature);
                    if (!string.IsNullOrWhiteSpace(item.Symbol.Project))
                    {
                        builder.Append(" [");
                        builder.Append(item.Symbol.Project);
                        builder.Append(']');
                    }

                    if (item.Symbol.Locations.Length > 0)
                    {
                        builder.Append(" @ ");
                        var location = item.Symbol.Locations[0];
                        AppendLocation(ref builder, in location, GetDirectoryPath(item.Symbol.ProjectPath));
                    }

                    builder.Append('\n');
                }
            }

            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatViewIl(in ViewIlResponse response)
    {
        if (!response.Success)
        {
            var failure = FormatFailure(response.Error, response.Candidates);
            if (response.EmitDiagnostics.Length is 0)
                return failure;

            var builder = ZString.CreateStringBuilder();
            try
            {
                builder.Append(failure);
                builder.Append('\n');
                builder.Append('\n');
                builder.Append("Emit Diagnostics:");
                builder.Append('\n');
                foreach (var diagnostic in response.EmitDiagnostics)
                {
                    builder.Append("- ");
                    builder.Append(diagnostic);
                    builder.Append('\n');
                }

                return Finish(ref builder);
            }
            finally
            {
                builder.Dispose();
            }
        }

        var output = ZString.CreateStringBuilder();
        try
        {
            for (var methodIndex = 0; methodIndex < response.Methods.Length; methodIndex++)
            {
                var method = response.Methods[methodIndex];
                if (methodIndex > 0)
                    output.Append('\n');

                output.Append(method.DisplayName);
                output.Append('\n');
                AppendIlMethodMetadata(ref output, in method);
                output.Append("IL:");
                output.Append('\n');
                foreach (var instruction in method.Instructions)
                {
                    output.Append(instruction);
                    output.Append('\n');
                }

                if (method.ExceptionRegions.Length is 0)
                    continue;

                output.Append('\n');
                output.Append("Exception Regions:");
                output.Append('\n');
                foreach (var region in method.ExceptionRegions)
                {
                    output.Append("- ");
                    output.Append(region);
                    output.Append('\n');
                }
            }

            return Finish(ref output);
        }
        finally
        {
            output.Dispose();
        }

        static void AppendIlMethodMetadata(ref Utf16ValueStringBuilder builder, in IlMethodInfo method)
        {
            builder.Append("Attributes: ");
            builder.Append(method.Attributes);
            builder.Append('\n');
            builder.Append("Code Size: ");
            builder.Append(method.CodeSize);
            builder.Append(" bytes");
            builder.Append('\n');
            builder.Append("Max Stack: ");
            builder.Append(method.MaxStack);
            builder.Append('\n');
            if (method.Locals.Length is 0)
                return;

            builder.Append("Locals");
            if (method.LocalVariablesInitialized)
                builder.Append(" init");

            builder.Append(":");
            builder.Append('\n');
            foreach (var local in method.Locals)
            {
                builder.Append("- ");
                builder.Append(local);
                builder.Append('\n');
            }
        }
    }

    static string FormatFailure(string? error, IReadOnlyList<string> candidates)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Error: ");
            builder.Append(string.IsNullOrWhiteSpace(error) ? "The request failed." : error);
            builder.Append('\n');
            AppendCandidates(ref builder, candidates);
            return Finish(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static void AppendWorkspaceMessages(ref Utf16ValueStringBuilder builder, IReadOnlyList<WorkspaceMessageDto> messages)
    {
        if (messages.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Messages:");
        builder.Append('\n');
        foreach (var message in messages)
        {
            builder.Append("- ");
            builder.Append(message.Severity);
            builder.Append(": ");
            builder.Append(message.Message);
            builder.Append('\n');
        }
    }

    static void AppendProjectCount(ref Utf16ValueStringBuilder builder, int projectCount, int excludedProjectCount)
    {
        builder.Append("Projects: ");
        builder.Append(projectCount);
        if (excludedProjectCount > 0)
        {
            builder.Append(" (");
            builder.Append(excludedProjectCount);
            builder.Append(" excluded via .roslynqueryignore)");
        }

        builder.Append('\n');
    }

    static void AppendDiagnosticCounts(ref Utf16ValueStringBuilder builder, int errorCount, int warningCount, int otherCount)
    {
        builder.Append("Diagnostics: ");
        builder.Append(errorCount);
        builder.Append(" errors, ");
        builder.Append(warningCount);
        builder.Append(" warnings, ");
        builder.Append(otherCount);
        builder.Append(" other");
        builder.Append('\n');
    }

    static void AppendProjects(
        ref Utf16ValueStringBuilder builder,
        IReadOnlyList<ProjectInfo> projects,
        int excludedProjectCount,
        string? workspaceRoot
    )
    {
        builder.Append('\n');
        AppendProjectCount(ref builder, projects.Count, excludedProjectCount);
        if (projects.Count is 0)
        {
            builder.Append("- none");
            builder.Append('\n');
            return;
        }

        foreach (var project in projects)
        {
            builder.Append("- ");
            builder.Append(project.Name);
            if (!string.IsNullOrWhiteSpace(project.TargetFramework))
            {
                builder.Append(" [");
                builder.Append(project.TargetFramework);
                builder.Append(']');
            }

            builder.Append(' ');
            builder.Append(CompactPath(project.ProjectPath, workspaceRoot));
            builder.Append('\n');
        }
    }

    static void AppendDiagnostics(
        ref Utf16ValueStringBuilder builder,
        IReadOnlyList<DiagnosticInfo> diagnostics,
        string? workspaceRoot,
        bool includeHeader = true
    )
    {
        if (diagnostics.Count is 0)
            return;

        if (includeHeader)
        {
            builder.Append('\n');
            builder.Append("Diagnostics:");
            builder.Append('\n');
        }

        foreach (var diagnostic in diagnostics)
        {
            builder.Append("- ");
            builder.Append(diagnostic.Severity);
            builder.Append(' ');
            builder.Append(diagnostic.Id);
            if (diagnostic.Location is { } location)
            {
                builder.Append(' ');
                AppendLocation(ref builder, in location, workspaceRoot);
            }

            builder.Append("  ");
            builder.Append(diagnostic.Message);
            builder.Append('\n');
        }
    }

    static void AppendLocations(ref Utf16ValueStringBuilder builder, IReadOnlyList<SourceLocationInfo> locations, string? basePath)
    {
        if (locations.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Locations:");
        builder.Append('\n');
        foreach (var location in locations)
        {
            builder.Append("- ");
            AppendLocation(ref builder, in location, basePath);
            builder.Append('\n');
        }
    }

    static void AppendCandidates(ref Utf16ValueStringBuilder builder, IReadOnlyList<string> candidates)
    {
        if (candidates.Count is 0)
            return;

        builder.Append('\n');
        builder.Append("Candidates:");
        builder.Append('\n');
        foreach (var candidate in candidates)
        {
            builder.Append("- ");
            builder.Append(candidate);
            builder.Append('\n');
        }
    }

    static void AppendDelimited(ref Utf16ValueStringBuilder builder, IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append(values[i]);
        }
    }

    static void AppendReferenceLocation(ref Utf16ValueStringBuilder builder, in ReferenceInfo reference)
    {
        if (reference.DocumentPath is { Length: > 0 } documentPath)
        {
            builder.Append(GetFileName(documentPath));
            builder.Append(':');
        }

        builder.Append(reference.Line);
    }

    static void AppendLocation(ref Utf16ValueStringBuilder builder, in SourceLocationInfo location, string? basePath)
    {
        builder.Append(CompactPath(location.FilePath, basePath));
        builder.Append(':');
        builder.Append(location.Line);
    }

    static void AppendFileLocation(ref Utf16ValueStringBuilder builder, in SourceLocationInfo location)
    {
        builder.Append(GetFileName(location.FilePath));
        builder.Append(':');
        builder.Append(location.Line);
    }

    static void AppendWithoutNamespaceQualifiers(ref Utf16ValueStringBuilder builder, ReadOnlySpan<char> value)
    {
        var tokenStart = -1;
        var lastSegmentStart = -1;

        for (int i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (IsIdentifierPart(character) || character is '.')
            {
                if (tokenStart < 0)
                    tokenStart = i;

                if (character is '.')
                    lastSegmentStart = i + 1;

                continue;
            }

            if (tokenStart >= 0)
            {
                var start = lastSegmentStart > tokenStart ? lastSegmentStart : tokenStart;
                builder.Append(value[start..i]);
                tokenStart = -1;
                lastSegmentStart = -1;
            }

            builder.Append(character);
        }

        if (tokenStart >= 0)
        {
            var start = lastSegmentStart > tokenStart ? lastSegmentStart : tokenStart;
            builder.Append(value[start..]);
        }
    }

    static void AppendHumanized(ref Utf16ValueStringBuilder builder, string value)
    {
        var capitalizeNext = true;
        foreach (var character in value)
        {
            if (character is '_')
            {
                builder.Append(' ');
                capitalizeNext = true;
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
            capitalizeNext = false;
        }
    }

    static string Finish(ref Utf16ValueStringBuilder builder)
    {
        var text = builder.ToString().AsSpan();
        var trimmed = text.TrimEnd();
        return trimmed.Length == text.Length ? text.ToString() : trimmed.ToString();
    }

    static string CompactPath(string? path, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(basePath))
            return path;

        var baseSpan = basePath.AsSpan();
        int trimmedBaseLength = baseSpan.Length;
        while (trimmedBaseLength > 0 && IsDirectorySeparator(baseSpan[trimmedBaseLength - 1]))
            trimmedBaseLength--;

        var trimmedBase = baseSpan[..trimmedBaseLength];
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (trimmedBase.Length > 0
            && path.Length > trimmedBase.Length + 1
            && path.AsSpan().StartsWith(trimmedBase, comparison)
            && IsDirectorySeparator(path[trimmedBase.Length]))
            return path[(trimmedBase.Length + 1)..];

        return path;
    }

    static string? GetDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        int separatorIndex = path.LastIndexOfAny(['/', '\\']);
        return separatorIndex <= 0 ? null : path[..separatorIndex];
    }

    static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
    {
        int separatorIndex = path.LastIndexOfAny(['/', '\\']);
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    static bool IsIdentifierPart(char value)
        => char.IsLetterOrDigit(value) || value is '_';

    static bool IsDirectorySeparator(char value)
        => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
}
