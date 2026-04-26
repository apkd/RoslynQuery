namespace RoslynQuery;

public readonly record struct WorkspaceStatusResponse()
{
    public bool IsBound { get; init; }
    public string? TargetPath { get; init; }
    public string? TargetKind { get; init; }
    public int ProjectCount { get; init; }
    public DateTimeOffset? LoadedAtUtc { get; init; }
    public double? LastLoadDurationMs { get; init; }
    public WorkspaceMessageDto[] Messages { get; init; } = [];
    public ProjectInfo[] Projects { get; init; } = [];
    public DiagnosticInfo[] Diagnostics { get; init; } = [];
}

public readonly record struct OpenWorkspaceResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string RequestedPath { get; init; } = string.Empty;
    public string? ResolvedPath { get; init; }
    public string? TargetKind { get; init; }
    public string[] Candidates { get; init; } = [];
    public WorkspaceStatusResponse Status { get; init; } = new();
}

public readonly record struct WorkspaceInitializationBenchmarkResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string RequestedPath { get; init; } = string.Empty;
    public string? ResolvedPath { get; init; }
    public string? TargetKind { get; init; }
    public int ProjectCount { get; init; }
    public double LoadDurationMs { get; init; }
    public double IndexWaitDurationMs { get; init; }
    public double TotalDurationMs { get; init; }
}

public readonly record struct DescribeSymbolResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Query { get; init; } = string.Empty;
    public string[] Candidates { get; init; } = [];
    public SymbolDetail? Symbol { get; init; }
    public ReferenceSummary Usages { get; init; } = new();
    public RelatedSymbolInfo[] Relations { get; init; } = [];
    public SymbolSummary[] Overloads { get; init; } = [];
    public TypeMemberSummary? TypeMembers { get; init; }
    public DiagnosticInfo[] Diagnostics { get; init; } = [];
}

public readonly record struct ListTypeMembersResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Query { get; init; } = string.Empty;
    public string[] Candidates { get; init; } = [];
    public SymbolSummary? Symbol { get; init; }
    public SymbolSummary[] Members { get; init; } = [];
}

public readonly record struct FindUsagesResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Query { get; init; } = string.Empty;
    public string[] Candidates { get; init; } = [];
    public SymbolSummary? Symbol { get; init; }
    public ReferenceInfo[] References { get; init; } = [];
}

public readonly record struct FindRelatedSymbolsResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Query { get; init; } = string.Empty;
    public string[] Candidates { get; init; } = [];
    public SymbolSummary? Symbol { get; init; }
    public RelatedSymbolInfo[] Relations { get; init; } = [];
}

public readonly record struct ViewIlResponse()
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Query { get; init; } = string.Empty;
    public string[] Candidates { get; init; } = [];
    public SymbolSummary? Symbol { get; init; }
    public string[] EmitDiagnostics { get; init; } = [];
    public IlMethodInfo[] Methods { get; init; } = [];
}

