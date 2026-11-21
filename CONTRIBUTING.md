# Contributing to `mssqlcl`

Thank you for your interest in contributing to **mssqlcl**! Whether you are fixing a bug, adding an MCP tool, improving documentation, or proposing an idea, your contributions are welcome.

By participating in this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).

---

## Ways to Contribute

- **Report bugs or request features:** Open an [Issue](https://github.com/codevswia/mssqlcl/issues/new/choose) using the appropriate template.
- **Improve documentation:** Fix typos, clarify setup steps, or add IDE/MCP client guides in `README.md`.
- **Submit code or tests:** Fix bugs, improve T-SQL guard coverage, or add new capabilities via Pull Requests.
- **Report security vulnerabilities:** **Do not** open a public Issue. Follow [SECURITY.md](SECURITY.md) to report privately via GitHub Security Advisories.

---

## Development Setup

### Prerequisites
- **OS:** Windows 10 / 11 (`x64`) — required for Windows DPAPI and Electron `SecretStorage` tests.
- **SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/download) (`10.0.x`).
- **Git:** Standard Git CLI.

### Clone, Build & Test

```powershell
# 1. Fork and clone the repository
git clone https://github.com/<your-username>/mssqlcl.git
cd mssqlcl

# 2. Restore dependencies & build (Release)
dotnet restore
dotnet build --configuration Release

# 3. Run unit tests
dotnet test --configuration Release --verbosity normal
```

To test your local build as a CLI / MCP binary without touching your global release:

```powershell
.\scripts\install.ps1 -FromSource
```

---

## Project Rules & Security Standards

Because `mssqlcl` interacts with SQL Server connections and local IDE credential stores, all PRs must respect these core guarantees:

1. **Zero Secret Leaks:** Never commit credentials, connection strings, `.vscdb` files, DPAPI vaults, API keys, or personal server FQDNs. Never log decrypted passwords to `stdout`, `stderr`, or `%USERPROFILE%\.mssqlcl\audit.log`.
2. **Clean `stdio` Protocol:** When running in `--mcp` mode, `stdout` is strictly reserved for JSON-RPC 2.0 messages. All diagnostics and logs must go to `stderr`.
3. **Preserve `--readonly` Guard:** Read-only mode is enabled by default. Any changes to T-SQL lexical parsing or query execution must include unit tests in `tests/mssqlcl.Tests` verifying DML/DDL/EXEC blocking.
4. **Update Docs When Behavior Changes:** If you add or change a CLI flag, MCP tool, or installation step, update `README.md` accordingly.

---

## Branching, Commits & Pull Request Workflow

1. **Create a branch from `main`** using a clear prefix:
   - `feat/<short-description>` — new features or MCP tools
   - `fix/<short-description>` — bug fixes
   - `docs/<short-description>` — documentation or governance updates
   - `chore/<short-description>` or `ci/<short-description>` — tooling, dependencies, or workflows
2. **Use [Conventional Commits](https://www.conventionalcommits.org/)** for commit messages:
   - Examples: `feat: add support for custom port discovery`, `fix(auth): handle expired MFA token`, `docs: add VS Code walkthrough`.
3. **Verify locally before pushing:**
   - Run `dotnet test --configuration Release` and ensure all tests pass.
4. **Open a Pull Request into `main`:**
   - Fill out the PR template (`.github/PULL_REQUEST_TEMPLATE.md`).
   - Direct pushes to `main` are not allowed. All PRs must pass **CI - Build & Test (`CI Gate`)**, **CodeQL**, and maintainer review per `.github/CODEOWNERS` (`@codevswia/maintainers`, plus `@codevswia/security-deps` for dependency/csproj changes).

---

## License

By contributing to `mssqlcl`, you agree that your contributions will be licensed under the [MIT License](LICENSE).
