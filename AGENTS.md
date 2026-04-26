# Compact project overview

Project layout:
- `RoslynQuery.Server`: MCP server implementation.
- `RoslynQuery.Server/Services`: workspace/session lifecycle, symbol indexing, target resolution, and Roslyn/MSBuild/build-host bootstrapping.
- `RoslynQuery.Server/Models.cs`: public DTOs returned by MCP tools.
- `RoslynQuery.Tests`: server-side tests.
- `RoslynQuery.Tests/Support`: generated fixture workspaces plus the stdio MCP test client.

Tests:
- Run the test suite after making changes.
- `dotnet build -v minimal`
- `dotnet test -v minimal`

Benchmarking:
- To measure initial workspace load plus full source symbol index build, run `dotnet run -c Release --project RoslynQuery.Server -- --benchmark-init <solution-or-project-path>`.
- Always pass the target solution/project path explicitly; the benchmark command does not use a default workspace.
- The command prints JSON with `load_duration_ms`, `index_wait_duration_ms`, and `total_duration_ms`. Use `total_duration_ms` when comparing initial load/index rebuild changes.

Roslyn/MSBuild constraints:
- `Microsoft.Build.Locator` must register before any MSBuild or `MSBuildWorkspace` type is touched.
- Do not introduce runtime-loaded `Microsoft.Build.*` package assemblies; if such references are required, they must keep `ExcludeAssets="runtime"` and `PrivateAssets="all"`.
- This project relies on the machine's installed .NET SDK/MSBuild and machine runtime for solution/project loading.
- Release packaging is framework-dependent single-file. `BuildHost-netcore` and `BuildHost-net472` are embedded and extracted beside the executable on the first workspace load.
- Publish modes must stay conservative: do not use trimming or NativeAOT.

C# conventions:
- Do not write obvious comments; prefer self-documenting code.
- Prefer nested local functions when a helper is used only once.
- Prefer expression-bodied members for one-line methods.
- Drop braces from single-statement `if` and `while` blocks.
- Split long method chains across multiple lines.
- Make classes `static` when they have no instance members.
- Make classes `sealed` when inheritance is not required.
- Do not make classes/members `public` unless necessary.
- Prefer `foreach` over manual indexed loops for readability.
- Do not explicitly write `private` in member declarations.
- Prefer stateless static helpers over object state where practical.
- Use modern .NET 10 features. Compact pattern matching is preferred over procedural code.
- Simplicity is paramount. Avoid complicating the code. Always think carefully until you find the simplest, most elegant solution.

Guidance for contributors:
- Read existing related server and test code before editing; follow established code style and architecture patterns.
- Preserve the one-active-workspace model unless a task explicitly requires changing it.
- Keep MCP tool contracts stable and compact plain-text oriented.
- Focus on writing performance-oriented code that achieves the highest possible runtime performance.
- Assume no backwards compatibility is needed.
