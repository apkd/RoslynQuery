using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynQuery;

sealed class WorkspaceMessageBuffer
{
    internal const string MissingVisualStudioBuildToolsWarning =
        "An installation of Visual Studio or the Build Tools for Visual Studio could not be found";

    readonly ConcurrentQueue<WorkspaceMessageDto> messages = new();
    readonly ConcurrentDictionary<string, byte> loggedOnceMessageKeys = new(StringComparer.Ordinal);

    internal WorkspaceMessageDto[] ToArray() => messages.ToArray();

    internal void Add(WorkspaceDiagnostic diagnostic)
    {
        if (ShouldSuppress(diagnostic))
            return;

        TryAdd(
            diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "error" : "warning",
            diagnostic.Message
        );
    }

    internal bool TryAdd(string severity, string message)
    {
        var onceKey = GetOnceKey(severity, message);
        if (onceKey is not null && !loggedOnceMessageKeys.TryAdd(onceKey, 0))
            return false;

        messages.Enqueue(
            new()
            {
                Severity = severity,
                Message = message,
            }
        );

        return true;
    }

    static bool ShouldSuppress(WorkspaceDiagnostic diagnostic)
    {
        if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            return false;

        const string prefix = "Found project reference without a matching metadata reference: ";
        return diagnostic.Message.StartsWith(prefix, StringComparison.Ordinal);
    }

    static string? GetOnceKey(string severity, string message)
        => string.Equals(severity, "warning", StringComparison.Ordinal)
           && message.Contains(MissingVisualStudioBuildToolsWarning, StringComparison.Ordinal)
            ? MissingVisualStudioBuildToolsWarning
            : null;
}
