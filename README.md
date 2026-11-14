# mssqlcl

**SQL Server MCP Server & CLI** modeled after **Oracle SQLcl (`sql -mcp`)** and compatible with the **VS Code / Antigravity IDE SQL Server (`ms-mssql.mssql`)** connection store.

Configure your SQL Server connections once in VS Code, Antigravity IDE, or via `mssqlcl conn save`, and use them from any external MCP client (Antigravity, Claude Desktop, Claude Code, Cursor, etc.) over `stdio`.

---

## Features

- **Zero-config IDE profile discovery:** Reads `mssql.connections` and `mssql.connectionGroups` (JSONC) from:
  - `%APPDATA%\Antigravity IDE\User\settings.json`
  - `%APPDATA%\Code\User\settings.json`
  - `%USERPROFILE%\.mssqlcl\connections.json` (local fallback)
- **Automatic credential resolution (Windows):**
  - **Windows Auth (`Integrated`):** Connects natively via Windows SSPI.
  - **SQL Login (`SqlLogin`):** Reads `ms-mssql.mssql` secrets from the IDE's Electron `SecretStorage` (`Local State` DPAPI master key + `state.vscdb` AES-256-GCM `v10` payload), with fallback to a local Windows DPAPI vault (`%USERPROFILE%\.mssqlcl\vault.json`) or `MSSQLCL_PWD_<PROFILE>` environment variables.
  - **Microsoft Entra ID / MFA (`AzureMFA`):** Uses `Microsoft.Data.SqlClient.Extensions.Azure` (`ActiveDirectoryInteractive`).
- **11 database MCP tools** (multi-connection via `connectionId`):
  - `mssql_list_servers`
  - `mssql_connect`
  - `mssql_disconnect`
  - `mssql_get_connection_details`
  - `mssql_change_database`
  - `mssql_list_databases`
  - `mssql_list_schemas`
  - `mssql_list_tables`
  - `mssql_list_views`
  - `mssql_list_functions`
  - `mssql_run_query`
- **SQLcl-inspired ergonomics & safety:**
  - Compact **CSV** output by default (optional `json`) to reduce LLM token usage, with `--max-rows` capping (default `500`).
  - **`--readonly` by default:** T-SQL lexical guard skips comments, string literals, and quoted identifiers while blocking DML/DDL/execution keywords (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `ALTER`, `CREATE`, `TRUNCATE`, `EXEC`, `SELECT ... INTO`, etc.).
  - **Local JSONL audit log** at `%USERPROFILE%\.mssqlcl\audit.log` (never creates tables inside target databases and never logs credentials).
  - **Clean `stdio` transport:** All diagnostics go exclusively to `stderr`.

---

## Requirements

- Windows 10 / 11 (DPAPI + Electron `SecretStorage` decryption)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or use `-SelfContained` during installation)

---

## Build, Test & Install

```powershell
# Build & run unit tests
dotnet test

# Install mssqlcl.exe to %LOCALAPPDATA%\Programs\mssqlcl and add to User PATH
.\scripts\install.ps1
```

To build a fully standalone binary that does not require the .NET runtime installed:

```powershell
.\scripts\install.ps1 -SelfContained
```

---

## CLI Usage

```powershell
# List discovered profiles and credential resolution status
mssqlcl conn list

# Test live connectivity for a profile (prefix with antigravity: or vscode: if duplicated)
mssqlcl conn test "MyServer\MyDatabase"
mssqlcl conn test "vscode:MyServer\MyDatabase"

# Create a local profile or store a DPAPI-protected password override
mssqlcl conn save --profile "LocalDev" --server "localhost\SQLEXPRESS" --database "AppDb" --user "sa" --savepwd
mssqlcl conn save --profile "ExistingIdeProfile" --savepwd

# Delete a local profile
mssqlcl conn delete "LocalDev"
```

---

## MCP Client Configuration

Add `mssqlcl` to your MCP client configuration (for example `mcp_config.json` or `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "mssqlcl": {
      "command": "mssqlcl",
      "args": ["--mcp"]
    }
  }
}
```

### Optional MCP Flags

| Flag | Default | Description |
| :--- | :--- | :--- |
| `--readonly` | enabled | Blocks DML/DDL/EXEC statements via lexical T-SQL inspection |
| `--readwrite` | off | Allows write/DDL statements in `mssql_run_query` |
| `--max-rows <N>` | `500` | Caps returned rows per result set |
| `--timeout <sec>` | `30` | Default SQL command timeout in seconds |
| `--source <mode>` | `auto` | `auto`, `antigravity`, `vscode`, or `local` |
| `--no-audit` | off | Disables writing to `%USERPROFILE%\.mssqlcl\audit.log` |

---

## Security Notes

- `mssqlcl` never writes decrypted passwords to disk, `stdout`, `stderr`, or `audit.log`.
- `state.vscdb` is copied to a temporary file in read-only mode before querying and deleted immediately afterwards, so it works safely even while VS Code or Antigravity IDE is open.
- Only run `--readwrite` against databases where write access is explicitly intended.

## Author & Organization

- **Author:** Brayan Raul Sullca Ccarampa
- **Organization:** [codevswia](https://github.com/codevswia) — *Human Ideas. AI-Powered Development.*
- **Repository:** [github.com/codevswia/mssqlcl](https://github.com/codevswia/mssqlcl)

## License

MIT — see [LICENSE](LICENSE).