public readonly record struct WorkspaceMessageDto()
{
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public readonly record struct ProjectInfo()
{
    public string Name { get; init; } = string.Empty;
    public string? ProjectPath { get; init; }
    public string Language { get; init; } = string.Empty;
    public string? TargetFramework { get; init; }
    public int DocumentCount { get; init; }
    public string[] Documents { get; init; } = [];
}

public readonly record struct SymbolSummary()
{
    public string CanonicalSignature { get; init; } = string.Empty;
    public string DisplaySignature { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? TypeKind { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string? ProjectPath { get; init; }
    public string? AssemblyPath { get; init; }
    public string? ContainingNamespace { get; init; }
    public string? ContainingType { get; init; }
    public string? ReturnType { get; init; }
    public string? ValueType { get; init; }
    public SourceLocationInfo[] Locations { get; init; } = [];
}

public readonly record struct SymbolDetail()
{
    public string CanonicalSignature { get; init; } = string.Empty;
    public string DisplaySignature { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? TypeKind { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string? ProjectPath { get; init; }
    public string? AssemblyPath { get; init; }
    public string? ContainingNamespace { get; init; }
    public string? ContainingType { get; init; }
    public string? Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsSealed { get; init; }
    public string? ReturnType { get; init; }
    public string? ReturnRefKind { get; init; }
    public string? ReturnNullableAnnotation { get; init; }
    public string? ValueType { get; init; }
    public string? ValueRefKind { get; init; }
    public string? ValueNullableAnnotation { get; init; }
    public AttributeInfo[] Attributes { get; init; } = [];
    public AttributeInfo[] ReturnAttributes { get; init; } = [];
    public TypeParameterInfo[] TypeParameters { get; init; } = [];
    public ParameterInfo[] Parameters { get; init; } = [];
    public AccessorInfo[] Accessors { get; init; } = [];
    public string[] Characteristics { get; init; } = [];
    public string? ConstantValue { get; init; }
    public SourceLocationInfo[] Locations { get; init; } = [];
    public DocumentationInfo Documentation { get; init; } = new();
}

public readonly record struct ParameterInfo()
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? RefKind { get; init; }
    public string? NullableAnnotation { get; init; }
    public string? ScopedKind { get; init; }
    public bool IsOptional { get; init; }
    public bool IsParams { get; init; }
    public bool IsParamsArray { get; init; }
    public bool IsParamsCollection { get; init; }
    public bool IsThis { get; init; }
    public string? DefaultValue { get; init; }
    public AttributeInfo[] Attributes { get; init; } = [];
}

public readonly record struct TypeParameterInfo()
{
    public string Name { get; init; } = string.Empty;
    public string? Variance { get; init; }
    public string[] Constraints { get; init; } = [];
    public AttributeInfo[] Attributes { get; init; } = [];
}

public readonly record struct AccessorInfo()
{
    public string Kind { get; init; } = string.Empty;
    public string? Accessibility { get; init; }
    public string[] Characteristics { get; init; } = [];
    public AttributeInfo[] Attributes { get; init; } = [];
}

public readonly record struct AttributeInfo()
{
    public string Text { get; init; } = string.Empty;
}

public readonly record struct DocumentationInfo()
{
    public DocumentationSection[] Sections { get; init; } = [];
}

public readonly record struct DocumentationSection()
{
    public string Name { get; init; } = string.Empty;
    public string[] Attributes { get; init; } = [];
    public string Text { get; init; } = string.Empty;
}

public readonly record struct ReferenceSummary()
{
    public int Count { get; init; }
    public ProjectReferenceCount[] Projects { get; init; } = [];
    public ReferenceInfo[] Examples { get; init; } = [];
}

public readonly record struct ProjectReferenceCount()
{
    public string Project { get; init; } = string.Empty;
    public int Count { get; init; }
}

public readonly record struct TypeMemberSummary()
{
    public int TotalCount { get; init; }
    public int PublicCount { get; init; }
    public int ConstructorCount { get; init; }
    public int MethodCount { get; init; }
    public int PropertyCount { get; init; }
    public int FieldCount { get; init; }
    public int EventCount { get; init; }
    public int NestedTypeCount { get; init; }
    public int OtherCount { get; init; }
}

public readonly record struct SourceLocationInfo()
{
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public string? LineText { get; init; }
}

public readonly record struct ReferenceInfo()
{
    public string Project { get; init; } = string.Empty;
    public string? DocumentPath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public string? LineText { get; init; }
}

public readonly record struct RelatedSymbolInfo()
{
    public string Relation { get; init; } = string.Empty;
    public SymbolSummary Symbol { get; init; } = new();
}

public readonly record struct IlMethodInfo()
{
    public string DisplayName { get; init; } = string.Empty;
    public string MetadataName { get; init; } = string.Empty;
    public int CodeSize { get; init; }
    public int MaxStack { get; init; }
    public bool LocalVariablesInitialized { get; init; }
    public string Attributes { get; init; } = string.Empty;
    public string[] Locals { get; init; } = [];
    public string[] Instructions { get; init; } = [];
    public string[] ExceptionRegions { get; init; } = [];
}

public readonly record struct DiagnosticInfo()
{
    public string Project { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public SourceLocationInfo? Location { get; init; }
}
