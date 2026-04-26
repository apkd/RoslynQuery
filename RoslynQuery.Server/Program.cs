using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynQuery;
using ZLogger;

var versionOption = new Option<bool>("--version")
{
    Description = "Print the RoslynQuery server version and exit.",
};

var benchmarkInitOption = new Option<string?>("--benchmark-init")
{
    Description = "Load the target solution/project, wait for the source symbol index build, print timings, and exit.",
};

var rootCommand = new RootCommand("RoslynQuery server")
{
    benchmarkInitOption,
    versionOption,
};

rootCommand.SetAction(async parseResult =>
    {
        if (parseResult.GetValue(versionOption))
        {
            Console.Out.WriteLine(RoslynServerMetadata.GetDisplayVersion());
            return 0;
        }

        var benchmarkTarget = parseResult.GetValue(benchmarkInitOption);
        if (!string.IsNullOrWhiteSpace(benchmarkTarget))
            return await RunInitializationBenchmarkAsync(benchmarkTarget);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddZLoggerConsole(options => { options.LogToStandardErrorThreshold = LogLevel.Trace; });
        builder.Services.AddSingleton<WorkspaceSessionManager>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<RoslynTools>(
                new(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                    WriteIndented = true,
                }
            );

        await builder.Build().RunAsync();
        return 0;
    }
);

return await rootCommand.Parse(args).InvokeAsync();

static async Task<int> RunInitializationBenchmarkAsync(string target)
{
    using var loggerFactory = LoggerFactory.Create(static _ => { });
    var manager = new WorkspaceSessionManager(loggerFactory.CreateLogger<WorkspaceSessionManager>());
    var result = await manager.BenchmarkInitializationAsync(target, CancellationToken.None);
    Console.Out.WriteLine(JsonSerializer.Serialize(result, CreateBenchmarkSerializerOptions()));
    return result.Success ? 0 : 1;
}

static JsonSerializerOptions CreateBenchmarkSerializerOptions()
    => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
    };
