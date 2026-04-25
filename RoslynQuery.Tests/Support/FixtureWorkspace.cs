using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynQuery;

sealed class FixtureWorkspace : IAsyncDisposable
{
    FixtureWorkspace(string rootPath)
    {
        RootPath = rootPath;
        SolutionPath = Path.Combine(rootPath, "Sample.slnx");
        CoreProjectPath = Path.Combine(rootPath, "src", "Sample.Core", "Sample.Core.csproj");
        AppProjectPath = Path.Combine(rootPath, "src", "Sample.App", "Sample.App.csproj");
        OtherProjectPath = Path.Combine(rootPath, "src", "Sample.Other", "Sample.Other.csproj");
        ExternalAssemblyPath = Path.Combine(rootPath, "external", "Sample.External.dll");
        ConsumerPath = Path.Combine(rootPath, "src", "Sample.App", "Consumer.cs");
        DogPath = Path.Combine(rootPath, "src", "Sample.Core", "Dog.cs");
    }

    public string RootPath { get; }

    public string SolutionPath { get; }

    public string CoreProjectPath { get; }

    public string AppProjectPath { get; }

    public string OtherProjectPath { get; }

    public string ExternalAssemblyPath { get; }

    public string ConsumerPath { get; }

    public string DogPath { get; }

    public static FixtureWorkspace Create()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "RoslynQuery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        WriteExternalAssembly(Path.Combine(rootPath, "external", "Sample.External.dll"));
        Write(Path.Combine(rootPath, "Sample.slnx"), SampleSolution);
        Write(Path.Combine(rootPath, "src", "Sample.Core", "Sample.Core.csproj"), CoreProject);
        Write(Path.Combine(rootPath, "src", "Sample.Core", "Animal.cs"), AnimalCode);
        Write(Path.Combine(rootPath, "src", "Sample.Core", "Dog.cs"), DogCode);
        Write(Path.Combine(rootPath, "src", "Sample.Core", "Widget.Part1.cs"), WidgetPart1Code);
        Write(Path.Combine(rootPath, "src", "Sample.Core", "Widget.Part2.cs"), WidgetPart2Code);
        Write(Path.Combine(rootPath, "src", "Sample.App", "Sample.App.csproj"), AppProject);
        Write(Path.Combine(rootPath, "src", "Sample.App", "Consumer.cs"), ConsumerCode);
        Write(Path.Combine(rootPath, "src", "Sample.Other", "Sample.Other.csproj"), OtherProject);
        Write(Path.Combine(rootPath, "src", "Sample.Other", "Widget.cs"), OtherWidgetCode);

        return new(rootPath);
    }

    public static string CreateAmbiguousProjectDirectory()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "RoslynQuery", Guid.NewGuid().ToString("N"));
        Write(Path.Combine(rootPath, "One", "One.csproj"), StandaloneProject);
        Write(Path.Combine(rootPath, "Two", "Two.csproj"), StandaloneProject);
        return rootPath;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
            Directory.Delete(RootPath, recursive: true);

        return ValueTask.CompletedTask;
    }

    static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.TrimStart(), Encoding.UTF8);
    }

    const string SampleSolution = """
        <Solution>
          <Project Path="src/Sample.Core/Sample.Core.csproj" />
          <Project Path="src/Sample.App/Sample.App.csproj" />
          <Project Path="src/Sample.Other/Sample.Other.csproj" />
        </Solution>
        """;

    const string CoreProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    const string AppProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>

          <ItemGroup>
            <ProjectReference Include="..\Sample.Core\Sample.Core.csproj" />
            <ProjectReference Include="..\Sample.Other\Sample.Other.csproj" />
            <Reference Include="Sample.External">
              <HintPath>..\..\external\Sample.External.dll</HintPath>
            </Reference>
          </ItemGroup>
        </Project>
        """;

    const string OtherProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    const string StandaloneProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    const string AnimalCode = """
        namespace Sample.Core;

        public interface IGreeter
        {
            string Greet(string name);
        }

        public abstract class Animal
        {
            public abstract string Speak();

            public virtual string Kind => "animal";
        }
        """;

    const string DogCode = """
        using System.Diagnostics.CodeAnalysis;

        namespace Sample.Core;

        /// <summary>
        /// Represents a greeter dog.
        /// </summary>
        /// <remarks>
        /// Call <see cref="Greet(string)" /> when a greeting is needed.
        /// </remarks>
        [ExcludeFromCodeCoverage(Justification = "fixture")]
        public sealed class Dog : Animal, IGreeter
        {
            public override string Speak() => "woof";

            public override string Kind => "dog";

            /// <summary>
            /// Greets <paramref name="name" />.
            /// </summary>
            /// <param name="name">The person to greet.</param>
            /// <returns>A greeting string.</returns>
            [return: NotNull]
            public string Greet([DisallowNull] string name) => $"Hi {name}";

            public string GenericEcho(string value) => Echo(value);

            /// <summary>
            /// Returns <paramref name="value" /> unchanged.
            /// </summary>
            /// <typeparam name="T">The echoed value type.</typeparam>
            public static T Echo<T>([DisallowNull] T value) where T : notnull => value;

            public int Overload(int value) => value + 1;

            public int Overload(string value) => value.Length;
        }
        """;

    const string WidgetPart1Code = """
        namespace Sample.Core;

        public partial class Widget
        {
            public int Count { get; private set; }
        }
        """;

    const string WidgetPart2Code = """
        namespace Sample.Core;

        public partial class Widget
        {
            public void Build(int count)
            {
                Count = count;
            }
        }
        """;

    const string ConsumerCode = """
        using Sample.Core;
        using Sample.External;

        namespace Sample.App;

        public sealed class Consumer : IExternalGreeter
        {
            public string Run()
            {
                var dog = new Dog();
                var widget = new Widget();
                var external = new ExternalThing();
                widget.Build(dog.Overload(2));
                return external.Compute(2).ToString() + new Sample.Other.Models.Widget().Name + dog.Greet(dog.Speak()) + widget.Count;
            }

            public string Format(string name) => new ExternalThing().Echo(name);
        }
        """;

    const string OtherWidgetCode = """
        namespace Sample.Other.Models;

        public sealed class Widget
        {
            public string Name => "other";
        }
        """;

    const string ExternalAssemblyCode = """
        namespace Sample.External;

        public interface IExternalGreeter
        {
            string Format(string name);
        }

        public class ExternalThing
        {
            public int Compute(int value) => value + 42;

            public string Echo(string value) => value;

            public string Name => "external";

            public sealed class Nested
            {
                public int Value => 7;
            }
        }

        public sealed class Consumer
        {
            public string Name => "metadata";
        }
        """;

    static void WriteExternalAssembly(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var references = GetReferenceAssemblyPaths()
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "Sample.External",
            [CSharpSyntaxTree.ParseText(ExternalAssemblyCode)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var result = compilation.Emit(path);
        if (result.Success)
            return;

        var diagnostics = string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
        throw new InvalidOperationException("Failed to emit fixture metadata assembly:" + Environment.NewLine + diagnostics);
    }

    static string[] GetReferenceAssemblyPaths()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var dotnetRoot = Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not locate the dotnet root from the runtime directory.");
        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        var refDirectory = Directory.GetDirectories(packRoot)
            .Select(static path => (
                Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version(0, 0),
                Path: Path.Combine(path, "ref", "net10.0")))
            .Where(static item => Directory.Exists(item.Path))
            .OrderByDescending(static item => item.Version)
            .Select(static item => item.Path)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Could not locate the .NET 10 reference assemblies.");

        return Directory.GetFiles(refDirectory, "*.dll");
    }
}
