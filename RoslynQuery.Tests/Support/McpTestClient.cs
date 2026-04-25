using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynQuery;

sealed class McpTestClient : IAsyncDisposable
{
    readonly Process process;
    readonly StreamWriter stdin;
    readonly StreamReader stdout;
    readonly List<string> stderrLines = [];
    int nextId;

    McpTestClient(Process process)
    {
        this.process = process;
        stdin = process.StandardInput;
        stdout = process.StandardOutput;
    }

    public static async Task<McpTestClient> StartAsync(CancellationToken ct)
    {
        var repoRoot = RepoRoot.Find();
        string? serverDllPath = null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(repoRoot, "RoslynQuery.Server", "bin", "Debug", "net10.0", "roslynquery.dll"),
                     Path.Combine(repoRoot, "RoslynQuery.Server", "bin", "Debug", "net10.0", "win-x64", "roslynquery.dll"),
                 })
        {
            if (!File.Exists(candidate))
                continue;

            serverDllPath = candidate;
            break;
        }

        if (serverDllPath is null)
            throw new FileNotFoundException("The server binary was not found.");

        var process = new Process
        {
            StartInfo = new()
            {
                FileName = "dotnet",
                Arguments = $"\"{serverDllPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var client = new McpTestClient(process);
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { Length: > 0 })
                lock (client.stderrLines)
                    client.stderrLines.Add(args.Data);
        };
        process.BeginErrorReadLine();

        await client.SendRequestAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "RoslynQuery",
                    ["version"] = "dev",
                },
            },
            ct);

        await client.SendMessageAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
                ["params"] = null,
            },
            ct);

        return client;
    }

    public async Task<string> CallToolAsync(string name, object arguments, CancellationToken ct)
    {
        var response = await SendRequestAsync(
            "tools/call",
            new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonSerializer.SerializeToNode(arguments),
            },
            ct);

        if (response["result"]?["content"]?[0]?["text"]?.GetValue<string>() is { Length: > 0 } textContent)
            return textContent;

        return response["result"]?.ToJsonString() ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    async Task<JsonNode> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        await SendMessageAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            },
            ct);

        while (true)
        {
            var message = await ReadMessageAsync(ct);
            if (message["id"]?.GetValue<int>() == id)
                return message;
        }
    }

    async Task SendMessageAsync(JsonObject payload, CancellationToken ct)
    {
        var json = payload.ToJsonString();
        await stdin.WriteLineAsync(json.AsMemory(), ct);
        await stdin.FlushAsync();
    }

    async Task<JsonNode> ReadMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await stdout.ReadLineAsync(ct);
            if (line is null)
            {
                lock (stderrLines)
                    throw new EndOfStreamException(
                        "Unexpected EOF while reading MCP messages.\n"
                        + (stderrLines.Count == 0 ? string.Empty : string.Join("\n", stderrLines)));
            }

            if (line.Length == 0)
                continue;

            try
            {
                return JsonNode.Parse(line)!;
            }
            catch (JsonException)
            {
                continue;
            }
        }
    }
}
