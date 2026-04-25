using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynQuery;

static class SymbolFactory
{
    public static SymbolDetail ToDetail(ResolvedSymbol resolved, WorkspaceSymbolIndex index)
    {
        var entry = resolved.Entry;
        var symbol = resolved.Symbol;
        return new()
        {
            CanonicalSignature = index.GetCanonicalSignature(entry),
            DisplaySignature = entry.DisplaySignature,
            ShortName = entry.ShortName,
            Kind = entry.Kind,
            TypeKind = entry.TypeKind,
            Origin = entry.Origin,
            Project = index.GetOwnerName(entry),
            ProjectPath = index.GetProjectPath(entry),
            AssemblyPath = index.GetAssemblyPath(entry),
            ContainingNamespace = SymbolText.GetContainingNamespace(symbol),
            ContainingType = SymbolText.GetContainingType(symbol),
            Accessibility = SymbolText.GetAccessibility(symbol),
            IsStatic = SymbolText.IsStatic(symbol),
            IsAbstract = SymbolText.IsAbstract(symbol),
            IsVirtual = SymbolText.IsVirtual(symbol),
            IsOverride = SymbolText.IsOverride(symbol),
            IsSealed = SymbolText.IsSealed(symbol),
            ReturnType = symbol is IMethodSymbol returnTypeMethod ? SymbolText.GetTypeDisplay(returnTypeMethod.ReturnType) : null,
            ReturnRefKind = symbol is IMethodSymbol returnRefMethod ? FormatReturnRefKind(returnRefMethod.RefKind) : null,
            ReturnNullableAnnotation = symbol is IMethodSymbol returnNullableMethod
                ? FormatNullableAnnotation(returnNullableMethod.ReturnType, returnNullableMethod.ReturnNullableAnnotation)
                : null,
            ValueType = symbol switch
            {
                IPropertySymbol property => SymbolText.GetTypeDisplay(property.Type),
                IFieldSymbol field => SymbolText.GetTypeDisplay(field.Type),
                IEventSymbol @event => SymbolText.GetTypeDisplay(@event.Type),
                _ => null,
            },
            ValueRefKind = symbol switch
            {
                IPropertySymbol property => FormatReturnRefKind(property.RefKind),
                IFieldSymbol field => FormatReturnRefKind(field.RefKind),
                _ => null,
            },
            ValueNullableAnnotation = symbol switch
            {
                IPropertySymbol property => FormatNullableAnnotation(property.Type, property.Type.NullableAnnotation),
                IFieldSymbol field => FormatNullableAnnotation(field.Type, field.NullableAnnotation),
                IEventSymbol @event => FormatNullableAnnotation(@event.Type, @event.NullableAnnotation),
                _ => null,
            },
            Attributes = ToAttributes(symbol.GetAttributes()),
            ReturnAttributes = symbol is IMethodSymbol returnAttributeMethod ? ToAttributes(returnAttributeMethod.GetReturnTypeAttributes()) : [],
            TypeParameters = ToTypeParameters(symbol),
            Parameters = symbol switch
            {
                IMethodSymbol parameterMethod => parameterMethod.Parameters.AsValueEnumerable().Select(ToParameter).ToArray(),
                IPropertySymbol { IsIndexer: true } property => property.Parameters.AsValueEnumerable().Select(ToParameter).ToArray(),
                _ => [],
            },
            Accessors = ToAccessors(symbol),
            Characteristics = GetCharacteristics(symbol),
            ConstantValue = GetConstantValue(symbol),
            Locations = index.GetLocations(resolved),
            Documentation = GetDocumentation(symbol),
        };
    }

    public static SourceLocationInfo ToLocation(Location location, string? lineText)
    {
        var lineSpan = location.GetLineSpan();
        return new()
        {
            FilePath = lineSpan.Path,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            EndColumn = lineSpan.EndLinePosition.Character + 1,
            LineText = lineText,
        };
    }

    static ParameterInfo ToParameter(IParameterSymbol symbol)
        => new()
        {
            Name = symbol.Name,
            Type = SymbolText.GetTypeDisplay(symbol.Type),
            RefKind = FormatParameterRefKind(symbol.RefKind),
            NullableAnnotation = FormatNullableAnnotation(symbol.Type, symbol.NullableAnnotation),
            ScopedKind = FormatScopedKind(symbol.ScopedKind),
            IsOptional = symbol.IsOptional,
            IsParams = symbol.IsParams,
            IsParamsArray = symbol.IsParamsArray,
            IsParamsCollection = symbol.IsParamsCollection,
            IsThis = symbol.IsThis,
            DefaultValue = symbol.IsOptional ? (symbol.HasExplicitDefaultValue ? FormatConstant(symbol.ExplicitDefaultValue) : "default") : null,
            Attributes = ToAttributes(symbol.GetAttributes()),
        };

