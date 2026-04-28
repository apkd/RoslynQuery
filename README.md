```
              `OooOOo.               o               .oOOOo.                             
               o     `o             O               .O     o.                            
               O      O             o               o       O                            
               o     .O             O               O       o                            
               OOooOO'  .oOo. .oOo  o  O   o 'OoOo. o       O  O   o  .oOo. `OoOo. O   o 
               o    o   O   o `Ooo. O  o   O  o   O O    Oo o  o   O  OooO'  o     o   O 
               O     O  o   O     O o  O   o  O   o `o     O'  O   o  O      O     O   o 
               O      o `OoO' `OoO' Oo `OoOO  o   O  `OoooO Oo `OoO'o `OoO'  o     `OoOO 
                                           o                                           o 
                                        OoO'                                        OoO' 
```

[![MIT License](https://img.shields.io/github/license/apkd/RoslynQuery?style=flat&label=License&logo=listmonk&labelColor=2C3439&color=fff)](https://github.com/apkd/RoslynQuery/blob/master/LICENSE)
[![Test status badge](https://github.com/apkd/RoslynQuery/actions/workflows/build-test-release.yml/badge.svg?branch=master&event=push)](https://github.com/apkd/RoslynQuery/actions/workflows/build-test-release.yml)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/apkd/RoslynQuery?authorFilter=apkd&label=Commits&labelColor=2C3439&color=EBFF65&logo=git)](https://github.com/apkd/RoslynQuery/commits/master)
[![GitHub last commit](https://img.shields.io/github/last-commit/apkd/RoslynQuery?labelColor=2C3439&color=f97&logoColor=f96&logo=tinder&label=Last%20commit)](https://github.com/apkd/RoslynQuery/commit/HEAD~1)

A local MCP server that exposes IDE-like semantic queries over a C# workspace.

Coding agents these days are remarkably powerful, and often the best way to help them is to *stay out of their way*.
RoslynQuery implements a small set of versatile commands that fill in the gaps in the text-oriented workflow without polluting the context.

# Installation

> Roslyn compiler/workspace libraries are bundled with the app, but real `.sln`, `.slnx`, and `.csproj` loading depends on the machine's installed MSBuild/.NET SDK through `Microsoft.Build.Locator`.

You can either:

- Download the server executable from [the releases page](https://github.com/apkd/RoslynQuery/releases/tag/release), or...
- Build it locally with `dotnet publish`.

To update an installed release binary in place:

```sh
roslynquery -U
```

Now point your editor at the MCP executable:

<details>
  <summary>Codex</summary>

For a basic `stdio` setup, add this to `config.toml`:

##### stdio | Windows (Native)

```toml
[mcp_servers.roslyn]
command = "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe"
cwd = "C:\\src\\RoslynQuery"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

##### stdio | Windows (WSL)

```toml
[mcp_servers.roslyn]
command = "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe"
cwd = "/mnt/c/src/RoslynQuery"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

##### stdio | Linux

```toml
[mcp_servers.roslyn]
command = "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery"
cwd = "/home/you/src/RoslynQuery"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

</details>

<details>
  <summary>Claude Code</summary>

Claude Code adds MCP servers with `claude mcp add`.

##### stdio | Windows (Native)

```bash
claude mcp add --transport stdio roslyn -- C:\src\RoslynQuery\RoslynQuery.Server\publish\win-x64\roslynquery.exe
```

##### stdio | Windows (WSL)

```bash
claude mcp add --transport stdio roslyn -- /mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe
```

##### stdio | Linux

```bash
claude mcp add --transport stdio roslyn -- /home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery
```

</details>

<details>
  <summary>Cursor</summary>

Cursor uses `mcp.json` with a top-level `mcpServers` object. You can put it in `~/.cursor/mcp.json` for a global setup or `.cursor/mcp.json` for a project-local setup.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe"
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe"
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery"
    }
  }
}
```

</details>

<details>
  <summary>Windsurf</summary>

Windsurf stores MCP servers in `~/.codeium/windsurf/mcp_config.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": []
    }
  }
}
```

</details>

<details>
  <summary>Cline</summary>

Cline stores MCP settings in `cline_mcp_settings.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": [],
      "disabled": false
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": [],
      "disabled": false
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": [],
      "disabled": false
    }
  }
}
```

</details>

<details>
  <summary>Continue</summary>

Continue uses YAML, typically as standalone files under `.continue/mcpServers/`

Create `.continue/mcpServers/roslyn.yaml`:

##### stdio | Windows (Native)

```yaml
name: RoslynQuery
version: 0.0.1
schema: v1
mcpServers:
  - name: roslyn
    type: stdio
    command: C:\src\RoslynQuery\RoslynQuery.Server\publish\win-x64\roslynquery.exe
    cwd: C:\src\RoslynQuery
```

##### stdio | Windows (WSL)

```yaml
name: RoslynQuery
version: 0.0.1
schema: v1
mcpServers:
  - name: roslyn
    type: stdio
    command: /mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe
    cwd: /mnt/c/src/RoslynQuery
