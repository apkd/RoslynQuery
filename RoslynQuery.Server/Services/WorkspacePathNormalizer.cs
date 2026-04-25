namespace RoslynQuery;

enum WorkspacePathStyle
{
    Native,
    Wsl,
}

static class WorkspacePathNormalizer
{
    public static WorkspacePathStyle DetectStyle(string? path)
        => OperatingSystem.IsWindows() && IsWslMountedPath(path)
            ? WorkspacePathStyle.Wsl
            : WorkspacePathStyle.Native;

    public static string Normalize(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !IsWslMountedPath(path))
            return path;

        const string wslMountPrefix = "/mnt/";
        if (path.Length == wslMountPrefix.Length + 1 && char.IsAsciiLetter(path[wslMountPrefix.Length]))
            return char.ToUpperInvariant(path[wslMountPrefix.Length]) + @":\";

        var drive = char.ToUpperInvariant(path[wslMountPrefix.Length]);
        var remainder = path[(wslMountPrefix.Length + 2)..].Replace('/', '\\');
        return remainder.Length == 0 ? $"{drive}:\\" : $"{drive}:\\{remainder}";
    }

    public static string? Format(string? path, WorkspacePathStyle style)
    {
        if (style != WorkspacePathStyle.Wsl || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return path;

        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return path;

        var drive = char.ToLowerInvariant(path[0]);
        if (path.Length == 2)
            return $"/mnt/{drive}/";

        return $"/mnt/{drive}{path[2..].Replace('\\', '/')}";
    }

    static bool IsWslMountedPath(string? path)
    {
        const string wslMountPrefix = "/mnt/";
        return path is not null
            && path.StartsWith(wslMountPrefix, StringComparison.OrdinalIgnoreCase)
            && path.Length >= wslMountPrefix.Length + 1
            && char.IsAsciiLetter(path[wslMountPrefix.Length])
            && (path.Length == wslMountPrefix.Length + 1 || path[wslMountPrefix.Length + 1] == '/');
    }
}
