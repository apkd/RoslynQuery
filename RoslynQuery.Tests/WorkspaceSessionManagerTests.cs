using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using static System.StringComparison;

namespace RoslynQuery;

public sealed class WorkspaceSessionManagerTests
{
    [Test]
    public async Task ServerAssemblyEmbedsBothBuildHosts()
    {
        var resourceNames = typeof(RoslynTools).Assembly.GetManifestResourceNames();

        await Assert.That(resourceNames).Contains("RoslynQuery.BuildHostNetCore.Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll");
        await Assert.That(resourceNames).Contains("RoslynQuery.BuildHostNet472.Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe");
    }

    [Test]
    public async Task LoadWorkspaceDirectoryLoadsSolutionAndStatusListsProjects()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);

        var opened = await manager.LoadAsync(fixture.RootPath, CancellationToken.None);
        var status = await manager.StatusAsync(CancellationToken.None);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(opened.TargetKind).IsEqualTo("solution");
        await Assert.That(opened.ResolvedPath).IsEqualTo(fixture.SolutionPath);
        await Assert.That(status.Projects).Count().IsEqualTo(3);
        foreach (var project in status.Projects)
            await Assert.That(project.TargetFramework).IsEqualTo("net10.0");

        await Assert.That(status.Projects).Contains(project => project.Documents.Contains(fixture.ConsumerPath, StringComparer.OrdinalIgnoreCase));
    }

    [Test]
    public async Task LoadWorkspaceHonorsRoslynQueryIgnoreProjectGlobs()
    {
        await using var fixture = FixtureWorkspace.Create();
        File.WriteAllText(Path.Combine(fixture.RootPath, ".roslynqueryignore"), "src/*Other/*.csproj");
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);

        var opened = await manager.LoadAsync(fixture.RootPath, CancellationToken.None);
        var status = await manager.StatusAsync(CancellationToken.None);
        var widget = await manager.DescribeSymbolAsync("Widget", CancellationToken.None);
        var formatted = ToolTextFormatter.FormatLoadWorkspace(opened);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(opened.Status.ExcludedProjectCount).IsEqualTo(1);
        await Assert.That(opened.Status.Messages).DoesNotContain(message => string.Equals(message.Severity, "info", Ordinal));
        await Assert.That(formatted).Contains("Projects: 2 (1 excluded via .roslynqueryignore)", Ordinal);
        await Assert.That(status.Projects).Count().IsEqualTo(2);
        await Assert.That(status.ExcludedProjectCount).IsEqualTo(1);
        await Assert.That(status.Projects).DoesNotContain(project => string.Equals(project.Name, "Sample.Other", Ordinal));
        await Assert.That(status.Projects).DoesNotContain(project => string.Equals(project.ProjectPath, fixture.OtherProjectPath, OrdinalIgnoreCase));
        await Assert.That(widget.Success).IsTrue();
        await Assert.That(widget.Symbol?.ContainingNamespace).IsEqualTo("Sample.Core");
    }

    [Test]
    public async Task LoadWorkspaceHonorsRoslynQueryIgnoreForClassicSolutionFiles()
    {
        await using var fixture = FixtureWorkspace.Create();
        var solutionPath = Path.Combine(fixture.RootPath, "Sample.sln");
        File.WriteAllText(solutionPath, ClassicSolution);
        File.WriteAllText(Path.Combine(fixture.RootPath, ".roslynqueryignore"), "Sample.Other");
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);

        var opened = await manager.LoadAsync(solutionPath, CancellationToken.None);
        var status = await manager.StatusAsync(CancellationToken.None);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(status.Projects).Count().IsEqualTo(2);
        await Assert.That(status.Projects).DoesNotContain(project => string.Equals(project.Name, "Sample.Other", Ordinal));
    }

    [Test]
    public async Task LoadWorkspacePrimesIndexBuildInBackground()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);

        var opened = await manager.LoadAsync(fixture.RootPath, CancellationToken.None);
        var session = GetActiveSession(manager);
        var indexTask = GetIndexTask(session);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(indexTask is not null).IsTrue();

        static object GetActiveSession(WorkspaceSessionManager manager)
            => typeof(WorkspaceSessionManager)
                   .GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                   .GetValue(manager)!
               ?? throw new InvalidOperationException("The active session was not set.");

        static Task GetIndexTask(object session)
            => (Task)(typeof(WorkspaceSessionManager).Assembly
                          .GetType("RoslynQuery.WorkspaceSession")!
                          .GetField("indexTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                          .GetValue(session)!
                      ?? throw new InvalidOperationException("The index task was not created."));
    }

    [Test]
    public async Task LoadWorkspaceAcceptsWslMountedSolutionPathsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        var drive = char.ToLowerInvariant(fixture.SolutionPath[0]);
        var wslPath = $"/mnt/{drive}/{fixture.SolutionPath[3..].Replace('\\', '/')}";

        var opened = await manager.LoadAsync(wslPath, CancellationToken.None);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(opened.TargetKind).IsEqualTo("solution");
        await Assert.That(opened.ResolvedPath).IsEqualTo(wslPath);
        await Assert.That(opened.Status.TargetPath).IsEqualTo(wslPath);
    }

    [Test]
    public async Task WslMountedWorkspacePathsAreReturnedInWslFormOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        var wslSolutionPath = ToWslPath(fixture.SolutionPath);
        var wslAppProjectPath = ToWslPath(fixture.AppProjectPath);
        var wslConsumerPath = ToWslPath(fixture.ConsumerPath);

        var opened = await manager.LoadAsync(wslSolutionPath, CancellationToken.None);
        var status = await manager.StatusAsync(CancellationToken.None);

        await Assert.That(opened.Success).IsTrue();
        await Assert.That(status.Projects).Contains(project => string.Equals(project.ProjectPath, wslAppProjectPath, Ordinal));
        await Assert.That(status.Projects).Contains(project => project.Documents.Contains(wslConsumerPath, StringComparer.Ordinal));
    }

    [Test]
    public async Task LoadWorkspaceReturnsCandidatesForAmbiguousProjectDirectory()
    {
        var rootPath = FixtureWorkspace.CreateAmbiguousProjectDirectory();
        try
        {
            var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
            var opened = await manager.LoadAsync(rootPath, CancellationToken.None);

            await Assert.That(opened.Success).IsFalse();
            await Assert.That(opened.Error).Contains("multiple project files", Ordinal);
            await Assert.That(opened.Candidates).Count().IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Test]
    public async Task DescribeSymbolUsesBestEffortQueriesAndReturnsCandidatesForAmbiguousNames()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var described = await manager.DescribeSymbolAsync("Dog", CancellationToken.None);
        var method = await manager.DescribeSymbolAsync("Dog.Greet", CancellationToken.None);
        var genericMethod = await manager.DescribeSymbolAsync("Dog.Echo", CancellationToken.None);
        var property = await manager.DescribeSymbolAsync("Sample.Core.Widget.Count", CancellationToken.None);
        var ambiguous = await manager.DescribeSymbolAsync("Widget", CancellationToken.None);

        await Assert.That(described.Success).IsTrue();
        await Assert.That(described.Symbol?.Kind).IsEqualTo("class");
        await Assert.That(described.Symbol?.ShortName).IsEqualTo("Dog");
        await Assert.That(described.Symbol?.Attributes).Contains(attribute => attribute.Text.Contains("ExcludeFromCodeCoverage", Ordinal));
        await Assert.That(described.Symbol?.Documentation.Sections).Contains(section => section.Name == "summary" && section.Text.Contains("greeter dog", Ordinal));
        await Assert.That(described.Usages.Count).IsGreaterThan(0);
        await Assert.That(described.Relations).Contains(relation => relation.Relation == "base_types" && relation.Symbol.CanonicalSignature.Contains("Animal", Ordinal));
        await Assert.That(described.Relations).Contains(relation => relation.Relation == "implemented_interfaces" && relation.Symbol.CanonicalSignature.Contains("IGreeter", Ordinal));
        await Assert.That(described.TypeMembers?.MethodCount ?? 0).IsGreaterThanOrEqualTo(5);
        await Assert.That(described.Diagnostics).IsEmpty();
        await Assert.That(method.Success).IsTrue();
        await Assert.That(method.Symbol?.Kind).IsEqualTo("method");
        await Assert.That(method.Symbol?.DisplaySignature).Contains("Dog.Greet", Ordinal);
        await Assert.That(method.Symbol?.ReturnAttributes).Contains(attribute => attribute.Text.Contains("NotNull", Ordinal));
        await Assert.That(method.Symbol?.Parameters).Contains(parameter => parameter.Attributes.Any(attribute => attribute.Text.Contains("DisallowNull", Ordinal)));
        await Assert.That(method.Symbol?.Documentation.Sections).Contains(section => section.Name == "param" && section.Attributes.Contains("name=\"name\"") && section.Text.Contains("person to greet", Ordinal));
        await Assert.That(method.Usages.Count).IsGreaterThan(0);
        await Assert.That(genericMethod.Success).IsTrue();
        await Assert.That(genericMethod.Symbol?.TypeParameters).Contains(parameter => parameter.Name == "T" && parameter.Constraints.Contains("notnull"));
        await Assert.That(property.Success).IsTrue();
        await Assert.That(property.Symbol?.Accessors).Contains(accessor => accessor is { Kind: "get", Accessibility: "public" });
        await Assert.That(property.Symbol?.Accessors).Contains(accessor => accessor is { Kind: "set", Accessibility: "private" });
        await Assert.That(ambiguous.Success).IsFalse();
        await Assert.That(ambiguous.Candidates).Count().IsEqualTo(2);
    }

    [Test]
    public async Task ListMembersFindUsagesAndFindRelatedSymbolsWorkAcrossProjects()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var members = await manager.ListTypeMembersAsync("Dog", includeInherited: true, CancellationToken.None);
        var usages = await manager.FindUsagesAsync("Dog.Greet", CancellationToken.None);
        var relatedType = await manager.FindRelatedSymbolsAsync("Dog", null, CancellationToken.None);
        var relatedMethod = await manager.FindRelatedSymbolsAsync(
            "Dog.Speak",
            ["overridden_members"],
            CancellationToken.None
        );

        await Assert.That(members.Symbol?.CanonicalSignature).IsEqualTo("Sample.Core::Sample.Core.Dog");
        await Assert.That(members.Members).Contains(member => member.DisplaySignature.Contains("Dog.Greet", Ordinal) && member.ReturnType == "string");
        await Assert.That(members.Members).Contains(member => member.DisplaySignature.Contains("Dog.Kind", Ordinal) && member.ValueType == "string");
        await Assert.That(members.Members).Contains(member => member.CanonicalSignature.Contains("Animal.Speak", Ordinal));
        await Assert.That(usages.References).Contains(reference => string.Equals(reference.DocumentPath, fixture.ConsumerPath, OrdinalIgnoreCase));
        await Assert.That(relatedType.Relations).Contains(relation => relation.Relation == "base_types" && relation.Symbol.CanonicalSignature.Contains("Animal", Ordinal));
        await Assert.That(relatedType.Relations).Contains(relation => relation.Relation == "implemented_interfaces" && relation.Symbol.CanonicalSignature.Contains("IGreeter", Ordinal));
        await Assert.That(relatedMethod.Relations).Contains(relation => relation.Symbol.CanonicalSignature.Contains("Animal.Speak", Ordinal));
    }

    [Test]
    public async Task ExternalMetadataSymbolsResolveLazily()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var described = await manager.DescribeSymbolAsync("Sample.External.ExternalThing", CancellationToken.None);
        var members = await manager.ListTypeMembersAsync("Sample.External.ExternalThing", includeInherited: false, CancellationToken.None);
        var usages = await manager.FindUsagesAsync("Sample.External.ExternalThing.Compute(int)", CancellationToken.None);
        var related = await manager.FindRelatedSymbolsAsync(
            "Sample.External.IExternalGreeter",
            ["implementations"],
            CancellationToken.None
        );

        var il = await manager.ViewIlAsync("Sample.External.ExternalThing.Compute(int)", CancellationToken.None);
        var stringLength = await manager.DescribeSymbolAsync("string.Length", CancellationToken.None);
        var nested = await manager.DescribeSymbolAsync("Sample.External.ExternalThing.Nested", CancellationToken.None);
        var sourceFirst = await manager.DescribeSymbolAsync("Consumer", CancellationToken.None);
        var shortExternal = await manager.DescribeSymbolAsync("ExternalThing", CancellationToken.None);

        await Assert.That(described.Success).IsTrue();
        await Assert.That(described.Symbol?.Origin).IsEqualTo("metadata");
        await Assert.That(described.Symbol?.Project).IsEqualTo("Sample.External");
        await Assert.That(described.Symbol?.AssemblyPath).IsEqualTo(fixture.ExternalAssemblyPath);
        await Assert.That(described.Symbol?.Locations).IsEmpty();
        await Assert.That(members.Success).IsTrue();
        await Assert.That(members.Members).Contains(member => member.DisplaySignature.Contains("ExternalThing.Compute", Ordinal) && member.ReturnType == "int");
        await Assert.That(members.Members).Contains(member => member.DisplaySignature.Contains("ExternalThing.Name", Ordinal) && member.ValueType == "string");
        await Assert.That(usages.Success).IsTrue();
        await Assert.That(usages.References).Contains(reference => string.Equals(reference.DocumentPath, fixture.ConsumerPath, OrdinalIgnoreCase));
        await Assert.That(related.Success).IsTrue();
        await Assert.That(related.Relations).Contains(relation => relation.Symbol.CanonicalSignature.Contains("Sample.App.Consumer", Ordinal));
        await Assert.That(il.Success).IsTrue();
        await Assert.That(il.Methods).HasSingleItem();
        await Assert.That(il.Methods.Single().Instructions).Contains(instruction => instruction.Contains("add", Ordinal));
        await Assert.That(stringLength.Success).IsTrue();
        await Assert.That(stringLength.Symbol?.Origin).IsEqualTo("metadata");
        await Assert.That(stringLength.Symbol?.Kind).IsEqualTo("property");
        await Assert.That(stringLength.Symbol?.ValueType).IsEqualTo("int");
        await Assert.That(nested.Success).IsTrue();
        await Assert.That(nested.Symbol?.Origin).IsEqualTo("metadata");
        await Assert.That(nested.Symbol?.Kind).IsEqualTo("class");
        await Assert.That(sourceFirst.Success).IsTrue();
        await Assert.That(sourceFirst.Symbol?.CanonicalSignature).IsEqualTo("Sample.App::Sample.App.Consumer");
        await Assert.That(shortExternal.Success).IsFalse();
    }

    [Test]
    public async Task ViewIlReturnsMethodInstructions()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var response = await manager.ViewIlAsync("Dog.Greet", CancellationToken.None);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Methods).HasSingleItem();
        var method = response.Methods.Single();
        await Assert.That(method.MetadataName).IsEqualTo("Greet");
        await Assert.That(method.CodeSize).IsGreaterThan(0);
        await Assert.That(method.Instructions).Contains(instruction => instruction.Contains("ldarg.1", Ordinal));
        await Assert.That(method.Instructions).Contains(instruction => instruction.Contains("ret", Ordinal));
    }

    [Test]
    public async Task ViewIlReturnsPropertyAccessorInstructions()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var response = await manager.ViewIlAsync("Count", CancellationToken.None);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Methods).Count().IsEqualTo(2);
        await Assert.That(response.Methods).Contains(method => method.MetadataName == "get_Count" && method.Instructions.Any(instruction => instruction.Contains("ldfld", Ordinal)));
        await Assert.That(response.Methods).Contains(method => method.MetadataName == "set_Count" && method.Instructions.Any(instruction => instruction.Contains("stfld", Ordinal)));
    }

    [Test]
    public async Task ViewIlDecodesGenericMethodArgumentsAndMethodSignatures()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var response = await manager.ViewIlAsync("Dog.GenericEcho", CancellationToken.None);
        var fullResponse = await manager.ViewIlAsync("Dog.GenericEcho", CancellationToken.None, compact: false);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Methods).HasSingleItem();
        var method = response.Methods.Single();
        await Assert.That(method.Instructions).Contains(instruction => instruction.Contains("Dog.Echo<string>(string)", Ordinal));
        await Assert.That(method.Instructions).DoesNotContain(instruction => instruction.Contains("Sample.Core.Dog::Echo", Ordinal));
        await Assert.That(method.Instructions).DoesNotContain(instruction => instruction.Contains("<...>", Ordinal));

        await Assert.That(fullResponse.Success).IsTrue();
        await Assert.That(fullResponse.Methods).HasSingleItem();
        var fullMethod = fullResponse.Methods.Single();
        await Assert.That(fullMethod.Instructions).Contains(instruction => instruction.Contains("Sample.Core.Dog::Echo<string>(string)", Ordinal));
    }

    [Test]
    public async Task ViewIlDecodesLocalSignature()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var response = await manager.ViewIlAsync("Consumer.Run", CancellationToken.None);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Methods).HasSingleItem();
        var method = response.Methods.Single();
        await Assert.That(method.Locals).Contains(local => local.Contains("Dog", Ordinal));
        await Assert.That(method.Locals).Contains(local => local.Contains("Widget", Ordinal));
    }

    [Test]
    public async Task DiagnosticsChangeAfterReloadingWithLoadWorkspace()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var before = await manager.StatusAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            fixture.DogPath,
            """
            namespace Sample.Core;

            public sealed class Dog : Animal, IGreeter
            {
                public override string Speak() => "woof"
            }
            """,
            CancellationToken.None
        );

        var stale = await manager.StatusAsync(CancellationToken.None);
        var reloaded = await manager.LoadAsync(fixture.RootPath, CancellationToken.None);
        var after = await manager.StatusAsync(CancellationToken.None);
        var diagnostics = await manager.ShowDiagnosticsAsync("all", CancellationToken.None);

        await Assert.That(before.Diagnostics).IsEmpty();
        await Assert.That(stale.Diagnostics).IsEmpty();
        await Assert.That(reloaded.Success).IsTrue();
        await Assert.That(after.Diagnostics).IsEmpty();
        await Assert.That(diagnostics.Diagnostics).Contains(diagnostic => string.Equals(diagnostic.Location?.FilePath, fixture.DogPath, OrdinalIgnoreCase));
        await Assert.That(ToolTextFormatter.FormatShowDiagnostics(diagnostics)).Contains("Diagnostics (all):", Ordinal);
    }

    [Test]
    public async Task ShowDiagnosticsHonorsVerbosity()
    {
        await using var fixture = FixtureWorkspace.Create();
        var manager = new WorkspaceSessionManager(NullLogger<WorkspaceSessionManager>.Instance);
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);
        await File.WriteAllTextAsync(
            fixture.DogPath,
            """
            namespace Sample.Core;

            public sealed class Dog : Animal, IGreeter
            {
                public override string Speak() => "woof"
            }
            """,
            CancellationToken.None
        );
        await manager.LoadAsync(fixture.RootPath, CancellationToken.None);

        var errors = await manager.ShowDiagnosticsAsync("errors", CancellationToken.None);
        var invalid = await manager.ShowDiagnosticsAsync("chatty", CancellationToken.None);

        await Assert.That(errors.Success).IsTrue();
        await Assert.That(errors.Verbosity).IsEqualTo("errors");
        await Assert.That(errors.Diagnostics.Length).IsGreaterThan(0);
        foreach (var diagnostic in errors.Diagnostics)
            await Assert.That(diagnostic.Severity).IsEqualTo("error");

        await Assert.That(invalid.Success).IsFalse();
        await Assert.That(invalid.Error).Contains("Invalid verbosity", Ordinal);
    }

    [Test]
    public async Task WorkspaceMessagesOnlyRecordVisualStudioBuildToolsWarningOnce()
    {
        var messages = new WorkspaceMessageBuffer();
        const string warning = "[Microsoft.CodeAnalysis.MSBuild.BuildHostProcessManager] An installation of Visual Studio or the Build Tools for Visual Studio could not be found; A.csproj will be loaded with the .NET Core SDK and may encounter errors.";

        await Assert.That(messages.TryAdd("warning", warning)).IsTrue();
        await Assert.That(messages.TryAdd("warning", warning.Replace("A.csproj", "B.csproj", Ordinal))).IsFalse();
        await Assert.That(messages.TryAdd("warning", "Some other workspace warning.")).IsTrue();

        var result = messages.ToArray();
        await Assert.That(result).Count().IsEqualTo(2);
        await Assert.That(result[0].Message).IsEqualTo(warning);
        await Assert.That(result[1].Message).IsEqualTo("Some other workspace warning.");
    }

    static string ToWslPath(string windowsPath)
    {
        var drive = char.ToLowerInvariant(windowsPath[0]);
        return $"/mnt/{drive}/{windowsPath[3..].Replace('\\', '/')}";
    }

    const string ClassicSolution = """
                                   Microsoft Visual Studio Solution File, Format Version 12.00
                                   # Visual Studio Version 17
                                   VisualStudioVersion = 17.0.31903.59
                                   MinimumVisualStudioVersion = 10.0.40219.1
                                   Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Core", "src\Sample.Core\Sample.Core.csproj", "{11111111-1111-1111-1111-111111111111}"
                                   EndProject
                                   Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.App", "src\Sample.App\Sample.App.csproj", "{22222222-2222-2222-2222-222222222222}"
                                   EndProject
                                   Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Other", "src\Sample.Other\Sample.Other.csproj", "{33333333-3333-3333-3333-333333333333}"
                                   EndProject
                                   Global
                                       GlobalSection(SolutionConfigurationPlatforms) = preSolution
                                           Debug|Any CPU = Debug|Any CPU
                                       EndGlobalSection
                                       GlobalSection(ProjectConfigurationPlatforms) = postSolution
                                           {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                                           {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                                           {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                                           {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                                           {33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                                           {33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
                                       EndGlobalSection
                                   EndGlobal
                                   """;
}
