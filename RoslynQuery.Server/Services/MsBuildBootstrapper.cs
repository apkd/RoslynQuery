using Microsoft.Build.Locator;

namespace RoslynQuery;

static class MsBuildBootstrapper
{
    static readonly Lock gate = new();

    public static void EnsureRegistered()
    {
        lock (gate)
        {
            BuildHostExtractor.EnsurePresent();

            if (MSBuildLocator.IsRegistered)
                return;

            MSBuildLocator.RegisterDefaults();
        }
    }
}