```

##### stdio | Linux

```yaml
name: RoslynQuery
version: 0.0.1
schema: v1
mcpServers:
  - name: roslyn
    type: stdio
    command: /home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery
    cwd: /home/you/src/RoslynQuery
```

</details>

<details>
  <summary>Gemini CLI</summary>

Gemini CLI stores MCP configuration in `~/.gemini/settings.json` for user scope or `.gemini/settings.json` for project scope.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "cwd": "C:\\src\\RoslynQuery"
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "cwd": "/mnt/c/src/RoslynQuery"
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "cwd": "/home/you/src/RoslynQuery"
    }
  }
}
```

Equivalent CLI commands:

##### Local stdio

```bash
gemini mcp add roslyn /home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery
```

</details>

<details>
  <summary>GitHub Copilot CLI</summary>

GitHub Copilot CLI stores MCP servers in `~/.copilot/mcp-config.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "local",
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "local",
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "local",
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

Interactive alternative:

```text
/mcp add
```

</details>

<details>
  <summary>VS Code / GitHub Copilot Chat</summary>

VS Code uses `mcp.json` with a top-level `servers` object. For workspace scope, put it in `.vscode/mcp.json`; for user scope, open the user MCP configuration from the Command Palette.

##### stdio | Windows (Native)

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": []
    }
  }
}
```

</details>

<details>
  <summary>Antigravity</summary>

You can get to the config file from **Manage MCP Servers → View raw config**.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": [],
      "cwd": "C:\\src\\RoslynQuery"
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": [],
      "cwd": "/mnt/c/src/RoslynQuery"
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": [],
      "cwd": "/home/you/src/RoslynQuery"
    }
  }
}
```

</details>

<details>
  <summary>Zed</summary>

Zed uses `context_servers` in its settings. For project scope, put this in `.zed/settings.json`; for user scope, add it to your user settings.

##### stdio | Windows (Native)

```json
{
  "context_servers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "context_servers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "context_servers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": []
    }
  }
}
```

</details>

<details>
  <summary>Roo Code</summary>

Roo Code stores global MCP configuration in `mcp_settings.json`. For project-local setup, create `.roo/mcp.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": [],
      "cwd": "C:\\src\\RoslynQuery",
      "disabled": false
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": [],
      "cwd": "/mnt/c/src/RoslynQuery",
      "disabled": false
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": [],
      "cwd": "/home/you/src/RoslynQuery",
      "disabled": false
    }
  }
}
```

</details>

<details>
  <summary>JetBrains IDEs / Junie</summary>

Junie in JetBrains IDEs and Junie CLI use the same MCP config file format. Use `~/.junie/mcp/mcp.json` for user scope or `.junie/mcp/mcp.json` in the project root for project scope.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\src\\RoslynQuery\\RoslynQuery.Server\\publish\\win-x64\\roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/mnt/c/src/RoslynQuery/RoslynQuery.Server/publish/win-x64/roslynquery.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/you/src/RoslynQuery/RoslynQuery.Server/publish/linux-x64/roslynquery",
      "args": []
    }
  }
}
```

</details>

# Implemented Tools

## Workspace lifecycle

- `load_workspace`: Opens the solution/project for analysis. Accepts a directory path, `.sln`, `.slnx`, or `.csproj`. Calling it again with the same path reloads the workspace from disk.

To exclude projects from analsis, create a `.roslynqueryignore` file next to the solution file. 
Add one project pattern per line. Patterns support `*` and `?` globs, and negation with `!`. This can improve the initial load performance.

```gitignore
Assembly-CSharp-Editor.csproj
Assembly-CSharp-Editor-firstpass.csproj
*Editor.csproj
*Tests.csproj
Unity.Services*.csproj
Unity.Searcher*.csproj
Unity.Recorder*.csproj
Unity.ProBuilder*.csproj
Unity.Polybrush*.csproj
Unity.Multiplayer.Center*.csproj
Unity.MemoryProfiler*.csproj
```

- `status`: Lists loaded projects.

- `show_diagnostics`: Lists workspace compilation diagnostics on demand, with an optional verbosity parameter.

## Search and analysis

- `describe_symbol`: Returns a set of useful details for a symbol.

- `list_type_members`: Lists members of a type symbol, optionally including inherited members.

- `find_usages`: Finds source references to a resolved symbol across the loaded workspace.

- `find_related_symbols`: Finds related symbols such as base types, implemented interfaces, derived types, implementations, overrides, overridden members, and containing symbols.

- `view_il`: Displays a compact view of a method's compiled IL representation.

# Agent guidance

The tool descriptions themselves should be enough to get you started out-of-the-box.
If you want to, you can also include something like this in your agent instructions:

```md
When working with C# solutions, use the Roslyn MCP tools to search and inspect symbols.
Start with `load_workspace`.
```
