using System.ComponentModel;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

namespace RoslynQuery;

[McpServerToolType]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class RoslynTools
{
    delegate string Formatter<T>(in T response);

    [McpServerTool(Name = "status")]
    [Description("Returns the current workspace/session status.")]
    public static Task<string> Status(
        WorkspaceSessionManager manager,
        CancellationToken ct
    ) => FormatAsync(manager.StatusAsync(ct), ToolTextFormatter.FormatStatus);

    [McpServerTool(Name = "show_diagnostics")]
    [Description("Shows workspace compilation diagnostics.")]
    public static Task<string> ShowDiagnostics(
        WorkspaceSessionManager manager,
        CancellationToken ct,
        [Description("Optional verbosity level filter. Allowed values: all, warnings, errors.")]
        string verbosity = "all"
    ) => FormatAsync(manager.ShowDiagnosticsAsync(verbosity, ct), ToolTextFormatter.FormatShowDiagnostics);

    [McpServerTool(Name = "load_workspace")]
    [Description("Loads (or reloads) a solution or project.")]
    public static Task<string> LoadWorkspace(
        [Description("Project directory, .sln, .slnx, or .csproj path.")]
        string path,
        WorkspaceSessionManager manager,
        CancellationToken ct
    ) => FormatAsync(manager.LoadAsync(path, ct), ToolTextFormatter.FormatLoadWorkspace);

    [McpServerTool(Name = "describe_symbol")]
    [Description("Describes a symbol.")]
    public static Task<string> DescribeSymbol(
        [Description("Canonical signature, fully-qualified name, or short query.")]
        string symbol,
        WorkspaceSessionManager manager,
        CancellationToken ct
    ) => FormatAsync(manager.DescribeSymbolAsync(symbol, ct), ToolTextFormatter.FormatDescribeSymbol);

    [McpServerTool(Name = "list_type_members")]
    [Description("Lists members declared on a type symbol, optionally including inherited members.")]
    public static Task<string> ListTypeMembers(
        [Description("Canonical type signature, fully-qualified name, or short/partial name.")]
        string symbol,
        WorkspaceSessionManager manager,
        CancellationToken ct,
        [Description("Include inherited members from base types and interfaces.")]
        bool includeInherited = false
    ) => FormatAsync(manager.ListTypeMembersAsync(symbol, includeInherited, ct), ToolTextFormatter.FormatListTypeMembers);

    [McpServerTool(Name = "find_usages")]
    [Description("Finds source references to a symbol.")]
    public static Task<string> FindUsages(
        [Description("Canonical signature, fully-qualified name, or short/partial name.")]
        string symbol,
        WorkspaceSessionManager manager,
        CancellationToken ct
    ) => FormatAsync(manager.FindUsagesAsync(symbol, ct), ToolTextFormatter.FormatFindUsages);

    [McpServerTool(Name = "find_related_symbols")]
    [Description("Finds related symbols such as base types, implementations, derived types, overrides, and containing types.")]
    public static Task<string> FindRelatedSymbols(
        [Description("Canonical signature, fully-qualified name, or short/partial name.")]
        string symbol,
        WorkspaceSessionManager manager,
        CancellationToken ct,
        [Description("Optional relation filter list. Allowed values: base_types, implemented_interfaces, derived_types, implementations, overrides, overridden_members, containing_symbol.")]
        string[]? relations = null
    ) => FormatAsync(manager.FindRelatedSymbolsAsync(symbol, relations, ct), ToolTextFormatter.FormatFindRelatedSymbols);

    [McpServerTool(Name = "view_il")]
    [Description("Shows the compiled IL for a method or property. Useful for debugging, optimization and low-level analysis.")]
    public static Task<string> ViewIl(
        [Description("Method or property canonical signature, fully-qualified name, or short/partial name.")]
        string symbol,
        WorkspaceSessionManager manager,
        CancellationToken ct,
        [Description("Simplify displayed type names in IL operands and locals.")]
        bool compact = true
    ) => FormatAsync(manager.ViewIlAsync(symbol, ct, compact), ToolTextFormatter.FormatViewIl);

    static async Task<string> FormatAsync<T>(Task<T> operation, Formatter<T> formatter)
    {
        var response = await operation;
        return formatter(in response);
    }
}
