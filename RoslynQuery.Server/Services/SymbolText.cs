using Microsoft.CodeAnalysis;

namespace RoslynQuery;

static class SymbolText
{
    static readonly SymbolDisplayFormat typeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    static readonly SymbolDisplayFormat memberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeExtensionThis
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    static readonly SymbolDisplayFormat namespaceFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces
    );

    public static string GetDisplaySignature(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol namespaceSymbol => namespaceSymbol.ToDisplayString(namespaceFormat),
            INamedTypeSymbol namedType       => namedType.ToDisplayString(typeFormat),
            _                                => symbol.ToDisplayString(memberFormat),
        };

    public static string GetTypeDisplay(ITypeSymbol symbol)
        => symbol.ToDisplayString(typeFormat);

    public static string GetShortName(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } method => method.ContainingType.Name,
            _                                                                                           => symbol.Name,
        };

    public static string GetKind(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol                    => "namespace",
            INamedTypeSymbol { IsRecord: true } => "record",
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class     => "class",
                TypeKind.Interface => "interface",
                TypeKind.Struct    => "struct",
                TypeKind.Enum      => "enum",
                TypeKind.Delegate  => "delegate",
                _                  => "type",
            },
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Constructor         => "constructor",
                MethodKind.StaticConstructor   => "static_constructor",
                MethodKind.UserDefinedOperator => "operator",
                MethodKind.Conversion          => "conversion",
                MethodKind.Destructor          => "destructor",
                _                              => "method",
            },
            IPropertySymbol { IsIndexer: true } => "indexer",
            IPropertySymbol                     => "property",
            IFieldSymbol                        => "field",
            IEventSymbol                        => "event",
            _                                   => symbol.Kind.ToString().ToLowerInvariant(),
        };

    public static string? GetTypeKind(ISymbol symbol)
        => symbol is INamedTypeSymbol namedType ? GetKind(namedType) : null;

    public static string? GetContainingNamespace(ISymbol symbol)
        => symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString(namespaceFormat)
            : null;

    public static string? GetContainingType(ISymbol symbol)
        => symbol.ContainingType?.ToDisplayString(typeFormat);

    public static string? GetAccessibility(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol => null,
            _                => symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
        };

    public static bool IsStatic(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol => true,
            _                => symbol.IsStatic,
        };

    public static bool IsAbstract(ISymbol symbol)
        => symbol switch
        {
            INamedTypeSymbol namedType => namedType.IsAbstract,
            IMethodSymbol method       => method.IsAbstract,
            IPropertySymbol property   => property.IsAbstract,
            IEventSymbol @event        => @event.IsAbstract,
            _                          => false,
        };

    public static bool IsVirtual(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol method     => method.IsVirtual,
            IPropertySymbol property => property.IsVirtual,
            IEventSymbol @event      => @event.IsVirtual,
            _                        => false,
        };

    public static bool IsOverride(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol method     => method.IsOverride,
            IPropertySymbol property => property.IsOverride,
            IEventSymbol @event      => @event.IsOverride,
            _                        => false,
        };

    public static bool IsSealed(ISymbol symbol)
        => symbol switch
        {
            INamedTypeSymbol namedType => namedType.IsSealed,
            IMethodSymbol method       => method.IsSealed,
            _                          => false,
        };
}
