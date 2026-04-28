using System.Reflection;

namespace RoslynQuery;

static class RoslynServerMetadata
{
    public static string GetDisplayVersion()
        => Assembly.GetExecutingAssembly()
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?
               .InformationalVersion
           ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
           ?? "0.0.0";

    public static string GetPackageVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString()
           ?? "0.0.0";
}