    static TypeParameterInfo[] ToTypeParameters(ISymbol symbol)
        => symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeParameters.AsValueEnumerable().Select(ToTypeParameter).ToArray(),
            IMethodSymbol genericMethod => genericMethod.TypeParameters.AsValueEnumerable().Select(ToTypeParameter).ToArray(),
            _ => [],
        };

    static TypeParameterInfo ToTypeParameter(ITypeParameterSymbol symbol)
        => new()
        {
            Name = symbol.Name,
            Variance = FormatVariance(symbol.Variance),
            Constraints = GetConstraints(symbol),
            Attributes = ToAttributes(symbol.GetAttributes()),
        };

    static string[] GetConstraints(ITypeParameterSymbol symbol)
    {
        var constraints = new List<string>();
        if (symbol.HasUnmanagedTypeConstraint)
            constraints.Add("unmanaged");
        else if (symbol.HasValueTypeConstraint)
            constraints.Add("struct");
        else if (symbol.HasReferenceTypeConstraint)
            constraints.Add(symbol.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");

        if (symbol.HasNotNullConstraint)
            constraints.Add("notnull");

        for (var i = 0; i < symbol.ConstraintTypes.Length; i++)
        {
            var constraintType = symbol.ConstraintTypes[i];
            var display = SymbolText.GetTypeDisplay(constraintType);
            if (i < symbol.ConstraintNullableAnnotations.Length
                && symbol.ConstraintNullableAnnotations[i] == NullableAnnotation.Annotated
                && !display.Contains('?'))
                display += "?";

            constraints.Add(display);
        }

        if (symbol.HasConstructorConstraint)
            constraints.Add("new()");

        if (symbol.AllowsRefLikeType)
            constraints.Add("allows ref struct");

        return constraints.ToArray();
    }

    static AccessorInfo[] ToAccessors(ISymbol symbol)
    {
        var accessors = new List<AccessorInfo>();
        switch (symbol)
        {
            case IPropertySymbol property:
                Add("get", property.GetMethod);
                Add(property.SetMethod is { IsInitOnly: true } ? "init" : "set", property.SetMethod);
                break;

            case IEventSymbol @event:
                Add("add", @event.AddMethod);
                Add("remove", @event.RemoveMethod);
                Add("raise", @event.RaiseMethod);
                break;
        }

        return accessors.ToArray();

        void Add(string kind, IMethodSymbol? accessor)
        {
            if (accessor is null)
                return;

            accessors.Add(new()
            {
                Kind = kind,
                Accessibility = SymbolText.GetAccessibility(accessor),
                Characteristics = GetAccessorCharacteristics(accessor),
                Attributes = ToAttributes(accessor.GetAttributes()),
            });
        }
    }

    static string[] GetAccessorCharacteristics(IMethodSymbol accessor)
    {
        var characteristics = new List<string>();
        if (accessor.IsExtern)
            characteristics.Add("extern");

        if (accessor.IsReadOnly)
            characteristics.Add("readonly");

        return characteristics.ToArray();
    }

    static string[] GetCharacteristics(ISymbol symbol)
    {
        var characteristics = new List<string>();
        if (symbol.IsExtern)
            characteristics.Add("extern");

        switch (symbol)
        {
            case INamedTypeSymbol namedType:
                if (namedType.IsFileLocal)
                    characteristics.Add("file-local");
                if (namedType.IsComImport)
                    characteristics.Add("com-import");
                if (namedType.IsSerializable)
                    characteristics.Add("serializable");
                if (namedType.IsExtension)
                    characteristics.Add("extension block");
                break;

            case IMethodSymbol method:
                if (method.IsAsync)
                    characteristics.Add("async");
                if (method.IsIterator)
                    characteristics.Add("iterator");
                if (method.IsExtensionMethod)
                    characteristics.Add("extension method");
                if (method.IsConditional)
                    characteristics.Add("conditional");
                if (method.IsVararg)
                    characteristics.Add("vararg");
                if (method.IsReadOnly)
                    characteristics.Add("readonly");
                if (method.IsCheckedBuiltin)
                    characteristics.Add("checked builtin");
                if (method.HidesBaseMethodsByName)
                    characteristics.Add("hides base methods by name");
                if (method.PartialImplementationPart is not null || method.IsPartialDefinition)
                    characteristics.Add("partial definition");
                if (method.PartialDefinitionPart is not null)
                    characteristics.Add("partial implementation");
                if (method.GetDllImportData() is { } dllImport)
                    characteristics.Add($"dllimport {dllImport.ModuleName}");

                AddExplicitImplementations(characteristics, method.ExplicitInterfaceImplementations);
                break;

            case IPropertySymbol property:
                if (property.IsReadOnly)
                    characteristics.Add("read-only");
                if (property.IsWriteOnly)
                    characteristics.Add("write-only");
                if (property.IsRequired)
                    characteristics.Add("required");
                if (property.IsWithEvents)
                    characteristics.Add("with-events");
                if (property.PartialImplementationPart is not null || property.IsPartialDefinition)
                    characteristics.Add("partial definition");
                if (property.PartialDefinitionPart is not null)
                    characteristics.Add("partial implementation");

                AddExplicitImplementations(characteristics, property.ExplicitInterfaceImplementations);
                break;

            case IFieldSymbol field:
                if (field.IsConst)
                    characteristics.Add("const");
                if (field.IsReadOnly)
                    characteristics.Add("readonly");
                if (field.IsVolatile)
                    characteristics.Add("volatile");
                if (field.IsRequired)
                    characteristics.Add("required");
                if (field.IsFixedSizeBuffer)
                    characteristics.Add($"fixed-size buffer ({field.FixedSize})");
                break;

            case IEventSymbol @event:
                if (@event.IsWindowsRuntimeEvent)
                    characteristics.Add("windows-runtime");
                if (@event.PartialImplementationPart is not null || @event.IsPartialDefinition)
                    characteristics.Add("partial definition");
                if (@event.PartialDefinitionPart is not null)
                    characteristics.Add("partial implementation");

                AddExplicitImplementations(characteristics, @event.ExplicitInterfaceImplementations);
                break;
        }

        return characteristics.ToArray();
    }

    static void AddExplicitImplementations<TSymbol>(List<string> characteristics, IEnumerable<TSymbol> symbols)
        where TSymbol : ISymbol
    {
        foreach (var symbol in symbols)
            characteristics.Add("implements " + SymbolText.GetDisplaySignature(symbol));
    }

    static string? GetConstantValue(ISymbol symbol)
        => symbol is IFieldSymbol { HasConstantValue: true } field
            ? FormatConstant(field.ConstantValue)
            : null;

    static AttributeInfo[] ToAttributes(IEnumerable<AttributeData> attributes)
        => attributes.Select(static attribute => new AttributeInfo { Text = FormatAttribute(attribute) }).ToArray();

    static string FormatAttribute(AttributeData attribute)
    {
        var builder = new StringBuilder();
        builder.Append(attribute.AttributeClass is null ? attribute.ToString() : SymbolText.GetTypeDisplay(attribute.AttributeClass));

        var hasConstructorArguments = !attribute.ConstructorArguments.IsDefaultOrEmpty;
        var hasNamedArguments = !attribute.NamedArguments.IsDefaultOrEmpty;
        if (!hasConstructorArguments && !hasNamedArguments)
            return builder.ToString();

        builder.Append('(');
        var appendedAny = false;

        foreach (var argument in attribute.ConstructorArguments)
        {
            if (appendedAny)
                builder.Append(", ");

            builder.Append(FormatTypedConstant(argument));
            appendedAny = true;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (appendedAny)
                builder.Append(", ");

            builder.Append(argument.Key);
            builder.Append(" = ");
            builder.Append(FormatTypedConstant(argument.Value));
            appendedAny = true;
        }

        builder.Append(')');
        return builder.ToString();
    }

    static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";

        if (constant.Kind == TypedConstantKind.Array)
            return "[" + string.Join(", ", constant.Values.AsValueEnumerable().Select(FormatTypedConstant).ToArray()) + "]";

        if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol type)
            return "typeof(" + SymbolText.GetTypeDisplay(type) + ")";

        return FormatConstant(constant.Value);
    }

    static string FormatConstant(object? value)
        => value switch
        {
            null => "null",
            string text => "\"" + EscapeString(text) + "\"",
            char character => "'" + EscapeChar(character) + "'",
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    static string EscapeString(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    static string EscapeChar(char value)
        => value switch
        {
            '\'' => "\\'",
            '\\' => "\\\\",
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            _ => value.ToString(),
        };

    static string? FormatParameterRefKind(RefKind refKind)
        => refKind switch
        {
            RefKind.None => null,
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            _ => refKind.ToString().ToLowerInvariant(),
        };

    static string? FormatReturnRefKind(RefKind refKind)
        => refKind switch
        {
            RefKind.None => null,
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "ref readonly",
            _ => refKind.ToString().ToLowerInvariant(),
        };

    static string? FormatScopedKind(ScopedKind scopedKind)
        => scopedKind switch
        {
            ScopedKind.None => null,
            ScopedKind.ScopedRef => "scoped ref",
            ScopedKind.ScopedValue => "scoped",
            _ => scopedKind.ToString().ToLowerInvariant(),
        };

    static string? FormatVariance(VarianceKind variance)
        => variance switch
        {
            VarianceKind.In => "in",
            VarianceKind.Out => "out",
            _ => null,
        };

    static string? FormatNullableAnnotation(ITypeSymbol type, NullableAnnotation annotation)
    {
        var isNullableRelevant = type.IsReferenceType || type.TypeKind == TypeKind.TypeParameter;
        if (!isNullableRelevant)
            return null;

        return annotation switch
        {
            NullableAnnotation.Annotated => "nullable",
            NullableAnnotation.NotAnnotated => "not-null",
            NullableAnnotation.None => "oblivious",
            _ => null,
        };
    }

    static DocumentationInfo GetDocumentation(ISymbol symbol)
    {
        var xml = string.Empty;
        try
        {
            xml = symbol.GetDocumentationCommentXml(expandIncludes: true) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(xml))
                return new();

            var root = XElement.Parse(xml);
            var sections = new List<DocumentationSection>();
            foreach (var node in root.Nodes())
            {
                switch (node)
                {
                    case XElement element:
                        sections.Add(new()
                        {
                            Name = element.Name.LocalName,
                            Attributes = element.Attributes().Select(FormatDocumentationAttribute).ToArray(),
                            Text = RenderDocumentationContent(element),
                        });
                        break;

                    case XCData data when !string.IsNullOrWhiteSpace(data.Value):
                        sections.Add(new()
                        {
                            Name = "cdata",
                            Text = NormalizeDocumentationText(data.Value),
                        });
                        break;

                    case XText text when !string.IsNullOrWhiteSpace(text.Value):
                        sections.Add(new()
                        {
                            Name = "text",
                            Text = NormalizeDocumentationText(text.Value),
                        });
                        break;
                }
            }

            return new() { Sections = sections.ToArray() };
        }
        catch
        {
            return string.IsNullOrWhiteSpace(xml)
                ? new()
                : new()
                {
                    Sections =
                    [
                        new()
                        {
                            Name = "raw",
                            Text = NormalizeDocumentationText(xml),
                        },
                    ],
                };
        }
    }

    static string RenderDocumentationContent(XElement element)
    {
        var builder = new StringBuilder();
        foreach (var node in element.Nodes())
            RenderDocumentationNode(builder, node);

        return NormalizeDocumentationText(builder.ToString());
    }

    static void RenderDocumentationNode(StringBuilder builder, XNode node)
    {
        switch (node)
        {
            case XCData data:
                builder.Append(data.Value);
                break;

            case XText text:
                builder.Append(text.Value);
                break;

            case XElement element:
                builder.Append('<');
                builder.Append(element.Name.LocalName);
                foreach (var attribute in element.Attributes())
                {
                    builder.Append(' ');
                    builder.Append(FormatDocumentationAttribute(attribute));
                }

                if (!element.Nodes().Any())
                {
                    builder.Append(" />");
                    return;
                }

                builder.Append('>');
                foreach (var child in element.Nodes())
                    RenderDocumentationNode(builder, child);

                builder.Append("</");
                builder.Append(element.Name.LocalName);
                builder.Append('>');
                break;
        }
    }

    static string FormatDocumentationAttribute(XAttribute attribute)
        => attribute.Name + "=\"" + EscapeDocumentationAttribute(attribute.Value) + "\"";

    static string EscapeDocumentationAttribute(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    static string NormalizeDocumentationText(string value)
    {
        var builder = new StringBuilder();
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(trimmed);
        }

        return builder.ToString();
    }
}
