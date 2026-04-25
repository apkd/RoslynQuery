using static System.StringComparison;

namespace RoslynQuery;

public sealed class StdioServerTests
{
    [Test]
    public async Task ServerHandlesBestEffortSymbolQueriesOverMcp()
    {
        await using var fixture = FixtureWorkspace.Create();
        await using var client = await McpTestClient.StartAsync(CancellationToken.None);

        var opened = await client.CallToolAsync("load_workspace", new { path = fixture.RootPath }, CancellationToken.None);
        var described = await client.CallToolAsync("describe_symbol", new { symbol = "Dog" }, CancellationToken.None);
        var members = await client.CallToolAsync("list_type_members", new { symbol = "Dog" }, CancellationToken.None);
        var usages = await client.CallToolAsync("find_usages", new { symbol = "Dog.Greet" }, CancellationToken.None);
        var il = await client.CallToolAsync("view_il", new { symbol = "Dog.Greet" }, CancellationToken.None);

        await Assert.That(opened).Contains("Loaded solution.", Ordinal);
        await Assert.That(opened).Contains(fixture.SolutionPath, OrdinalIgnoreCase);
        await Assert.That(described).StartsWith("Sample.Core::Sample.Core.Dog", Ordinal);
        await Assert.That(described).DoesNotContain("Canonical:", Ordinal);
        await Assert.That(described).Contains("Type Members:", Ordinal);
        await Assert.That(described).Contains("Relationships:", Ordinal);
        await Assert.That(described).Contains("Usages:", Ordinal);
        await Assert.That(described).DoesNotContain("Diagnostics:", Ordinal);
        await Assert.That(described).Contains("Documentation:", Ordinal);
        await Assert.That(described).DoesNotContain("Project Path:", Ordinal);
        await Assert.That(described).DoesNotContain("Namespace:", Ordinal);
        await Assert.That(members).StartsWith("Members of Sample.Core::Sample.Core.Dog", Ordinal);
        await Assert.That(members).Contains("- string Greet(string name)", Ordinal);
        await Assert.That(members).Contains("- string Kind", Ordinal);
        await Assert.That(usages).Contains("Usages of Sample.Core.Dog.Greet(string name)", Ordinal);
        await Assert.That(usages).Contains("Consumer.cs", Ordinal);
        await Assert.That(il).StartsWith("Sample.Core.Dog.Greet(string name)", Ordinal);
        await Assert.That(il).DoesNotContain("IL for ", Ordinal);
        await Assert.That(il).Contains("0000 ", Ordinal);
        await Assert.That(il).Contains("ret", Ordinal);
        await Assert.That(il).DoesNotContain("IL_", Ordinal);
        await Assert.That(il).DoesNotContain("0x", Ordinal);
        await Assert.That(il).DoesNotContain("Token:", Ordinal);
        await Assert.That(il).DoesNotContain("RVA:", Ordinal);
        await Assert.That(il).DoesNotContain("Implementation:", Ordinal);
        await Assert.That(il).DoesNotContain("Local Signature:", Ordinal);
    }

    [Test]
    public async Task ServerReturnsWslPathsAndTargetFrameworkOverMcp()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var fixture = FixtureWorkspace.Create();
        await using var client = await McpTestClient.StartAsync(CancellationToken.None);
        var wslSolutionPath = ToWslPath(fixture.SolutionPath);

        var opened = await client.CallToolAsync("load_workspace", new { path = wslSolutionPath }, CancellationToken.None);
        var status = await client.CallToolAsync("status", new { }, CancellationToken.None);

        await Assert.That(opened).Contains("Loaded solution.", Ordinal);
        await Assert.That(opened).Contains(wslSolutionPath, Ordinal);
        await Assert.That(status).Contains("Path: " + wslSolutionPath, Ordinal);
        await Assert.That(status).Contains("- Sample.App [net10.0] src/Sample.App/Sample.App.csproj", Ordinal);
    }

    [Test]
    public async Task ServerPrefersRootSolutionWhenNestedSolutionsExist()
    {
        await using var fixture = FixtureWorkspace.Create();
        foreach (var nestedSolutionPath in new[]
                 {
                     Path.Combine(fixture.RootPath, "Library", "com.singularitygroup.hotreload", "Solution", "HighlandKeep.sln"),
                     Path.Combine(fixture.RootPath, "Library", "com.singularitygroup.hotreload", "Solution", "hk.sln"),
                 })
            WritePlaceholderSolution(nestedSolutionPath);

        await using var client = await McpTestClient.StartAsync(CancellationToken.None);

        var opened = await client.CallToolAsync("load_workspace", new { path = fixture.RootPath }, CancellationToken.None);

        await Assert.That(opened).Contains("Loaded solution.", Ordinal);
        await Assert.That(opened).Contains(fixture.SolutionPath, OrdinalIgnoreCase);
        await Assert.That(opened).DoesNotContain("Candidates:", Ordinal);
    }

    [Test]
    public async Task ServerListsOnlyRootLevelSolutionsWhenRootIsAmbiguous()
    {
        await using var fixture = FixtureWorkspace.Create();
        var otherRootSolutionPath = Path.Combine(fixture.RootPath, "Other.sln");
        var nestedSolutionPath = Path.Combine(fixture.RootPath, "Library", "com.singularitygroup.hotreload", "Solution", "Ignored.sln");
        WritePlaceholderSolution(otherRootSolutionPath);
        WritePlaceholderSolution(nestedSolutionPath);

        await using var client = await McpTestClient.StartAsync(CancellationToken.None);

        var opened = await client.CallToolAsync("load_workspace", new { path = fixture.RootPath }, CancellationToken.None);

        await Assert.That(opened).Contains("contains multiple solution files.", Ordinal);
        await Assert.That(opened).Contains("Candidates:", Ordinal);
        await Assert.That(opened).Contains(fixture.SolutionPath, OrdinalIgnoreCase);
        await Assert.That(opened).Contains(otherRootSolutionPath, OrdinalIgnoreCase);
        await Assert.That(opened).DoesNotContain(nestedSolutionPath, OrdinalIgnoreCase);
    }

    static void WritePlaceholderSolution(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00");
    }

    static string ToWslPath(string windowsPath)
    {
        var drive = char.ToLowerInvariant(windowsPath[0]);
        return $"/mnt/{drive}/{windowsPath[3..].Replace('\\', '/')}";
    }
}
